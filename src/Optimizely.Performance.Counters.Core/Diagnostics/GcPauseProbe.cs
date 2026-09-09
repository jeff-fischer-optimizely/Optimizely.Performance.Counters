#if NET6_0_OR_GREATER
using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Reports how long the garbage collector stopped the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>time-in-gc</c> is a percentage, and a percentage cannot distinguish the two cases that
    /// matter. Ten percent spread over a thousand sub-millisecond gen 0 collections costs no
    /// request anything measurable. The same ten percent delivered as three two-second gen 2 pauses
    /// stops every thread in the process three times, and the requests unlucky enough to be in
    /// flight see their duration jump by two seconds for reasons that appear nowhere in their own
    /// telemetry. This probe reports the pause durations themselves, so the distribution is visible
    /// rather than averaged away.
    /// </para>
    /// <para>
    /// No ETW session is involved and nothing needs enabling. The runtime already records this;
    /// <see cref="GC.GetGCMemoryInfo()"/> reads it back, and the call does not itself provoke a
    /// collection.
    /// </para>
    /// <para>
    /// One honest limitation. <see cref="GC.GetGCMemoryInfo()"/> describes only the most recent
    /// collection, so polling samples individual pauses rather than counting all of them - under a
    /// heavy gen 0 rate most are never seen. The probe tracks the collection index so it at least
    /// never reports the same pause twice, which would otherwise skew the distribution towards
    /// whatever was happening when it happened to look. On .NET 8 and later
    /// <c>GC.GetTotalPauseDuration()</c> closes the gap for the aggregate: the per-interval total is
    /// exact even though the individual pauses behind it are sampled.
    /// </para>
    /// </remarks>
    public sealed class GcPauseProbe : SamplingProbe
    {
        private readonly IMetricTracker _metrics;
        private readonly GcPauseProbeOptions _options;

        // The last collection reported, so a quiet period does not re-report it every interval.
        private long _lastGcIndex;

#if NET8_0_OR_GREATER
        private TimeSpan _lastTotalPause;
        private long _lastTotalPauseTimestamp;
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="GcPauseProbe"/> class.
        /// </summary>
        /// <param name="metrics">Where samples are published.</param>
        /// <param name="options">Probe options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        public GcPauseProbe(
            IMetricTracker metrics,
            GcPauseProbeOptions? options = null,
            ILogger<GcPauseProbe>? logger = null)
            : this(metrics, options ?? new GcPauseProbeOptions(), (ILogger?)logger)
        {
        }

        private GcPauseProbe(IMetricTracker metrics, GcPauseProbeOptions options, ILogger? logger)
            : base(
                logger,
                "Optimizely GC Pause Probe",
                options.Enabled,
                options.SampleInterval,
                options.LogsPerMinute)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options;
        }

        /// <remarks>
        /// Seeds the baselines so the opening interval reports the pauses that happened during it,
        /// rather than every collection since process start arriving as one implausible spike.
        /// </remarks>
        protected override bool OnStarting()
        {
            try
            {
                _lastGcIndex = GC.GetGCMemoryInfo().Index;
#if NET8_0_OR_GREATER
                _lastTotalPause = GC.GetTotalPauseDuration();
                _lastTotalPauseTimestamp = Stopwatch.GetTimestamp();
#endif
            }
            catch
            {
                // Sampling will retry; a failed seed is not worth refusing to start over.
            }

            return true;
        }

        /// <inheritdoc />
        protected override void Sample()
        {
            var info = GC.GetGCMemoryInfo();

            RecordIntervalTotal();

            // Index is monotonic. Equal means no collection has completed since the last look, and
            // the durations on hand are ones already reported.
            if (info.Index == _lastGcIndex)
            {
                return;
            }

            _lastGcIndex = info.Index;

            _metrics.TrackMetric(
                CounterNames.Runtime.GarbageCollection.PauseTimePercent,
                info.PauseTimePercentage);

            // Generation is baked into the counter name rather than carried as a dimension:
            // EventCounters have none, and gen 0 and gen 2 pauses differ by orders of magnitude.
            var counter = CounterForGeneration(info.Generation, info.Concurrent);

            // A background gen 2 collection reports two pauses - the suspensions at either end of
            // the concurrent phase - and they are separate stop-the-world events, so they are
            // reported separately rather than summed.
            var longestMilliseconds = 0.0;

            foreach (var pause in info.PauseDurations)
            {
                var milliseconds = pause.TotalMilliseconds;
                if (milliseconds <= 0)
                {
                    continue;
                }

                _metrics.TrackMetric(counter, milliseconds);

                if (milliseconds > longestMilliseconds)
                {
                    longestMilliseconds = milliseconds;
                }
            }

            if (_options.SlowPauseThresholdMilliseconds > 0
                && longestMilliseconds >= _options.SlowPauseThresholdMilliseconds)
            {
                var generation = info.Generation;
                var concurrent = info.Concurrent;

                TryLog(log => log.LogWarning(
                    "A generation {Generation} garbage collection{Background} paused the process " +
                    "for {PauseMs:F0} ms. Every thread was stopped for that time, so any request in " +
                    "flight took at least this much longer for reasons that will not appear " +
                    "anywhere in its own telemetry. Sustained gen 2 pauses usually mean the heap is " +
                    "growing, fragmenting, or being churned faster than the collector can keep up " +
                    "with in the background.",
                    generation,
                    concurrent ? " (background)" : string.Empty,
                    longestMilliseconds));
            }
        }

        /// <remarks>
        /// Only available on .NET 8 and later. On .NET 6 the aggregate has to be inferred from the
        /// sampled durations, which understates it whenever collections outpace the sampling
        /// interval, so it is not reported there rather than reported wrongly.
        /// </remarks>
        private void RecordIntervalTotal()
        {
#if NET8_0_OR_GREATER
            var total = GC.GetTotalPauseDuration();
            var now = Stopwatch.GetTimestamp();

            var pausedMilliseconds = (total - _lastTotalPause).TotalMilliseconds;
            var elapsedMilliseconds = (now - _lastTotalPauseTimestamp) * 1000.0 / Stopwatch.Frequency;

            _lastTotalPause = total;
            _lastTotalPauseTimestamp = now;

            if (pausedMilliseconds < 0 || elapsedMilliseconds <= 0)
            {
                // Cumulative totals do not go backwards; if one appears to, the reading is not one
                // to publish.
                return;
            }

            _metrics.TrackMetric(
                CounterNames.Runtime.GarbageCollection.IntervalPauseMs,
                pausedMilliseconds);

            _metrics.TrackMetric(
                CounterNames.Runtime.GarbageCollection.PauseDutyCyclePercent,
                pausedMilliseconds / elapsedMilliseconds * 100.0);
#endif
        }

        /// <summary>
        /// Picks the counter for a collection's generation and mode.
        /// </summary>
        /// <remarks>
        /// Background gen 2 gets its own counter because it is the benign case: the process is only
        /// suspended at the edges of the concurrent phase. Charted together with a blocking gen 2,
        /// an ordinary background collection looks like a stall.
        /// </remarks>
        private static string CounterForGeneration(int generation, bool concurrent)
        {
            switch (generation)
            {
                case 0:
                    return CounterNames.Runtime.GarbageCollection.Gen0PauseMs;
                case 1:
                    return CounterNames.Runtime.GarbageCollection.Gen1PauseMs;
                default:
                    return concurrent
                        ? CounterNames.Runtime.GarbageCollection.Gen2BackgroundPauseMs
                        : CounterNames.Runtime.GarbageCollection.Gen2PauseMs;
            }
        }
    }
}
#endif
