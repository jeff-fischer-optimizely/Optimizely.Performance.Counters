using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, System.Diagnostics.Tracing.EventCounter> _counters = new Dictionary<string, System.Diagnostics.Tracing.EventCounter>();
#endif
        private readonly object _lock = new object();

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
            lock (_lock)
            {
                if (!_counters.TryGetValue(name, out var counter))
                {
                    counter = new System.Diagnostics.Tracing.EventCounter(name, this);
                    _counters[name] = counter;
                }

                counter.WriteMetric(value);
            }
#else
            // .NET Framework 4.7.2: Use WriteEvent to emit raw events
            // Application Insights will collect these via EventSource integration
            WriteMetricEvent(name, value);
#endif
        }

#if NETFRAMEWORK
        [Event(1, Level = EventLevel.Informational)]
        private void WriteMetricEvent(string name, double value) => WriteEvent(1, name, value);
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
                lock (_lock)
                {
                    foreach (var counter in _counters.Values)
                    {
                        counter.Dispose();
                    }
                    _counters.Clear();
                }
#endif
            }

            base.Dispose(disposing);
        }
    }
}
