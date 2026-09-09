using System.Collections.Generic;
using System.Linq;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Http;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Everything that publishes a counter, driven through enough of its surface to emit each one.
    /// <para>
    /// There are two kinds of publisher and they are driven completely differently - a decorator
    /// emits because something called it, a probe because it went and looked - so the drives live in
    /// <see cref="DecoratorSweep"/> and <see cref="ProbeSweep"/> respectively. This composes them,
    /// because the registry invariants are about the whole published set and a test that checked
    /// only one half would pass while the other half charted empty.
    /// </para>
    /// </summary>
    public static class EmitterSweep
    {
        /// <summary>
        /// Registered counters the sweep cannot produce. See each half for the reasons.
        /// </summary>
        public static readonly IReadOnlyCollection<string> OutOfReach =
            DecoratorSweep.OutOfReach.Concat(ProbeSweep.OutOfReach).ToList();

        // The probe half manipulates process-wide state - it forces collections and generates lock
        // contention - so two sweeps running at once corrupt each other's readings rather than
        // merely slowing each other down. One sweep's forced gen 2 landing between the other's
        // Collect(0) and its sample makes the gen 0 counter go missing, which presents as an
        // occasional failure in whichever coverage test lost the race.
        private static readonly object Gate = new object();

        /// <summary>
        /// Exercises every decorator and every probe, reporting through the supplied tracker.
        /// </summary>
        /// <param name="tracker">Sink the emitters are constructed with.</param>
        public static void DriveEverything(IMetricTracker tracker)
        {
            lock (Gate)
            {
                DecoratorSweep.DriveEverything(tracker);
                ProbeSweep.DriveEverything(tracker);
                DriveLogWriteRate(tracker);
                DriveHttpCacheability(tracker);
                DriveProcessUptime(tracker);
            }
        }

        /// <remarks>
        /// Neither a decorator nor a probe, so it belongs to neither half: nothing calls the
        /// recorder through an Optimizely interface, and it never goes looking either. It counts
        /// what the host's logging pipeline hands it, so driving it means handing it writes - which
        /// is exactly what the logging provider on V12 and V13, and the log4net appender on V11, do
        /// with an event apiece. Those two sinks are covered separately; this covers the counters.
        /// </remarks>
        private static void DriveLogWriteRate(IMetricTracker tracker)
        {
            using var recorder = new LogWriteRateRecorder(tracker);

            recorder.Record(LogWriteSeverity.Normal);
            recorder.Record(LogWriteSeverity.Warning);
            recorder.Record(LogWriteSeverity.Error);

            MetricFlush.Run(recorder);
        }

        /// <remarks>
        /// Neither a decorator nor a probe either, and driven the same way as the log write rate:
        /// by handing the recorder what its sinks would hand it. One response of each kind, because
        /// the five shares are only published when the interval had traffic behind it and a sweep
        /// that recorded nothing would leave six of the nine counters unemitted - which is exactly
        /// what the coverage test is looking for.
        /// </remarks>
        private static void DriveHttpCacheability(IMetricTracker tracker)
        {
            using var recorder = new HttpCacheabilityRecorder(tracker);

            // Shared-cacheable and sets a cookie, so this one response covers the freshness counter
            // and the conflict share as well as its own bucket.
            Record(recorder, "public, max-age=600", hasValidator: true, hasSetCookie: true);
            Record(recorder, "private, max-age=60");
            Record(recorder, "no-cache");
            Record(recorder, "no-store");
            Record(recorder, cacheControl: null);

            MetricFlush.Run(recorder);
        }

        /// <remarks>
        /// The third of the reporters that is neither a decorator nor a probe, and the only one
        /// that needs nothing handed to it: it reads a clock. Driving it is a flush and nothing
        /// else.
        /// </remarks>
        private static void DriveProcessUptime(IMetricTracker tracker)
        {
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);
        }

        private static void Record(
            HttpCacheabilityRecorder recorder,
            string? cacheControl,
            bool hasValidator = false,
            bool hasSetCookie = false) =>
            recorder.Record(
                ResponseCacheSummary.Describe(cacheControl, null, hasValidator, hasSetCookie));
    }
}
