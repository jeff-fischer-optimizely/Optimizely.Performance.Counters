using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Reflection;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// What the Optimizely-Performance EventSource actually published, read back from the far side
    /// of the EventSource boundary.
    /// <para>
    /// This is the layer Application Insights and DataDog see, and it is where names can silently
    /// diverge from <see cref="EventCounterRegistry"/>: both consumers subscribe by exact name, so
    /// a counter published under any other spelling is collected by nobody, and nothing in a build
    /// or an ordinary unit test notices.
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

        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _errors = new List<string>();
        private readonly object _lock = new object();

        private PublishedCounterNames()
        {
        }

        /// <summary>
        /// Runs <paramref name="drive"/> with the EventSource enabled and reports what came out.
        /// </summary>
        /// <param name="drive">Work that exercises the decorators.</param>
        /// <returns>The published counter names, plus any self-reported EventSource failures.</returns>
        public static Result Observe(Action drive)
        {
            // Touch the singleton before listening. An EventSource that has not been constructed
            // cannot be enabled, and enabling is what makes IsEnabled() true - without it the
            // tracker short-circuits and publishes nothing.
            var source = OptimizelyPerformanceEventSource.Instance;

            using var listener = new PublishedCounterNames();
            listener.EnableEvents(source, EventLevel.Verbose, EventKeywords.All);

            drive();

            lock (listener._lock)
            {
                return new Result(listener.CollectNames(source), new List<string>(listener._errors));
            }
        }

        /// <summary>
        /// Separates the EventSource's own error reports from metric events. On .NET Framework the
        /// metric events are also the only place counter names surface, since that build writes
        /// each metric as a raw event whose first payload value is the name.
        /// </summary>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.Payload == null || eventData.Payload.Count == 0)
            {
                return;
            }

            if (!(eventData.Payload[0] is string first))
            {
                return;
            }

            lock (_lock)
            {
                if (eventData.EventId == EventSourceMessageId)
                {
                    _errors.Add(first);
                }
                else
                {
                    _names.Add(first);
                }
            }
        }

        private IReadOnlyCollection<string> CollectNames(OptimizelyPerformanceEventSource source)
        {
#if NETFRAMEWORK
            _ = source;
            return new List<string>(_names);
#else
            // On .NET 6+ each metric becomes an EventCounter, and an EventCounter only writes to
            // listeners when its polling interval elapses. Waiting for that would make this test
            // both slow and timing-dependent, so the names are read from the counters the source
            // created instead - the same string it passed to the EventCounter constructor, and
            // therefore the same string a consumer must subscribe to.
            const string CountersField = "_counters";

            var field = typeof(OptimizelyPerformanceEventSource)
                .GetField(CountersField, BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(OptimizelyPerformanceEventSource)} has no {CountersField} field. " +
                    "If the counter cache was renamed, update PublishedCounterNames to match.");
            }

            // Read through IReadOnlyDictionary rather than the concrete type, so swapping the cache
            // implementation again does not turn this into an InvalidCastException.
            var counters = (IReadOnlyDictionary<string, EventCounter>?)field.GetValue(source);

            // No synchronization beyond the lock already held: the drive has returned, and the
            // reporting timers behind the cache and event-publisher decorators run on a 60 second
            // interval, so nothing else in the process is writing by the time this runs.
            return counters == null ? Array.Empty<string>() : new List<string>(counters.Keys);
#endif
        }

        /// <summary>
        /// The outcome of one <see cref="Observe"/> run.
        /// </summary>
        public sealed class Result
        {
            internal Result(IReadOnlyCollection<string> names, IReadOnlyCollection<string> errors)
            {
                Names = names;
                Errors = errors;
            }

            /// <summary>Distinct counter names the EventSource published.</summary>
            public IReadOnlyCollection<string> Names { get; }

            /// <summary>
            /// Failures the EventSource reported about itself. Any entry here means the source is
            /// in an error state and is publishing nothing, however healthy it looks to callers.
            /// </summary>
            public IReadOnlyCollection<string> Errors { get; }
        }
    }
}
