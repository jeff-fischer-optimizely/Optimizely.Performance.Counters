#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Reports Monitor lock contention, and how long the waits lasted when there is enough of it to
    /// be worth finding out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate alone does not say whether contention is a problem. A million contentions that each
    /// resolve in a microsecond cost nothing; a hundred that each block for fifty milliseconds are
    /// five seconds of stalled request time. <c>monitor-lock-contention-count</c> reports only the
    /// count, so the two look the same from outside.
    /// </para>
    /// <para>
    /// So the probe is in two halves. The always-on half is a delta of
    /// <see cref="Monitor.LockContentionCount"/> - a counter the runtime already maintains, costing
    /// one read per interval. When that rate crosses the trigger, the probe opens a short capture
    /// burst: an in-process <see cref="EventListener"/> on the runtime's contention keyword, which
    /// yields the wait duration of every contention while it is open. That is genuinely expensive -
    /// one event per contention, at the rate that just triggered it - which is why the window is
    /// seconds long and sits behind a cooldown and an hourly cap.
    /// </para>
    /// <para>
    /// No ETW session and no administrator rights are involved; an in-process listener subscribes
    /// to the runtime's own <c>EventSource</c> directly.
    /// </para>
    /// </remarks>
    public sealed class ContentionProbe : SamplingProbe
    {
        private readonly IMetricTracker _metrics;
        private readonly ContentionProbeOptions _options;

        private long _lastContentionCount;
        private long _lastSampleTimestampTicks;

        private DateTime _lastBurstUtc = DateTime.MinValue;

        // Burst start times inside the rolling hour. Only ever touched from the probe thread.
        private readonly Queue<DateTime> _burstsThisHour = new Queue<DateTime>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ContentionProbe"/> class.
        /// </summary>
        /// <param name="metrics">Where samples are published.</param>
        /// <param name="options">Probe options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        public ContentionProbe(
            IMetricTracker metrics,
            ContentionProbeOptions? options = null,
            ILogger<ContentionProbe>? logger = null)
            : this(metrics, options ?? new ContentionProbeOptions(), (ILogger?)logger)
        {
        }

        private ContentionProbe(IMetricTracker metrics, ContentionProbeOptions options, ILogger? logger)
            : base(
                logger,
                "Optimizely Contention Probe",
                options.Enabled,
                options.SampleInterval,
                options.LogsPerMinute)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options;
        }

        /// <remarks>
        /// Seeds the baseline, so the first interval reports what happened during it rather than
        /// every contention since process start.
        /// </remarks>
        protected override bool OnStarting()
        {
            _lastContentionCount = Monitor.LockContentionCount;
            _lastSampleTimestampTicks = DateTime.UtcNow.Ticks;
            return true;
        }

        /// <inheritdoc />
        protected override void Sample()
        {
            var count = Monitor.LockContentionCount;
            var nowTicks = DateTime.UtcNow.Ticks;

            var contentions = count - _lastContentionCount;
            var elapsedSeconds = (nowTicks - _lastSampleTimestampTicks) / (double)TimeSpan.TicksPerSecond;

            _lastContentionCount = count;
            _lastSampleTimestampTicks = nowTicks;

            if (contentions < 0 || elapsedSeconds <= 0)
            {
                return;
            }

            var perSecond = contentions / elapsedSeconds;

            _metrics.TrackMetric(CounterNames.Runtime.Contention.ContentionsPerSecond, perSecond);

            if (perSecond >= _options.BurstTriggerContentionsPerSecond && MayBurst())
            {
                RunBurst(perSecond);
            }
        }

        /// <summary>
        /// Whether a capture burst is allowed right now.
        /// </summary>
        /// <remarks>
        /// Two limits, because they guard different things. The cooldown stops a single noisy
        /// episode from being captured over and over; the hourly cap stops a site that simply lives
        /// above the trigger threshold from capturing forever.
        /// </remarks>
        private bool MayBurst()
        {
            if (!_options.BurstCaptureEnabled)
            {
                return false;
            }

            var now = DateTime.UtcNow;

            if (now - _lastBurstUtc < _options.BurstCooldown)
            {
                return false;
            }

            while (_burstsThisHour.Count > 0 && now - _burstsThisHour.Peek() >= TimeSpan.FromHours(1))
            {
                _burstsThisHour.Dequeue();
            }

            return _burstsThisHour.Count < _options.MaxBurstsPerHour;
        }

        private void RunBurst(double triggeringRate)
        {
            var startedUtc = DateTime.UtcNow;

            // Recorded before the window opens, so a burst that throws partway still counts against
            // both limits. Failing repeatedly is not a reason to retry more often.
            _lastBurstUtc = startedUtc;
            _burstsThisHour.Enqueue(startedUtc);

            ContentionBurstListener listener;

            try
            {
                listener = new ContentionBurstListener(_options.SampleCap);
            }
            catch (Exception ex)
            {
                TryLog(log => log.LogDebug(ex, "Could not open a contention capture burst."));
                return;
            }

            try
            {
                listener.Begin();
                WaitOrStop(_options.BurstDuration);
            }
            finally
            {
                // Disposal detaches the listener from the runtime source, which is what actually
                // stops the per-contention event traffic. It has to happen even on the stop path.
                listener.Dispose();
            }

            Publish(listener, triggeringRate);
        }

        private void Publish(ContentionBurstListener listener, double triggeringRate)
        {
            var observed = listener.ObservedCount;

            _metrics.TrackMetric(CounterNames.Runtime.Contention.BurstContentions, observed);

            if (observed == 0)
            {
                // The rate said there was contention and the window saw none. Nothing worth
                // publishing percentiles for, and not worth a warning either - a burst can land in
                // a quiet gap.
                return;
            }

            var waits = listener.TakeSortedWaitsMilliseconds();

            if (waits.Length == 0)
            {
                return;
            }

            var p50 = Percentile(waits, 0.50);
            var p95 = Percentile(waits, 0.95);
            var max = waits[waits.Length - 1];

            _metrics.TrackMetric(CounterNames.Runtime.Contention.BurstWaitP50Ms, p50);
            _metrics.TrackMetric(CounterNames.Runtime.Contention.BurstWaitP95Ms, p95);
            _metrics.TrackMetric(CounterNames.Runtime.Contention.BurstWaitMaxMs, max);

            TryLog(log => log.LogWarning(
                "Lock contention reached {Rate:F0} per second, so a {WindowSeconds:F0} s capture " +
                "was taken. It saw {Observed} contentions with a median wait of {P50:F2} ms, a 95th " +
                "percentile of {P95:F2} ms and a longest wait of {Max:F2} ms. Contention itself is " +
                "normal; what costs request time is the waiting, so read the percentiles rather " +
                "than the rate.",
                triggeringRate,
                _options.BurstDuration.TotalSeconds,
                observed,
                p50,
                p95,
                max));
        }

        /// <summary>
        /// Nearest-rank percentile over an ascending array.
        /// </summary>
        private static double Percentile(double[] sorted, double fraction)
        {
            var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;

            if (rank < 0)
            {
                rank = 0;
            }
            else if (rank >= sorted.Length)
            {
                rank = sorted.Length - 1;
            }

            return sorted[rank];
        }

        /// <summary>
        /// Listens to the runtime's contention events for the length of one burst.
        /// </summary>
        private sealed class ContentionBurstListener : EventListener
        {
            private const string RuntimeEventSourceName = "Microsoft-Windows-DotNETRuntime";

            // ContentionKeyword. Narrow on purpose: the runtime source carries GC, JIT, loader and
            // exception events too, and enabling more than this is how an in-process listener turns
            // into a performance problem of its own.
            private const EventKeywords ContentionKeyword = (EventKeywords)0x4000;

            // The event is ContentionStop_V1 on .NET 6 and ContentionStop on .NET 10, so it is
            // matched by prefix rather than by equality.
            private const string ContentionStopPrefix = "ContentionStop";

            private const string DurationPayloadName = "DurationNs";

            private readonly double[] _waits;

            private EventSource? _runtimeSource;
            private int _observed;
            private int _retained;
            private int _enabled;

            internal ContentionBurstListener(int sampleCap)
            {
                _waits = new double[sampleCap];
            }

            /// <summary>Contentions seen during the window, including any not retained.</summary>
            internal int ObservedCount => Volatile.Read(ref _observed);

            /// <summary>
            /// Opens the window.
            /// </summary>
            /// <remarks>
            /// Separate from the constructor because <see cref="OnEventSourceCreated"/> can run
            /// during the base constructor, before this type's fields are assigned. Enabling from
            /// there would read a null array. So construction only records the source, and events
            /// are enabled once the object is whole.
            /// </remarks>
            internal void Begin()
            {
                if (Interlocked.Exchange(ref _enabled, 1) != 0)
                {
                    return;
                }

                var source = Volatile.Read(ref _runtimeSource);

                if (source != null)
                {
                    EnableEvents(source, EventLevel.Informational, ContentionKeyword);
                }
            }

            /// <summary>
            /// Returns the retained waits in ascending order and empties the buffer.
            /// </summary>
            internal double[] TakeSortedWaitsMilliseconds()
            {
                var retained = Math.Min(Volatile.Read(ref _retained), _waits.Length);

                if (retained <= 0)
                {
                    return Array.Empty<double>();
                }

                var copy = new double[retained];
                Array.Copy(_waits, copy, retained);
                Array.Sort(copy);

                return copy;
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (!string.Equals(eventSource.Name, RuntimeEventSourceName, StringComparison.Ordinal))
                {
                    return;
                }

                Volatile.Write(ref _runtimeSource, eventSource);

                // Covers the source appearing after Begin has already run. Before it, Begin does the
                // enabling; the interlocked flag keeps the two from doing it twice.
                if (Volatile.Read(ref _enabled) != 0)
                {
                    EnableEvents(eventSource, EventLevel.Informational, ContentionKeyword);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                var name = eventData.EventName;

                if (name == null || !name.StartsWith(ContentionStopPrefix, StringComparison.Ordinal))
                {
                    return;
                }

                Interlocked.Increment(ref _observed);

                if (!TryReadDurationNanoseconds(eventData, out var nanoseconds))
                {
                    return;
                }

                // Claims a slot rather than appending: this runs on whichever thread lost the race
                // for the lock, so several threads are here at once.
                var slot = Interlocked.Increment(ref _retained) - 1;

                if (slot >= _waits.Length)
                {
                    // Past the cap the count stays exact while the distribution stops growing.
                    // Wound back so a long burst cannot overflow the counter.
                    Interlocked.Exchange(ref _retained, _waits.Length);
                    return;
                }

                _waits[slot] = nanoseconds / 1_000_000.0;
            }

            /// <remarks>
            /// Read by payload name rather than by position. The contention event's payload has
            /// changed shape across runtime versions, and an index that silently moves would report
            /// a flags byte as a duration.
            /// </remarks>
            private static bool TryReadDurationNanoseconds(
                EventWrittenEventArgs eventData,
                out double nanoseconds)
            {
                nanoseconds = 0;

                var names = eventData.PayloadNames;
                var payload = eventData.Payload;

                if (names == null || payload == null)
                {
                    return false;
                }

                var count = Math.Min(names.Count, payload.Count);

                for (var i = 0; i < count; i++)
                {
                    if (!string.Equals(names[i], DurationPayloadName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (payload[i] is double value && value >= 0)
                    {
                        nanoseconds = value;
                        return true;
                    }

                    return false;
                }

                return false;
            }
        }
    }
}
#endif
