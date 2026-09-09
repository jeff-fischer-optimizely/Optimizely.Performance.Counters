using System;
using System.Linq;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;
#if !NET472
using Microsoft.Extensions.Logging;
#endif

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// The counting half of the log write rate feature: what the recorder publishes, and what the
    /// monitor does with the one recorder a process is allowed to have.
    /// <para>
    /// The sinks that feed it are covered separately, because they differ by major -
    /// <c>Log4NetWriteRateSinkTests</c> on V11 and the provider tests at the bottom of this file on
    /// V12 and V13.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Not run in parallel with anything else: <see cref="LogWriteRateMonitor"/> is process-wide
    /// static state, so a test that starts it while another has one running would see the other's
    /// recorder. The collection name is shared with the log4net tests, which start the same monitor.
    /// </remarks>
    [Collection(LogWriteRateCollection.Name)]
    public class LogWriteRateTests : IDisposable
    {
        public void Dispose() => LogWriteRateMonitor.Stop();

        [Fact]
        public void A_flush_publishes_all_three_counters()
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new LogWriteRateRecorder(tracker);
            recorder.Record(LogWriteSeverity.Normal);
            MetricFlush.Run(recorder);

            Assert.Contains(CounterNames.Runtime.Logging.WritesPerSecond, tracker.Names);
            Assert.Contains(CounterNames.Runtime.Logging.WarningsPerSecond, tracker.Names);
            Assert.Contains(CounterNames.Runtime.Logging.ErrorsPerSecond, tracker.Names);
        }

        [Fact]
        public void A_quiet_interval_publishes_zero_rather_than_nothing()
        {
            // Deliberate. A gap in a chart reads as "the counter is broken"; a zero reads as "the
            // site stopped logging", which during an incident is information.
            var tracker = new RecordingMetricTracker();

            using var recorder = new LogWriteRateRecorder(tracker);
            MetricFlush.Run(recorder);

            Assert.Equal(3, tracker.Metrics.Count);
            Assert.All(tracker.Metrics, metric => Assert.Equal(0d, metric.Value));
        }

        [Theory]
        [InlineData(LogWriteSeverity.Warning, CounterNames.Runtime.Logging.WarningsPerSecond)]
        [InlineData(LogWriteSeverity.Error, CounterNames.Runtime.Logging.ErrorsPerSecond)]
        public void A_warning_or_error_counts_towards_its_own_counter_and_the_total(
            LogWriteSeverity severity, string expected)
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new LogWriteRateRecorder(tracker);
            recorder.Record(severity);
            MetricFlush.Run(recorder);

            // Both, not either. The severity counters are a breakdown of the write rate rather than
            // a partition of it, so a query can chart errors against total writes without adding
            // the three series together first.
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
            Assert.Equal(Rate(1), ValueOf(tracker, expected));
        }

        [Fact]
        public void An_ordinary_write_leaves_the_severity_counters_alone()
        {
            var tracker = new RecordingMetricTracker();

            using var recorder = new LogWriteRateRecorder(tracker);
            recorder.Record(LogWriteSeverity.Normal);
            MetricFlush.Run(recorder);

            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
            Assert.Equal(0d, ValueOf(tracker, CounterNames.Runtime.Logging.WarningsPerSecond));
            Assert.Equal(0d, ValueOf(tracker, CounterNames.Runtime.Logging.ErrorsPerSecond));
        }

        [Fact]
        public void Counts_are_reset_by_the_flush_that_published_them()
        {
            // What makes this a rate rather than a running total. Without the reset every interval
            // would report everything since startup, so the chart would only ever climb.
            var tracker = new RecordingMetricTracker();

            using var recorder = new LogWriteRateRecorder(tracker);
            recorder.Record(LogWriteSeverity.Error);
            MetricFlush.Run(recorder);
            MetricFlush.Run(recorder);

            var writes = tracker.Metrics
                .Where(metric => metric.Name == CounterNames.Runtime.Logging.WritesPerSecond)
                .Select(metric => metric.Value)
                .ToList();

            Assert.Equal(new[] { Rate(1), 0d }, writes);
        }

        [Fact]
        public void A_recorder_will_not_take_a_null_tracker() =>
            Assert.Throws<ArgumentNullException>(() => new LogWriteRateRecorder(null!));

        [Fact]
        public void The_monitor_will_not_take_a_null_tracker() =>
            Assert.Throws<ArgumentNullException>(() => LogWriteRateMonitor.Start(null!));

        [Fact]
        public void Only_the_first_start_wins()
        {
            // The case this exists for: a site with both packages installed runs two initialization
            // modules, each of which starts this. Two recorders would both be fed by the one sink,
            // so every counter would read double.
            var tracker = new RecordingMetricTracker();

            Assert.True(LogWriteRateMonitor.Start(tracker));

            var first = LogWriteRateMonitor.Current;

            Assert.False(LogWriteRateMonitor.Start(new RecordingMetricTracker()));
            Assert.Same(first, LogWriteRateMonitor.Current);
        }

        [Fact]
        public void Configuration_can_switch_the_whole_thing_off()
        {
            Assert.False(LogWriteRateMonitor.Start(
                new RecordingMetricTracker(), new LogWriteRateOptions { Enabled = false }));

            Assert.False(LogWriteRateMonitor.IsRunning);
            Assert.Null(LogWriteRateMonitor.Current);
        }

        [Fact]
        public void Stopping_leaves_nothing_for_a_sink_to_count_into()
        {
            LogWriteRateMonitor.Start(new RecordingMetricTracker());
            LogWriteRateMonitor.Stop();

            Assert.False(LogWriteRateMonitor.IsRunning);

            // Idempotent, because Uninitialize runs on both modules and the second one has to be
            // able to call this without knowing the first did.
            LogWriteRateMonitor.Stop();
        }

#if !NET472
        [Fact]
        public void The_provider_counts_what_the_hosts_logging_writes()
        {
            // Through a real LoggerFactory rather than by calling the provider's logger directly.
            // What is being tested is that a provider registered the way the initialization module
            // registers one actually sees writes, and that is a fact about Microsoft.Extensions
            // .Logging's dispatch rather than about our ILogger.
            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);

            using var factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new LogWriteRateLoggerProvider());
            });

            var logger = factory.CreateLogger("Some.Site.Category");
            logger.LogInformation("ordinary");
            logger.LogWarning("a warning");
            logger.LogError("an error");
            logger.LogCritical("worse than an error");

            MetricFlush.Run(LogWriteRateMonitor.Current!);

            Assert.Equal(Rate(4), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WarningsPerSecond));

            // Critical counts as an error. There is no third severity counter, and a chart of
            // "things that went wrong" that omitted the worst of them would be misleading.
            Assert.Equal(Rate(2), ValueOf(tracker, CounterNames.Runtime.Logging.ErrorsPerSecond));
        }

        [Fact]
        public void The_provider_never_reports_itself_as_enabled()
        {
            // Load-bearing, and the reason the provider is safe to register on any site.
            // ILogger.IsEnabled is the OR across every provider's logger, so a provider that said
            // true at Trace would make every 'if (logger.IsEnabled(...))' in the site true and set
            // it building messages that no sink writes. Log is called regardless of what this
            // returns - the factory consults the configured filter, not the provider - which is
            // what lets it count without inflating anything.
            var provider = new LogWriteRateLoggerProvider();
            var logger = provider.CreateLogger("Some.Site.Category");

            Assert.All(
                Enum.GetValues(typeof(LogLevel)).Cast<LogLevel>(),
                level => Assert.False(logger.IsEnabled(level)));
        }

        [Fact]
        public void The_provider_counts_nothing_when_the_monitor_is_not_running()
        {
            // The state a site is in for its whole startup, and again after shutdown. It has to be
            // silent rather than faulted: the provider is constructed by the host's logging long
            // before any initialization module runs.
            LogWriteRateMonitor.Stop();

            var provider = new LogWriteRateLoggerProvider();
            var logger = provider.CreateLogger("Some.Site.Category");

            var exception = Record.Exception(() => logger.LogError("nothing is listening yet"));

            Assert.Null(exception);
        }

        [Fact]
        public void The_provider_never_renders_the_message()
        {
            // Rendering is most of what a log write costs, and doing it again to count it would
            // double the cost of logging on a site that is already logging too much - which is
            // precisely the site these counters exist to find.
            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);

            var provider = new LogWriteRateLoggerProvider();
            var logger = provider.CreateLogger("Some.Site.Category");
            var rendered = false;

            logger.Log<object?>(
                LogLevel.Information,
                new EventId(1),
                state: null,
                exception: null,
                formatter: (_, _) =>
                {
                    rendered = true;
                    return "should never be asked for";
                });

            Assert.False(rendered);

            MetricFlush.Run(LogWriteRateMonitor.Current!);
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
        }
#endif

        /// <summary>
        /// A count of <paramref name="writes"/> over one reporting interval, as a rate.
        /// </summary>
        /// <remarks>
        /// Derived from the reporting interval rather than written out, so that changing the
        /// interval does not turn every assertion here into a puzzle about where 60 came from.
        /// </remarks>
        internal static double Rate(int writes) => writes / 60.0;

        internal static double? ValueOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.LastOrDefault(metric => metric.Name == name)?.Value;
    }

    /// <summary>
    /// Serialises every test that touches the process-wide <see cref="LogWriteRateMonitor"/>.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class LogWriteRateCollection
    {
        public const string Name = "log write rate";
    }
}
