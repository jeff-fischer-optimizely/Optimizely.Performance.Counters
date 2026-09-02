using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// What the Optimizely-Performance EventSource actually delivered to a subscriber, read from the
    /// far side of the EventSource boundary.
    /// <para>
    /// This is the layer Application Insights and DataDog see, and it is where names can silently
    /// diverge from <see cref="EventCounterRegistry"/>: both consumers subscribe by exact name, so
    /// a counter published under any other spelling is collected by nobody, and nothing in a build
    /// or an ordinary unit test notices.
    /// </para>
    /// <para>
    /// It reads counters off the wire rather than out of the EventSource's own cache. Reflecting
    /// into the cache used to be enough, because a counter existed only once some decorator had
    /// written to it - so "cached" and "published" meant the same thing. They no longer do: the
    /// EventSource now creates all thirty up front, because a collector that attaches before the
    /// first one exists never gets its polling timer armed and receives nothing at all. Existence
    /// therefore proves nothing, and only a subscriber can tell whether a counter carried anything.
    /// </para>
    /// <para>
    /// Note that this lives only in the test project. The product deliberately ships no
    /// EventListener - it publishes counters and leaves collection to whatever the host already
    /// runs.
    /// </para>
    /// </summary>
    public sealed class PublishedCounterNames : EventListener
    {
        /// <summary>
        /// EventSource reports its own failures through event ID 0 rather than by throwing, so a
        /// misconfigured source looks like a working one to its caller.
        /// </summary>
        private const int EventSourceMessageId = 0;

        /// <summary>
        /// How long to keep driving before giving up. Generous on purpose: an EventCounter reports
        /// on its own schedule, so anything tight here buys a flaky test rather than a fast one. In
        /// practice the observation settles in about three seconds.
        /// </summary>
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long the observed set must stop growing before it is taken as complete. Two seconds,
        /// against a one second polling interval, so a counter that arrives one tick later than the
        /// rest is not mistaken for one that never arrives.
        /// </summary>
        private static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);

        private static readonly TimeSpan DrivePause = TimeSpan.FromMilliseconds(200);

        private readonly Dictionary<string, double> _values = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly List<string> _errors = new List<string>();
        private readonly HashSet<string> _reporting = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        private PublishedCounterNames()
        {
        }

        /// <summary>
        /// Attaches a subscriber and drives nothing, so what arrives is whatever the EventSource
        /// reports on its own.
        /// <para>
        /// This is the regression guard on counters being created up front. Before that, a
        /// subscriber attaching while no counter existed never had its polling timer armed and
        /// received nothing ever again; now every counter reports from the moment the EventSource is
        /// constructed, quiet ones included, so a subscriber gets ticks whatever order it attaches
        /// in. Meaningless on net472, which has no polling at all.
        /// </para>
        /// </summary>
        /// <returns>Every counter that reported, whether or not it carried a measurement.</returns>
        public static Result ObserveIdle() => Observe(() => { });

        /// <summary>
        /// Runs <paramref name="drive"/> with a subscriber attached and reports what reached it.
        /// </summary>
        /// <param name="drive">
        /// Work that exercises the decorators. Called repeatedly, not once: an EventCounter reports
        /// an aggregate per interval, so a single write early on is averaged into an interval that
        /// has already elapsed by the time anything is listening.
        /// </param>
        /// <returns>The counters that carried a measurement, plus any self-reported failures.</returns>
        public static Result Observe(Action drive)
        {
            // Touch the singleton before listening. An EventSource that has not been constructed
            // cannot be enabled, and enabling is what makes IsEnabled() true - without it the
            // tracker short-circuits and publishes nothing.
            var source = OptimizelyPerformanceEventSource.Instance;

            using var listener = new PublishedCounterNames();
            listener.Subscribe(source);

            var deadline = DateTime.UtcNow + Budget;
            var lastChange = DateTime.UtcNow;
            var observed = 0;

            do
            {
                drive();
                Thread.Sleep(DrivePause);

                // Both sets, so a run that drives nothing still settles on the reporting counters
                // rather than sitting out the whole budget waiting for a measurement.
                var current = listener.Progress();
                if (current != observed)
                {
                    observed = current;
                    lastChange = DateTime.UtcNow;
                }
            }
            while ((observed == 0 || DateTime.UtcNow - lastChange < Settled) && DateTime.UtcNow < deadline);

            lock (listener._lock)
            {
                return new Result(
                    new Dictionary<string, double>(listener._values, StringComparer.Ordinal),
                    new List<string>(listener._reporting),
                    new List<string>(listener._errors));
            }
        }

        private void Subscribe(OptimizelyPerformanceEventSource source)
        {
#if NET6_0_OR_GREATER
            // One second, the shortest interval worth asking for. Without EventCounterIntervalSec
            // no counter is published to this listener at all: EventCounter reads its absence as
            // "this subscriber does not want counters".
            EnableEvents(
                source,
                EventLevel.Verbose,
                EventKeywords.All,
                new Dictionary<string, string?> { { "EventCounterIntervalSec", "1" } });
#else
            // net472 writes each measurement as a raw event as it happens, so there is no interval
            // to ask for.
            EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
#endif
        }

        /// <summary>
        /// Separates the EventSource's own error reports from counter payloads.
        /// </summary>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload == null || eventData.Payload.Count == 0)
            {
                return;
            }

            if (eventData.EventId == EventSourceMessageId)
            {
                if (eventData.Payload[0] is string message)
                {
                    lock (_lock)
                    {
                        _errors.Add(message);
                    }
                }

                return;
            }

#if NET6_0_OR_GREATER
            foreach (var item in eventData.Payload)
            {
                // The counter arrives as the aggregate the EventCounter computed over the interval:
                // Name, Count, Mean, Min, Max and the rest.
                if (!(item is IDictionary<string, object> counter) ||
                    !counter.TryGetValue("Name", out var rawName) ||
                    !(rawName is string name) ||
                    !counter.TryGetValue("Count", out var rawCount) ||
                    !counter.TryGetValue("Mean", out var rawMean))
                {
                    continue;
                }

                // Count, not Mean. Every counter now exists from the moment the EventSource is
                // constructed, so all thirty report on every tick whether or not anything wrote to
                // them; a quiet one reports Count 0. Count is what separates "some decorator
                // published this" from "this counter merely exists", which is the distinction the
                // name-coverage tests are about. ItemsLoaded legitimately carries the value zero.
                lock (_lock)
                {
                    _reporting.Add(name);
                }

                if (Convert.ToDouble(rawCount, CultureInfo.InvariantCulture) > 0)
                {
                    Record(name, Convert.ToDouble(rawMean, CultureInfo.InvariantCulture));
                }
            }
#else
            // net472 has no EventCounter polling. Each measurement goes out as a raw event carrying
            // the name and then the value, and arrives the moment it is written.
            if (eventData.Payload.Count >= 2 &&
                eventData.Payload[0] is string rawName &&
                eventData.Payload[1] is double value)
            {
                Record(rawName, value);
            }
#endif
        }

        private void Record(string name, double value)
        {
            lock (_lock)
            {
                _reporting.Add(name);

                // Keep the largest rather than the latest, so a lull cannot overwrite a good
                // measurement with the zero from a quiet interval.
                if (!_values.TryGetValue(name, out var existing) || value > existing)
                {
                    _values[name] = value;
                }
            }
        }

        private int Progress()
        {
            lock (_lock)
            {
                return _reporting.Count + _values.Count;
            }
        }

        /// <summary>
        /// The outcome of one <see cref="Observe"/> run.
        /// </summary>
        public sealed class Result
        {
            private readonly IReadOnlyDictionary<string, double> _values;

            internal Result(
                IReadOnlyDictionary<string, double> values,
                IReadOnlyCollection<string> reporting,
                IReadOnlyCollection<string> errors)
            {
                _values = values;
                Reporting = reporting;
                Errors = errors;
            }

            /// <summary>Counter names that carried at least one measurement to the subscriber.</summary>
            public IReadOnlyCollection<string> Names => _values.Keys.ToList();

            /// <summary>
            /// Counter names that reached the subscriber at all, including those reporting an empty
            /// interval. Wider than <see cref="Names"/>, and the difference is the point: it is what
            /// distinguishes a counter nothing wrote to from a counter nobody can see.
            /// </summary>
            public IReadOnlyCollection<string> Reporting { get; }

            /// <summary>
            /// Failures the EventSource reported about itself. Any entry here means the source is
            /// in an error state and is publishing nothing, however healthy it looks to callers.
            /// </summary>
            public IReadOnlyCollection<string> Errors { get; }

            /// <summary>
            /// The largest value seen for a counter, or null if it never reached the subscriber.
            /// </summary>
            /// <param name="name">Counter name.</param>
            public double? ValueOf(string name) => _values.TryGetValue(name, out var value) ? value : (double?)null;
        }
    }
}
