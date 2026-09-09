#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using Optimizely.Performance.Counters.Core.Telemetry;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Whether the SqlClient pool counters this package asks Application Insights for are the ones
    /// SqlClient actually publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These names are the only ones in the registry that this package does not produce, so nothing
    /// else here would notice them going stale. Application Insights subscribes by exact
    /// (source, counter) pair and collects nothing for a pair that does not match, so a renamed
    /// counter charts as permanently empty rather than failing - and SqlClient is a dependency
    /// Optimizely upgrades on its own schedule.
    /// </para>
    /// <para>
    /// Asked of the real event source rather than a copied list. A list checked against another list
    /// only proves the two were typed the same way.
    /// </para>
    /// <para>
    /// Not compiled on net472. SqlClient publishes these as Windows performance counters there, in a
    /// category this package does not collect from - see <see cref="SqlClientCounters"/>.
    /// </para>
    /// </remarks>
    public class SqlClientCounterNamesTests
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

        [Fact]
        public void The_sql_client_event_source_exists_under_the_name_we_register()
        {
            using var listener = new SqlClientCounterListener();

            Assert.True(
                listener.FoundTheSource,
                $"No event source named '{SqlClientCounters.EventSourceName}' was created, even after " +
                "loading SqlClient. Everything registered against that name collects nothing.");
        }

        [Fact]
        public void Every_pool_counter_we_ask_for_is_one_sql_client_publishes()
        {
            using var listener = new SqlClientCounterListener();
            var published = listener.WaitForCounterNames(SqlClientCounters.All);

            var missing = SqlClientCounters.All.Except(published, StringComparer.Ordinal).ToList();

            Assert.True(
                missing.Count == 0,
                "These counters are registered with Application Insights but SqlClient does not " +
                "publish them, so they will chart as permanently empty: " +
                string.Join(", ", missing) + Environment.NewLine +
                "SqlClient publishes: " +
                string.Join(", ", published.OrderBy(name => name, StringComparer.Ordinal)));
        }

        /// <summary>
        /// Enables SqlClient's event source and collects the counter names it reports.
        /// </summary>
        private sealed class SqlClientCounterListener : EventListener
        {
            private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);
            private EventSource? _sqlClient;

            internal SqlClientCounterListener()
            {
                // The event source is a singleton created the first time anything in SqlClient runs,
                // so until the assembly is touched there is nothing to listen to. A connection is
                // constructed and thrown away; nothing is opened and no connection string is needed.
                LoadSqlClient();

                // Enabled from here rather than from OnEventSourceCreated, which can fire during the
                // base constructor before this type's own fields exist.
                var source = _sqlClient;

                if (source != null)
                {
                    EnableCounters(source);
                }
            }

            internal bool FoundTheSource => _sqlClient != null;

            /// <summary>
            /// Waits until every name in <paramref name="expected"/> has been reported, then returns
            /// everything seen. Returns early on timeout so the caller can say what was missing
            /// rather than failing on the wait.
            /// </summary>
            /// <remarks>
            /// Containment rather than a count. SqlClient publishes more counters than this package
            /// collects, and every counter arrives as its own event, so a count-based wait can be
            /// satisfied by a partial set topped up with counters nobody asked for - which fails the
            /// caller's assertion on timing rather than on the names having drifted.
            /// </remarks>
            internal IReadOnlyCollection<string> WaitForCounterNames(IEnumerable<string> expected)
            {
                var required = new HashSet<string>(expected, StringComparer.Ordinal);
                var deadline = Stopwatch.StartNew();

                while (deadline.Elapsed < Patience)
                {
                    lock (_names)
                    {
                        if (required.IsSubsetOf(_names))
                        {
                            return _names.ToList();
                        }
                    }

                    Thread.Sleep(100);
                }

                lock (_names)
                {
                    return _names.ToList();
                }
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                if (!string.Equals(eventSource.Name, SqlClientCounters.EventSourceName, StringComparison.Ordinal))
                {
                    return;
                }

                _sqlClient = eventSource;

                // Sources created after construction are enabled here; the one that already existed
                // is enabled by the constructor, which cannot rely on this having run.
                EnableCounters(eventSource);
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                // Only SqlClient's source. A listener is supposed to receive events from the sources
                // it enabled and no others, but the runtime tracks that with a per-listener index
                // into a global source list, and test classes running in parallel construct
                // listeners and sources concurrently - which is enough to have another test's source
                // delivered here. Without this, the package's own counters arrive and this test
                // passes or fails on whichever of the two got in first.
                if (!string.Equals(
                        eventData.EventSource?.Name,
                        SqlClientCounters.EventSourceName,
                        StringComparison.Ordinal))
                {
                    return;
                }

                if (!string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal))
                {
                    return;
                }

                if (eventData.Payload == null)
                {
                    return;
                }

                foreach (var payload in eventData.Payload)
                {
                    if (payload is IDictionary<string, object?> counter &&
                        counter.TryGetValue("Name", out var name) &&
                        name is string counterName)
                    {
                        lock (_names)
                        {
                            _names.Add(counterName);
                        }
                    }
                }
            }

            public override void Dispose()
            {
                var source = _sqlClient;

                if (source != null)
                {
                    DisableEvents(source);
                }

                base.Dispose();
            }

            private void EnableCounters(EventSource source) =>
                EnableEvents(
                    source,
                    EventLevel.LogAlways,
                    EventKeywords.All,
                    new Dictionary<string, string?>
                    {
                        ["EventCounterIntervalSec"] = PollInterval.TotalSeconds.ToString("F0"),
                    });

            /// <remarks>
            /// By reflection, so the test project takes no compile-time dependency on SqlClient. It
            /// is here transitively through Commerce, at whichever version that Optimizely major
            /// pins, and naming it directly would pin a second one.
            /// </remarks>
            private static void LoadSqlClient()
            {
                var connectionType = Type.GetType(
                    "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
                    throwOnError: false);

                Assert.True(
                    connectionType != null,
                    "Microsoft.Data.SqlClient could not be loaded, so the event source these counter " +
                    "names come from was never created and nothing here would be verified.");

                using var connection = (IDisposable)Activator.CreateInstance(connectionType!)!;
            }
        }
    }
}
#endif
