using System.Collections.Generic;
#if NET472
using System;
using System.Diagnostics;
using System.Reflection;
#endif

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// The ADO.NET connection pool counters, which SqlClient publishes itself rather than being
    /// produced by this package.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pool exhaustion is the usual end state of a cache problem: entries get invalidated en masse,
    /// every request misses, and they all queue for a connection. These counters are what
    /// distinguish "the database is slow" from "we ran out of connections to the database", which
    /// look identical from the outside and have nothing in common as fixes. That is why they are
    /// listed alongside the counters this package measures itself, even though nothing here produces
    /// them.
    /// </para>
    /// <para>
    /// Nothing needs enabling. The counters are created the first time anything enables the event
    /// source, which is exactly what registering them with the collector does. The counter names and
    /// the source name are identical across the SqlClient 3.x and 6.x versions Optimizely 12 and 13
    /// ship with.
    /// </para>
    /// <para>
    /// The same measurements exist on .NET Framework, but not as EventCounters. There SqlClient
    /// publishes them as Windows performance counters in the <c>.NET Data Provider for SqlServer</c>
    /// category, which is a different collector, a different registration path and an instance-name
    /// scheme of its own. All three are handled by the <c>NET472</c> members below and by
    /// <c>WindowsPerformanceCounterRegistration</c>, which is compiled on that target framework
    /// only and so is named here rather than linked. A V11 site charts the same pool the V12 and
    /// V13 sites do, under different counter names, because the two mechanisms name nothing alike.
    /// </para>
    /// </remarks>
    public static class SqlClientCounters
    {
        /// <summary>
        /// The event source SqlClient publishes its pool counters on.
        /// </summary>
        /// <remarks>
        /// <c>Microsoft.Data.SqlClient</c>, not <c>System.Data.SqlClient</c>. Optimizely 12 and 13
        /// both use the former; the latter publishes nothing comparable.
        /// </remarks>
        public const string EventSourceName = "Microsoft.Data.SqlClient.EventSource";

        /// <summary>
        /// Gets the pool counters to collect, in SqlClient's own naming.
        /// </summary>
        /// <remarks>
        /// Spelled exactly as SqlClient publishes them. The collector matches by string, so a name
        /// that drifts collects nothing rather than failing.
        /// </remarks>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            // How many connections exist, and how they are held. A non-pooled connection is one
            // opened with Pooling=false or outside a pool group; it costs a full connect every time.
            "number-of-pooled-connections",
            "number-of-non-pooled-connections",

            // In use versus available: the numerator and denominator of pool utilisation. When free
            // reaches zero and stays there, requests are blocking on the pool and will fail with a
            // connection timeout that names nothing useful.
            "number-of-active-connections",
            "number-of-free-connections",

            // A sustained hard connect rate means the pool is not holding connections: each request
            // pays the full TCP and authentication cost. Under steady load this should settle near
            // zero. Soft connects are pool hits, so the ratio between the two is the pool's hit rate.
            "hard-connects",
            "hard-disconnects",
            "soft-connects",
            "soft-disconnects",

            // Pool count and pool group count should both be small and flat. Growth means connection
            // strings are varying at runtime - each variant gets its own pool with its own Max Pool
            // Size, so the site can exhaust a pool while the totals still look healthy.
            "number-of-active-connection-pools",
            "number-of-active-connection-pool-groups",

            // Connections held open by an unfinished distributed transaction.
            "number-of-stasis-connections",

            // Connections the garbage collector had to recover because nothing disposed them.
            // Anything other than zero is a leak, and it is the one counter here that names a defect
            // rather than describing load.
            "number-of-reclaimed-connections",
        };

#if NET472
        /// <summary>
        /// Placeholder for the connection pool counter instance name.
        /// </summary>
        /// <remarks>
        /// Application Insights understands <c>??APP_WIN32_PROC??</c>, <c>??APP_CLR_PROC??</c> and
        /// <c>??APP_W3SVC_PROC??</c>, but ADO.NET names its instance differently from all three, so
        /// this one is substituted by <see cref="WindowsPerformanceCounterRegistration"/> before the
        /// path reaches the collector.
        /// </remarks>
        public const string InstanceNameToken = "??SQLCLIENT_INSTANCE??";

        /// <summary>
        /// Name of the trace switch that turns on the four detail-level pool counters.
        /// </summary>
        public const string DetailSwitchName = "ConnectionPoolPerformanceCounterDetail";

        /// <summary>
        /// The Windows performance counter category ADO.NET publishes the pool counters in.
        /// </summary>
        public const string WindowsCategoryName = ".NET Data Provider for SqlServer";

        private const int InstanceNameMaxLength = 127;
        private const string TruncationMarker = "[...]";

        /// <summary>
        /// Gets the pool counters to collect on .NET Framework, as counter paths.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same measurements as <see cref="All"/>, under the names the Windows counter category
        /// uses. The two mechanisms agree on nothing but the meaning: <c>hard-connects</c> here is
        /// <c>HardConnectsPerSecond</c>, and there is no counter matching
        /// <c>number-of-inactive-connection-pools</c> at all. A V11 chart therefore cannot be
        /// overlaid on a V12 one without renaming a series, which is why the reported names are
        /// spelled out rather than derived.
        /// </para>
        /// <para>
        /// A method rather than a property because the answer depends on
        /// <see cref="IsDetailEnabled"/>, which reads host configuration.
        /// </para>
        /// </remarks>
        /// <returns>Counter paths carrying <see cref="InstanceNameToken"/>, with the name to report each as.</returns>
        public static IReadOnlyList<WindowsCounter> GetWindowsCounters()
        {
            var counters = new List<WindowsCounter>
            {
                // How many connections exist, and how they are held.
                new WindowsCounter(Path("NumberOfPooledConnections"), "SQL Pooled Connections"),
                new WindowsCounter(Path("NumberOfNonPooledConnections"), "SQL Non-Pooled Connections"),

                // A sustained hard connect rate means the pool is not holding connections: each
                // request pays the full TCP and authentication cost. Under steady load this should
                // settle near zero.
                new WindowsCounter(Path("HardConnectsPerSecond"), "SQL Hard Connects/Sec"),
                new WindowsCounter(Path("HardDisconnectsPerSecond"), "SQL Hard Disconnects/Sec"),

                // Pool count and pool group count should both be small and flat. Growth means
                // connection strings are varying at runtime - each variant gets its own pool with
                // its own Max Pool Size, so the site can exhaust a pool while the totals still look
                // healthy.
                new WindowsCounter(Path("NumberOfActiveConnectionPools"), "SQL Active Connection Pools"),
                new WindowsCounter(Path("NumberOfActiveConnectionPoolGroups"), "SQL Active Connection Pool Groups"),

                // Connections held open by an unfinished distributed transaction.
                new WindowsCounter(Path("NumberOfStasisConnections"), "SQL Stasis Connections"),

                // Connections the garbage collector had to recover because nothing disposed them.
                new WindowsCounter(Path("NumberOfReclaimedConnections"), "SQL Reclaimed Connections"),
            };

            // The four that answer "how full is the pool" are only published when the host opts in;
            // without the switch they read a constant zero rather than failing, so they are
            // collected only once they can be believed.
            if (IsDetailEnabled())
            {
                counters.Add(new WindowsCounter(Path("NumberOfActiveConnections"), "SQL Active Connections"));
                counters.Add(new WindowsCounter(Path("NumberOfFreeConnections"), "SQL Free Connections"));
                counters.Add(new WindowsCounter(Path("SoftConnectsPerSecond"), "SQL Soft Connects/Sec"));
                counters.Add(new WindowsCounter(Path("SoftDisconnectsPerSecond"), "SQL Soft Disconnects/Sec"));
            }

            return counters;
        }

        /// <summary>
        /// Builds the counter instance name ADO.NET publishes under for this process.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This reproduces <c>DbConnectionPoolCounters.GetInstanceName</c> exactly, because there is
        /// no API that reports it. Under IIS the entry assembly is null, so the name comes from the
        /// app domain's friendly name - typically something along the lines of
        /// <c>/LM/W3SVC/2/ROOT-1-133...</c>, which the character substitutions below turn into
        /// <c>_LM_W3SVC_2_ROOT-1-133...</c>. A console or service host takes the entry assembly name
        /// instead, so the two hosts produce visibly different instance names for the same
        /// application.
        /// </para>
        /// <para>
        /// Being a faithful copy is the whole point: any deviation yields a path that matches no
        /// live instance, and the counter reads as absent rather than as an error.
        /// </para>
        /// </remarks>
        /// <returns>The instance name to substitute for <see cref="InstanceNameToken"/>.</returns>
        public static string ResolveInstanceName()
        {
            string? name = null;

            try
            {
                name = Assembly.GetEntryAssembly()?.GetName()?.Name;
            }
            catch
            {
                // Reflecting on the entry assembly can fail under partial trust. The app domain
                // fallback below is what ADO.NET itself would have used anyway.
            }

            if (string.IsNullOrEmpty(name))
            {
                name = AppDomain.CurrentDomain?.FriendlyName;
            }

            var instance = $"{name}[{GetCurrentProcessId()}]"
                .Replace('(', '[')
                .Replace(')', ']')
                .Replace('#', '_')
                .Replace('/', '_')
                .Replace('\\', '_');

            if (instance.Length <= InstanceNameMaxLength)
            {
                return instance;
            }

            // Same elision ADO.NET applies: keep both ends, drop the middle. The tail matters
            // because that is where the process id is.
            var head = (InstanceNameMaxLength - TruncationMarker.Length) / 2;
            var tail = InstanceNameMaxLength - head - TruncationMarker.Length;

            return instance.Substring(0, head)
                + TruncationMarker
                + instance.Substring(instance.Length - tail, tail);
        }

        /// <summary>
        /// Reports whether the four detail-level connection pool counters are being published.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>NumberOfActiveConnections</c>, <c>NumberOfFreeConnections</c>,
        /// <c>SoftConnectsPerSecond</c> and <c>SoftDisconnectsPerSecond</c> - the four that actually
        /// answer "how full is the pool" - are only created when the <see cref="DetailSwitchName"/>
        /// trace switch is set to <c>Verbose</c>. Without it they do not fail: the instance still
        /// exists, so the counters read a constant zero. A metric that is confidently and
        /// permanently wrong is worse than a missing one, which is why they are collected only when
        /// this returns <c>true</c>.
        /// </para>
        /// <para>
        /// Reading the same switch ADO.NET reads means the answer cannot drift from reality, and the
        /// site needs no setting beyond the one that turns the counters on:
        /// </para>
        /// <code>
        /// &lt;system.diagnostics&gt;
        ///   &lt;switches&gt;
        ///     &lt;add name="ConnectionPoolPerformanceCounterDetail" value="4" /&gt;
        ///   &lt;/switches&gt;
        /// &lt;/system.diagnostics&gt;
        /// </code>
        /// </remarks>
        /// <returns><c>true</c> when the detail counters carry real measurements.</returns>
        public static bool IsDetailEnabled()
        {
            try
            {
                return new TraceSwitch(
                        DetailSwitchName,
                        "level of detail to track with connection pool performance counters")
                    .Level == TraceLevel.Verbose;
            }
            catch
            {
                // A malformed switch value throws. Treat that as "off", matching the conservative
                // reading of an unconfigured host.
                return false;
            }
        }

        private static string Path(string counterName) =>
            $@"\{WindowsCategoryName}({InstanceNameToken})\{counterName}";

        private static int GetCurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        /// <summary>
        /// One Windows performance counter to collect, and the name to chart it under.
        /// </summary>
        public readonly struct WindowsCounter : IEquatable<WindowsCounter>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="WindowsCounter"/> struct.
            /// </summary>
            /// <param name="path">Counter path, which may carry <see cref="InstanceNameToken"/>.</param>
            /// <param name="reportAs">Name Application Insights reports the counter under.</param>
            public WindowsCounter(string path, string reportAs)
            {
                CounterPath = path;
                ReportAs = reportAs;
            }

            /// <summary>Gets the counter path, still carrying <see cref="InstanceNameToken"/>.</summary>
            public string CounterPath { get; }

            /// <summary>Gets the name Application Insights reports the counter under.</summary>
            public string ReportAs { get; }

            /// <inheritdoc/>
            public bool Equals(WindowsCounter other) =>
                string.Equals(CounterPath, other.CounterPath, StringComparison.Ordinal) &&
                string.Equals(ReportAs, other.ReportAs, StringComparison.Ordinal);

            /// <inheritdoc/>
            public override bool Equals(object? obj) => obj is WindowsCounter other && Equals(other);

            /// <inheritdoc/>
            public override int GetHashCode() =>
                (CounterPath?.GetHashCode() ?? 0) ^ (ReportAs?.GetHashCode() ?? 0);

            /// <inheritdoc/>
            public override string ToString() => $"{CounterPath} as {ReportAs}";
        }
#endif
    }
}
