#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Whether the SQL connection pool counters this package asks for on .NET Framework are the ones
    /// ADO.NET actually publishes, and whether the instance name they are asked for under is the one
    /// ADO.NET publishes them at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The V11 counterpart to <see cref="SqlClientCounterNamesTests"/>, and it has the same failure
    /// mode to guard against: a performance counter path that matches nothing is reported as absent
    /// rather than as wrong, so a name or an instance that drifts charts as permanently empty and
    /// nothing anywhere says why.
    /// </para>
    /// <para>
    /// The instance name is the sharper edge of the two. It is reconstructed rather than read,
    /// because the framework exposes it nowhere, so it is a copy of somebody else's private method
    /// with no compiler and no test between the copy and the original except this one.
    /// </para>
    /// <para>
    /// Compiled on net472 only. On .NET 6 and later the same measurements are EventCounters, which
    /// is a different mechanism with different names - see <see cref="SqlClientCounters"/>.
    /// </para>
    /// </remarks>
    public class SqlClientWindowsCounterTests
    {
        [Fact]
        public void The_counter_category_exists_under_the_name_we_register()
        {
            Assert.True(
                PerformanceCounterCategory.Exists(SqlClientCounters.WindowsCategoryName),
                $"No performance counter category named '{SqlClientCounters.WindowsCategoryName}' is " +
                "installed, so every path built from it collects nothing. It ships with the .NET " +
                "Framework, so its absence means the framework's counters were never registered on " +
                "this machine - lodctr /R repairs that.");
        }

        /// <summary>
        /// Asked of the real category rather than a copied list, for the same reason the EventCounter
        /// side is asked of the real event source: a list checked against another list only proves
        /// the two were typed the same way.
        /// </summary>
        [Fact]
        public void Every_pool_counter_we_ask_for_is_one_ado_net_publishes()
        {
            var published = PublishedCounterNames();
            var requested = RequestedCounterNames();

            var missing = requested.Except(published, StringComparer.Ordinal).ToList();

            Assert.True(
                missing.Count == 0,
                "These counters are registered with Application Insights but ADO.NET does not " +
                "publish them, so they will chart as permanently empty: " +
                string.Join(", ", missing) + Environment.NewLine +
                "The category publishes: " +
                string.Join(", ", published.OrderBy(name => name, StringComparer.Ordinal)));
        }

        [Fact]
        public void Every_counter_path_carries_the_instance_token()
        {
            // The token is substituted at registration. A path that shipped without it would name an
            // instance called "??SQLCLIENT_INSTANCE??", which resolves for nobody.
            Assert.All(
                SqlClientCounters.GetWindowsCounters(),
                counter => Assert.Contains(
                    SqlClientCounters.InstanceNameToken,
                    counter.CounterPath,
                    StringComparison.Ordinal));
        }

        [Fact]
        public void Every_counter_is_reported_under_a_name_of_its_own()
        {
            // Two counters reporting as the same name do not fail; they interleave into one series
            // that averages two unrelated measurements.
            var duplicated = SqlClientCounters.GetWindowsCounters()
                .GroupBy(counter => counter.ReportAs, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            Assert.True(duplicated.Count == 0, "Reported more than once: " + string.Join(", ", duplicated));
        }

        [Fact]
        public void The_pool_counters_that_need_no_switch_are_always_asked_for()
        {
            var requested = RequestedCounterNames();

            // NumberOfReclaimedConnections is the one that names a defect rather than describing
            // load: anything other than zero means connections are being left undisposed and
            // recovered by the garbage collector.
            Assert.Contains("NumberOfReclaimedConnections", requested);
            Assert.Contains("NumberOfPooledConnections", requested);
        }

        [Fact]
        public void The_detail_counters_are_left_out_while_the_switch_is_off()
        {
            // No switch is configured for this test host, matching an unmodified site. The four
            // detail counters read a constant zero in that state, so they must not be collected -
            // a metric that is confidently and permanently wrong is worse than a missing one.
            Assert.False(SqlClientCounters.IsDetailEnabled());

            var requested = RequestedCounterNames();

            foreach (var detail in new[]
                     {
                         "NumberOfActiveConnections",
                         "NumberOfFreeConnections",
                         "SoftConnectsPerSecond",
                         "SoftDisconnectsPerSecond",
                     })
            {
                Assert.DoesNotContain(detail, requested);
            }
        }

        [Fact]
        public void Reading_the_trace_switch_never_throws()
        {
            // A malformed switch value throws out of TraceSwitch, and this runs during startup.
            Assert.Null(Record.Exception(() => SqlClientCounters.IsDetailEnabled()));
        }

        [Fact]
        public void The_instance_name_ends_with_this_process_id()
        {
            // The process id suffix is what makes the instance unique across a web garden, where
            // several worker processes publish into the same category.
            Assert.EndsWith(
                $"[{Process.GetCurrentProcess().Id}]",
                SqlClientCounters.ResolveInstanceName(),
                StringComparison.Ordinal);
        }

        [Fact]
        public void The_instance_name_uses_the_entry_assembly_when_there_is_one()
        {
            // Under a test host or a console application there is an entry assembly, so the name
            // comes from it rather than from the app domain. Under IIS the reverse holds, which is
            // the case that makes the two hosts produce different names for the same application.
            var instance = SqlClientCounters.ResolveInstanceName();

            Assert.False(string.IsNullOrWhiteSpace(instance));
            Assert.NotEqual($"[{Process.GetCurrentProcess().Id}]", instance);
        }

        [Theory]
        [InlineData('(')]
        [InlineData(')')]
        [InlineData('/')]
        [InlineData('\\')]
        [InlineData('#')]
        public void The_instance_name_contains_no_character_ado_net_substitutes(char forbidden)
        {
            // ADO.NET rewrites these before publishing. Parentheses matter most: a counter path is
            // "\Category(Instance)\Counter", so an unescaped one would split the path.
            Assert.DoesNotContain(forbidden, SqlClientCounters.ResolveInstanceName());
        }

        [Fact]
        public void The_instance_name_respects_the_length_limit()
        {
            // 127 characters is the performance counter instance name limit. An app domain friendly
            // name under IIS can easily exceed it, at which point ADO.NET elides the middle - and a
            // name that was merely truncated would not match.
            var instance = SqlClientCounters.ResolveInstanceName();

            Assert.True(
                instance.Length <= 127,
                $"Instance name was {instance.Length} characters: {instance}");
        }

        [Fact]
        public void The_instance_name_is_the_same_every_time_it_is_asked_for()
        {
            // Resolved once at registration and reused for the life of the process, so it had
            // better not vary.
            Assert.Equal(SqlClientCounters.ResolveInstanceName(), SqlClientCounters.ResolveInstanceName());
        }

        /// <summary>
        /// The counter names ADO.NET publishes in its category on this machine.
        /// </summary>
        /// <remarks>
        /// Read with an empty instance name. The category is multi-instance, so the parameterless
        /// overload throws; an empty instance still reports the category's counter definitions,
        /// which is what is being checked here rather than any live measurement.
        /// </remarks>
        private static IReadOnlyCollection<string> PublishedCounterNames() =>
            new PerformanceCounterCategory(SqlClientCounters.WindowsCategoryName)
                .GetCounters(string.Empty)
                .Select(counter => counter.CounterName)
                .ToList();

        /// <summary>
        /// The counter names this package asks for, taken off the tail of each path.
        /// </summary>
        private static IReadOnlyCollection<string> RequestedCounterNames() =>
            SqlClientCounters.GetWindowsCounters()
                .Select(counter => counter.CounterPath.Substring(counter.CounterPath.LastIndexOf('\\') + 1))
                .ToList();
    }
}
#endif
