using System;
#if NET6_0_OR_GREATER
using System.Collections.Concurrent;
#endif
using System.Diagnostics.Tracing;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Metric tracker that publishes to .NET EventCounters.
    /// These are automatically consumed by:
    /// - Application Insights (via EventCounterCollectionModule)
    /// - DataDog (.NET tracer with EventCounter support)
    /// - dotnet-counters CLI tool
    /// - Any EventListener
    /// <para>
    /// EventCounters have no notion of dimensions, so the dimensioned overloads publish under the
    /// plain counter name and drop the dimensions. Encoding them into the name instead - as
    /// <c>Name[Operation=Get]</c> - produced a counter name nothing was subscribed to, because
    /// Application Insights matches the names in <see cref="EventCounterRegistry"/> exactly. The
    /// dimensions are still passed to <see cref="IMetricTracker"/>, so a tracker built on a backend
    /// that supports them can use them without the decorators changing.
    /// </para>
    /// </summary>
    public class EventCounterMetricTracker : IMetricTracker
    {
        private readonly OptimizelyPerformanceEventSource _eventSource;

        /// <summary>
        /// Binds the tracker to the process-wide <see cref="OptimizelyPerformanceEventSource"/>.
        /// </summary>
        public EventCounterMetricTracker() => _eventSource = OptimizelyPerformanceEventSource.Instance;

        /// <inheritdoc />
        public bool IsEnabled => _eventSource.IsEnabled();

        /// <inheritdoc />
        public void TrackMetric(string name, double value) => _eventSource.TrackMetric(name, value);

        /// <inheritdoc />
        public void TrackMetric(string name, double value, string dimension1Name, string dimension1Value) =>
            TrackMetric(name, value);

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value) =>
            TrackMetric(name, value);

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value,
            string dimension3Name, string dimension3Value) =>
            TrackMetric(name, value);
    }

    /// <summary>
    /// EventSource for Optimizely performance metrics.
    /// This is the integration point with Application Insights, DataDog, and other telemetry systems.
    /// </summary>
    [EventSource(Name = CounterNames.EventSourceName)]
    public sealed class OptimizelyPerformanceEventSource : EventSource
    {
        /// <summary>
        /// The single instance every tracker writes through. An EventSource name may only be
        /// registered once per process, so this type is deliberately not constructible.
        /// </summary>
        public static readonly OptimizelyPerformanceEventSource Instance = new OptimizelyPerformanceEventSource();

#if NET6_0_OR_GREATER
        // Concurrent, not a Dictionary under a lock. This is written exactly once per counter name
        // - thirty times over the life of the process - and read on every single measurement, so a
        // lock here is a process-wide serialization point in the middle of content loading. A page
        // rendering 380 content loads emits roughly 950 measurements, and every one of them would
        // queue on the same monitor.
        //
        // This does not make writes lock free: EventCounter.WriteMetric takes its own lock on the
        // counter. It splits one global lock into thirty per-counter ones, so threads contend only
        // when they hit the same counter.
        private readonly ConcurrentDictionary<string, System.Diagnostics.Tracing.EventCounter> _counters =
            new ConcurrentDictionary<string, System.Diagnostics.Tracing.EventCounter>(StringComparer.Ordinal);
#endif

        private OptimizelyPerformanceEventSource() : base(EventSourceSettings.EtwSelfDescribingEventFormat)
        {
        }

        /// <summary>
        /// Tracks a metric value. Creates EventCounter on first use for each metric name.
        /// </summary>
        /// <remarks>
        /// <see cref="NonEventAttribute"/> is required, not decorative. EventSource treats every
        /// declared instance method as an event method unless told otherwise, and auto-assigns IDs
        /// in declaration order starting at 1 - so without this, <c>TrackMetric</c> takes ID 1 and
        /// collides with <c>WriteMetricEvent</c> below. On .NET Framework that collision
        /// puts the whole EventSource into a permanent error state at construction: no exception
        /// reaches the caller, and every counter silently disappears.
        /// </remarks>
        [NonEvent]
        internal void TrackMetric(string name, double value)
        {
            if (!IsEnabled())
                return;

#if NET6_0_OR_GREATER
            if (!_counters.TryGetValue(name, out var counter))
            {
                counter = GetOrCreateCounter(name);
            }

            counter.WriteMetric(value);
#else
            // .NET Framework 4.7.2: Use WriteEvent to emit raw events
            // Application Insights will collect these via EventSource integration
            WriteMetricEvent(name, value);
#endif
        }

#if NETFRAMEWORK
        /// <summary>
        /// Writes one measurement as a raw ETW event carrying (name, value).
        /// </summary>
        /// <remarks>
        /// WriteEventCore rather than the obvious <c>WriteEvent(1, name, value)</c>. EventSource has
        /// fast overloads for (string, int) and (string, long) but none for (string, double), so
        /// that call binds to <c>WriteEvent(int, params object[])</c> - which allocates an object[2]
        /// and boxes the double on every measurement, then walks the arguments reflectively. On
        /// this target framework that is the only wire format there is, so it is the whole hot path.
        /// <para>
        /// The payload is unchanged. EventSource reads the shape from this method's own signature,
        /// so a listener still decodes (string, double) exactly as before. The string size includes
        /// its null terminator, which is what EventSource expects of a string field.
        /// </para>
        /// </remarks>
        [Event(1, Level = EventLevel.Informational)]
        private unsafe void WriteMetricEvent(string name, double value)
        {
            // fixed on a null string yields a null pointer, which the old params-object[] overload
            // would have turned into an empty string rather than a malformed payload. No caller
            // passes null - the names are constants - but the buffer arithmetic below is not the
            // place to find out otherwise.
            var text = name ?? string.Empty;

            fixed (char* pName = text)
            {
                var data = stackalloc EventData[2];

                data[0].DataPointer = (IntPtr)pName;
                data[0].Size = checked((text.Length + 1) * sizeof(char));

                data[1].DataPointer = (IntPtr)(&value);
                data[1].Size = sizeof(double);

                WriteEventCore(1, 2, data);
            }
        }
#endif

#if NET6_0_OR_GREATER
        /// <summary>
        /// The cold half of <see cref="TrackMetric"/>, kept out of it so the hot path is a lookup
        /// and a write.
        /// </summary>
        /// <remarks>
        /// Not GetOrAdd with a factory: ConcurrentDictionary may run a factory more than once under
        /// contention, and constructing an EventCounter is not free of side effects - it publishes
        /// itself to this EventSource. A losing racer's counter would stay registered and report
        /// alongside the winner. So the loser is disposed, which unregisters it.
        /// </remarks>
        [NonEvent]
        private System.Diagnostics.Tracing.EventCounter GetOrCreateCounter(string name)
        {
            var created = new System.Diagnostics.Tracing.EventCounter(name, this);
            var winner = _counters.GetOrAdd(name, created);

            if (!ReferenceEquals(winner, created))
            {
                created.Dispose();
            }

            return winner;
        }
#endif

        /// <summary>
        /// Disposes the lazily created counters before handing off to the base EventSource.
        /// </summary>
        /// <param name="disposing">True when called from <see cref="IDisposable.Dispose"/>.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
#if NET6_0_OR_GREATER
                foreach (var counter in _counters.Values)
                {
                    counter.Dispose();
                }

                _counters.Clear();
#endif
            }

            base.Dispose(disposing);
        }
    }
}
