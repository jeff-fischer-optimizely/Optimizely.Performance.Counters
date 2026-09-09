#if NETFRAMEWORK
using System;
using System.Linq;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Whether <see cref="WindowsPerformanceCounterRegistration"/> still binds to Application
    /// Insights on .NET Framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The V11 counterpart to <see cref="ApplicationInsightsRegistrationTests"/>, and it exists for
    /// the same reason: the binding is entirely reflection over strings, every step of it fails by
    /// logging and returning rather than by throwing, and a binding that has stopped working is
    /// therefore indistinguishable from a host that simply has no Application Insights. Only a real
    /// <see cref="PerformanceCollectorModule"/> can tell the difference.
    /// </para>
    /// <para>
    /// V11 is the target where that matters most. It has no <c>IServiceCollection</c>, so there is
    /// no container to inspect afterwards and no framework calling back in - if this binding is
    /// wrong, nothing else notices at all.
    /// </para>
    /// </remarks>
    public class WindowsPerformanceCounterRegistrationTests
    {
        [Fact]
        public void Every_pool_counter_is_handed_to_the_collector()
        {
            var module = new PerformanceCollectorModule();

            var added = WindowsPerformanceCounterRegistration.AddCounters(module);

            Assert.Equal(SqlClientCounters.GetWindowsCounters().Count, added);
            Assert.Equal(added, module.Counters.Count);
        }

        [Fact]
        public void Every_counter_reaches_the_collector_under_the_name_it_is_meant_to_chart_as()
        {
            var module = new PerformanceCollectorModule();
            WindowsPerformanceCounterRegistration.AddCounters(module);

            var reported = module.Counters
                .Select(request => request.ReportAs)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var expected = SqlClientCounters.GetWindowsCounters()
                .Select(counter => counter.ReportAs)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected, reported);
        }

        [Fact]
        public void The_instance_token_is_gone_by_the_time_the_collector_sees_a_path()
        {
            var module = new PerformanceCollectorModule();
            WindowsPerformanceCounterRegistration.AddCounters(module);

            // The whole point of the token. A path still carrying it names an instance nothing
            // publishes under, and the counter is then reported as absent rather than as wrong.
            Assert.All(
                module.Counters,
                request => Assert.DoesNotContain(
                    SqlClientCounters.InstanceNameToken,
                    request.PerformanceCounter,
                    StringComparison.Ordinal));

            var instance = SqlClientCounters.ResolveInstanceName();

            Assert.All(
                module.Counters,
                request => Assert.Contains(instance, request.PerformanceCounter, StringComparison.Ordinal));
        }

        /// <summary>
        /// A site already collecting these must not end up collecting them twice.
        /// </summary>
        [Fact]
        public void A_counter_the_site_already_collects_is_not_added_again()
        {
            var alreadyCollected = SqlClientCounters.GetWindowsCounters()[0]
                .CounterPath
                .Replace(SqlClientCounters.InstanceNameToken, SqlClientCounters.ResolveInstanceName());

            var module = new PerformanceCollectorModule();
            module.Counters.Add(new PerformanceCounterCollectionRequest(alreadyCollected, "The site's own name"));

            var added = WindowsPerformanceCounterRegistration.AddCounters(module);

            Assert.Equal(SqlClientCounters.GetWindowsCounters().Count - 1, added);
            Assert.Single(
                module.Counters,
                request => request.PerformanceCounter == alreadyCollected);
        }

        /// <summary>
        /// The CMS and Commerce packages both configure telemetry and are routinely installed side
        /// by side, so this runs twice on a Commerce site.
        /// </summary>
        [Fact]
        public void Adding_twice_to_the_same_collector_asks_for_nothing_twice()
        {
            var module = new PerformanceCollectorModule();

            WindowsPerformanceCounterRegistration.AddCounters(module);
            var addedAgain = WindowsPerformanceCounterRegistration.AddCounters(module);

            Assert.Equal(0, addedAgain);

            var duplicated = module.Counters
                .GroupBy(request => request.PerformanceCounter, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            Assert.True(duplicated.Count == 0, "Collected more than once: " + string.Join(", ", duplicated));
        }

        [Fact]
        public void There_is_no_collector_to_add_to_when_none_was_given()
        {
            Assert.Throws<ArgumentNullException>(
                () => WindowsPerformanceCounterRegistration.AddCounters(null!));
        }

        /// <summary>
        /// The end-to-end path: find the module type, build it, fill it, and start it against the
        /// process-wide telemetry configuration.
        /// </summary>
        /// <remarks>
        /// This is the only assertion that covers the <c>PerformanceCollectorModule</c> type lookup
        /// and the <c>TelemetryConfiguration.Active</c> lookup, both of which are assembly-qualified
        /// strings that nothing else here would notice going stale. It latches a static, so the
        /// idempotency assertion lives in the same test rather than in one of its own.
        /// </remarks>
        [Fact]
        public void The_registration_reaches_the_collector_at_all()
        {
            var log = new RecordingLogger();

            Assert.True(
                WindowsPerformanceCounterRegistration.Register(log),
                "Nothing was registered with Application Insights. Every step of the binding fails " +
                "by logging and returning, so the log is the diagnosis:" +
                Environment.NewLine + log.Transcript());

            Assert.False(
                log.Failures.Any(),
                "Registration logged a failure:" + Environment.NewLine + log.Transcript());

            // Registering again builds no second collector. There is no shared module to read the
            // existing requests back off on this path, so a second collector would mean every
            // counter collected and billed twice.
            var second = new RecordingLogger();

            Assert.True(WindowsPerformanceCounterRegistration.Register(second));
            Assert.Empty(second.Entries);
        }
    }
}
#endif
