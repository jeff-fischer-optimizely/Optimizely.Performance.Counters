using System.Linq;
using System.Threading;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.Runtime.Process;

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// Covers the process uptime counter, which is a gauge in a package where nearly everything
    /// else is a rate - and the two have opposite flush semantics.
    /// </summary>
    public class ProcessUptimeTests
    {
        [Fact]
        public void The_flush_reports_the_uptime()
        {
            var tracker = new RecordingMetricTracker();
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);

            var reported = tracker.Metrics.Single();

            Assert.Equal(Names.UptimeSeconds, reported.Name);
            Assert.True(reported.Value >= 0.0, $"Uptime read as {reported.Value}.");
        }

        [Fact]
        public void The_uptime_is_the_process_age_rather_than_the_reporter_age()
        {
            var tracker = new RecordingMetricTracker();

            // Constructed now, so a reporter that timed itself would report a number too small to
            // measure. The test host has been up since assembly load and discovery, which is
            // comfortably longer than this.
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);

            // Fails rather than skips if the host refused to give up its start time, because the
            // fallback path silently reporting reporter age is exactly the regression worth
            // hearing about.
            Assert.True(
                ValueOf(tracker) > 0.5,
                $"Uptime read as {ValueOf(tracker)} seconds, which is the reporter's own age " +
                "rather than the process's.");
        }

        [Fact]
        public void The_uptime_climbs_across_flushes_instead_of_resetting()
        {
            var tracker = new RecordingMetricTracker();
            using var reporter = new ProcessUptimeReporter(tracker);

            MetricFlush.Run(reporter);
            var first = ValueOf(tracker);

            Thread.Sleep(50);

            MetricFlush.Run(reporter);
            var second = ValueOf(tracker);

            // A gauge, not a rate. Every other reporter in this package exchanges its accumulators
            // to zero on flush, and doing that here would turn the counter into a sawtooth with a
            // sixty second period - which is what it is supposed to distinguish a restart from.
            Assert.True(second > first, $"Second reading {second} did not exceed the first {first}.");
        }

        [Fact]
        public void A_reporter_will_not_take_a_null_tracker()
        {
            // Fails where the probes do, at construction in the initialization module, rather
            // than once a minute on a timer thread that has nowhere to report it.
            Assert.Throws<System.ArgumentNullException>(() => new ProcessUptimeReporter(null!));
        }

        private static double ValueOf(RecordingMetricTracker tracker) =>
            tracker.Metrics.Last(m => m.Name == Names.UptimeSeconds).Value;
    }
}
