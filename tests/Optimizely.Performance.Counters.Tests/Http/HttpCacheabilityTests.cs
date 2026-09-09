using System;
using System.Linq;
using Optimizely.Performance.Counters.Core.Http;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.Runtime.Http;

namespace Optimizely.Performance.Counters.Tests.Http
{
    /// <summary>
    /// The counting half of the response cacheability feature: what the recorder publishes for a
    /// minute's worth of responses, and what the monitor does with the one recorder a process is
    /// allowed to have.
    /// <para>
    /// The classification that feeds it is covered by <see cref="ResponseCacheSummaryTests"/>, and
    /// the sinks that call it differ by major - an <c>IHttpModule</c> on V11, middleware on V12 and
    /// V13 - so they are covered separately.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Not run in parallel with anything else: <see cref="HttpCacheabilityMonitor"/> is process-wide
    /// static state, so a test that starts it while another has one running would see the other's
    /// recorder.
    /// </remarks>
    [Collection(HttpCacheabilityCollection.Name)]
    public class HttpCacheabilityTests : IDisposable
    {
        public void Dispose() => HttpCacheabilityMonitor.Stop();

        [Fact]
        public void A_quiet_interval_publishes_the_response_rate_and_nothing_else()
        {
            // The one counter that means something with no traffic behind it. The shares would all
            // read nought percent, and a chart claiming nothing was cacheable during the quiet hours
            // is worse than a gap - the gap is unambiguous next to a response rate of zero.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            MetricFlush.Run(recorder);

            var metric = Assert.Single(tracker.Metrics);
            Assert.Equal(Names.ResponsesPerSecond, metric.Name);
            Assert.Equal(0d, metric.Value);
        }

        [Fact]
        public void A_busy_interval_publishes_every_counter()
        {
            // One response of each kind, so that every share has something behind it and the
            // per-response counters fire too. This is the test that fails if a counter is added to
            // CounterNames and never wired up.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "public, max-age=600", hasSetCookie: true);
            Record(recorder, "private, max-age=60");
            Record(recorder, "no-cache", hasValidator: true);
            Record(recorder, "no-store");
            Record(recorder, null);
            MetricFlush.Run(recorder);

            Assert.Equal(
                new[]
                {
                    Names.ResponsesPerSecond,
                    Names.PublicPercent,
                    Names.PrivatePercent,
                    Names.RevalidatePercent,
                    Names.NoStorePercent,
                    Names.NoDirectivePercent,
                    Names.FreshnessSeconds,
                    Names.ValidatorPercent,
                    Names.SharedCacheConflictPercent,
                }.OrderBy(name => name, StringComparer.Ordinal),
                tracker.Names.OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void The_response_rate_is_the_count_over_the_interval()
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            for (var i = 0; i < 120; i++)
            {
                Record(recorder, "no-store");
            }

            MetricFlush.Run(recorder);

            Assert.Equal(2d, ValueOf(tracker, Names.ResponsesPerSecond));
        }

        [Theory]
        [InlineData("public, max-age=600", Names.PublicPercent)]
        [InlineData("private, max-age=600", Names.PrivatePercent)]
        [InlineData("no-cache", Names.RevalidatePercent)]
        [InlineData("no-store", Names.NoStorePercent)]
        [InlineData(null, Names.NoDirectivePercent)]
        public void One_kind_of_response_all_interval_is_a_hundred_percent_of_that_kind(
            string? cacheControl, string expected)
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, cacheControl);
            Record(recorder, cacheControl);
            MetricFlush.Run(recorder);

            Assert.Equal(100d, ValueOf(tracker, expected));
        }

        [Fact]
        public void The_five_shares_always_add_up_to_a_hundred()
        {
            // The property that makes the five readable as one stacked chart, and the reason the
            // buckets are an enum rather than five independent flags: every response increments
            // exactly one of them, so there is no arrangement of headers that can make these sum to
            // anything else.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "public, max-age=600");
            Record(recorder, "public, max-age=600");
            Record(recorder, "public, max-age=600");
            Record(recorder, "private");
            Record(recorder, "must-revalidate");
            Record(recorder, "no-store");
            Record(recorder, "immutable");
            MetricFlush.Run(recorder);

            var total =
                ValueOf(tracker, Names.PublicPercent) +
                ValueOf(tracker, Names.PrivatePercent) +
                ValueOf(tracker, Names.RevalidatePercent) +
                ValueOf(tracker, Names.NoStorePercent) +
                ValueOf(tracker, Names.NoDirectivePercent);

            Assert.Equal(100d, total, precision: 9);
        }

        [Fact]
        public void A_flush_starts_the_next_interval_from_nothing()
        {
            // Shares that carried yesterday's traffic forward would smear a deployment across the
            // hour after it. Every count is exchanged to zero as it is read.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "no-store");
            MetricFlush.Run(recorder);
            MetricFlush.Run(recorder);

            Assert.Equal(0d, ValueOf(tracker, Names.ResponsesPerSecond));
            Assert.Equal(
                1, tracker.Metrics.Count(metric => metric.Name == Names.NoStorePercent));
        }

        [Fact]
        public void Freshness_is_published_as_each_response_goes_out()
        {
            // Per response rather than averaged here, so the counter's own aggregation keeps the
            // maximum as well as the mean. A single route with a ten-minute lifetime among a
            // thousand five-second ones is the interesting reading, and a mean would hide it.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "public, max-age=600");
            Record(recorder, "private, max-age=30");

            Assert.Equal(
                new[] { 600d, 30d },
                tracker.Metrics
                    .Where(metric => metric.Name == Names.FreshnessSeconds)
                    .Select(metric => metric.Value));
        }

        [Fact]
        public void A_response_that_states_no_lifetime_reports_none()
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "no-store");
            Record(recorder, "no-cache");
            Record(recorder, null);
            MetricFlush.Run(recorder);

            Assert.DoesNotContain(Names.FreshnessSeconds, tracker.Names);
        }

        [Fact]
        public void Validators_are_counted_across_every_bucket()
        {
            // Half of these are uncacheable and half are not, and all four carry an ETag. The
            // counter is about conditional requests, which is a separate question from freshness.
            var tracker = new RecordingMetricTracker();

            using var recorder = new HttpCacheabilityRecorder(tracker);
            Record(recorder, "no-cache", hasValidator: true);
            Record(recorder, "public, max-age=60", hasValidator: true);
            Record(recorder, "no-cache");
            Record(recorder, "public, max-age=60");
            MetricFlush.Run(recorder);

            Assert.Equal(50d, ValueOf(tracker, Names.ValidatorPercent));
        }

        [Fact]
        public void A_shared_cacheable_response_that_sets_a_cookie_is_counted_and_reported()
        {
            // The finding worth a sentence rather than a number: the response claims to be
            // shared-cacheable, no shared cache will store it, and nothing about the traffic looks
            // wrong from the origin's side.
            var tracker = new RecordingMetricTracker();
            var logger = new RecordingLogger();

            using var recorder = new HttpCacheabilityRecorder(tracker, logger: logger);
            Record(recorder, "public, max-age=600", hasSetCookie: true, path: "/en/products");
            Record(recorder, "public, max-age=600");
            MetricFlush.Run(recorder);

            Assert.Equal(50d, ValueOf(tracker, Names.SharedCacheConflictPercent));
            Assert.Contains(
                logger.Entries,
                entry => entry.Message.Contains("/en/products"));
        }

        [Fact]
        public void One_steadily_misconfigured_route_cannot_fill_the_log()
        {
            // The cap is per minute and the finding is per response, so a route serving a hundred
            // times a second would otherwise write the same sentence a hundred times a second. The
            // counter keeps the true scale; the log only has to say it once.
            var tracker = new RecordingMetricTracker();
            var logger = new RecordingLogger();
            var options = new HttpCacheabilityOptions { SharedCacheConflictLogsPerMinute = 3 };

            using var recorder = new HttpCacheabilityRecorder(tracker, options, logger);
            for (var i = 0; i < 50; i++)
            {
                Record(recorder, "public, max-age=600", hasSetCookie: true);
            }

            MetricFlush.Run(recorder);

            Assert.Equal(3, logger.Entries.Count);
            Assert.Equal(100d, ValueOf(tracker, Names.SharedCacheConflictPercent));
        }

        [Fact]
        public void Conflicts_can_be_counted_without_being_logged()
        {
            // For a site that knows about the conflict and has decided to live with it. The counter
            // stays, because a share going up is still worth seeing; only the sentence stops.
            var tracker = new RecordingMetricTracker();
            var logger = new RecordingLogger();
            var options = new HttpCacheabilityOptions { LogSharedCacheConflicts = false };

            using var recorder = new HttpCacheabilityRecorder(tracker, options, logger);
            Record(recorder, "public, max-age=600", hasSetCookie: true);
            MetricFlush.Run(recorder);

            Assert.Empty(logger.Entries);
            Assert.Equal(100d, ValueOf(tracker, Names.SharedCacheConflictPercent));
        }

        [Fact]
        public void Configuration_can_switch_the_whole_thing_off()
        {
            // Read by the sinks before they touch a response, so switching off removes the header
            // reads as well as the counters.
            var tracker = new RecordingMetricTracker();
            var options = new HttpCacheabilityOptions { Enabled = false };

            using var recorder = new HttpCacheabilityRecorder(tracker, options);

            Assert.True(recorder.IsDisabled);

            Record(recorder, "public, max-age=600");
            MetricFlush.Run(recorder);

            Assert.Equal(0d, ValueOf(tracker, Names.ResponsesPerSecond));
        }

        [Fact]
        public void Repeated_failures_switch_it_off_rather_than_reaching_the_response()
        {
            // This runs while the response headers are being written, so the alternative to
            // swallowing a broken tracker is failing requests that had already succeeded. Counting
            // the failures and stopping is what keeps a broken sink from being a broken site.
            var tracker = new ThrowingMetricTracker();
            var logger = new RecordingLogger();
            var options = new HttpCacheabilityOptions { FailureThreshold = 3 };

            using var recorder = new HttpCacheabilityRecorder(tracker, options, logger);
            for (var i = 0; i < 3; i++)
            {
                // Needs a lifetime: the metric write is the only call in Record that can throw.
                Record(recorder, "public, max-age=600");
            }

            Assert.True(recorder.IsDisabled);
            Assert.Single(logger.Failures);
        }

        [Fact]
        public void A_recorder_with_nowhere_to_publish_is_refused_at_construction()
        {
            // Not deferred to the first response. A null tracker is a wiring mistake, and the place
            // to find out is the line that made it.
            Assert.Throws<ArgumentNullException>(
                () => new HttpCacheabilityRecorder(null!));
        }

        [Fact]
        public void Only_the_first_start_wins()
        {
            // The CMS and Commerce packages are routinely installed together and each has an
            // initialization module that starts this. Two recorders would each see every response,
            // so the rate would read double while the shares, being shares, read correctly - a
            // combination worse than either being wrong on its own.
            var tracker = new RecordingMetricTracker();

            Assert.True(HttpCacheabilityMonitor.Start(tracker));

            var first = HttpCacheabilityMonitor.Current;

            Assert.False(HttpCacheabilityMonitor.Start(tracker));
            Assert.Same(first, HttpCacheabilityMonitor.Current);
        }

        [Fact]
        public void A_disabled_monitor_starts_nothing_at_all()
        {
            var options = new HttpCacheabilityOptions { Enabled = false };

            Assert.False(HttpCacheabilityMonitor.Start(new RecordingMetricTracker(), options));
            Assert.False(HttpCacheabilityMonitor.IsRunning);
            Assert.Null(HttpCacheabilityMonitor.Current);
        }

        [Fact]
        public void Stopping_leaves_the_sinks_nothing_to_report_to()
        {
            // Both sinks read Current on every response and treat null as "do not measure", so this
            // is what shutdown looks like from their side. There is no other way to detach them:
            // the module stays in the ASP.NET pipeline for the life of the process.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            HttpCacheabilityMonitor.Stop();

            Assert.Null(HttpCacheabilityMonitor.Current);
            Assert.False(HttpCacheabilityMonitor.IsRunning);
        }

        [Fact]
        public void Stopping_something_that_never_started_is_allowed()
        {
            // Uninitialize runs even when ConfigureContainer decided not to start anything.
            HttpCacheabilityMonitor.Stop();
            HttpCacheabilityMonitor.Stop();
        }

        [Fact]
        public void A_monitor_with_nowhere_to_publish_is_refused()
        {
            Assert.Throws<ArgumentNullException>(
                () => HttpCacheabilityMonitor.Start(null!));
        }

        private static void Record(
            HttpCacheabilityRecorder recorder,
            string? cacheControl,
            bool hasValidator = false,
            bool hasSetCookie = false,
            string? path = null) =>
            recorder.Record(
                ResponseCacheSummary.Describe(cacheControl, null, hasValidator, hasSetCookie),
                path);

        private static double ValueOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.Last(metric => metric.Name == name).Value;

        /// <summary>
        /// A tracker that fails the way a broken one fails: on the metric write, from the thread
        /// that is about to send a response.
        /// </summary>
        private sealed class ThrowingMetricTracker : IMetricTracker
        {
            public bool IsEnabled => true;

            public void TrackMetric(string name, double value) =>
                throw new InvalidOperationException("The metric sink is broken.");

            public void TrackMetric(
                string name, double value, string dimension1Name, string dimension1Value) =>
                throw new InvalidOperationException("The metric sink is broken.");

            public void TrackMetric(
                string name, double value,
                string dimension1Name, string dimension1Value,
                string dimension2Name, string dimension2Value) =>
                throw new InvalidOperationException("The metric sink is broken.");

            public void TrackMetric(
                string name, double value,
                string dimension1Name, string dimension1Value,
                string dimension2Name, string dimension2Value,
                string dimension3Name, string dimension3Value) =>
                throw new InvalidOperationException("The metric sink is broken.");
        }
    }

    /// <summary>
    /// Serializes everything that touches <see cref="HttpCacheabilityMonitor"/>, which is one
    /// recorder per process by design.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class HttpCacheabilityCollection
    {
        public const string Name = "HttpCacheability";
    }
}
