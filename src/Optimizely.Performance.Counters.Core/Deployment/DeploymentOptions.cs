namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// How often, and how completely, the deployed assembly set is reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every leaf here is a bool, an int or a string, because those are the three types
    /// <c>InstrumentationConfiguration</c> binds. A <c>TimeSpan</c> would bind silently to nothing,
    /// so the two intervals are named in the unit they are read in.
    /// </para>
    /// </remarks>
    public class DeploymentOptions
    {
        /// <summary>
        /// Gets or sets whether the deployed assembly set is reported at all.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets an explicit directory for the state file, overriding the probe order in
        /// <see cref="DeploymentStateStore"/>.
        /// </summary>
        /// <remarks>
        /// Set this when the host has a durable path that the probe cannot be expected to guess -
        /// a mounted volume, say. An unusable value is logged and fallen past rather than honoured,
        /// on the same reasoning as every other setting here: a diagnostic must not be the reason a
        /// site fails to start.
        /// </remarks>
        public string? StatePath { get; set; }

        /// <summary>
        /// Gets or sets when the per-assembly inventory rows are emitted: <c>Never</c>,
        /// <c>OnChange</c> or <c>Always</c>. Unrecognised values fall back to <c>OnChange</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>OnChange</c> emits once per process start, again whenever the fingerprint moves while
        /// the process is running, and again on the <see cref="InventoryReemitHours"/> interval.
        /// Once per process start is included deliberately and is not a contradiction of the name:
        /// on a host with no durable state every start is a baseline, so an inventory gated purely
        /// on a detected change would never be emitted at all on the topology this was built for,
        /// and the question it exists to answer would have no answer there. The cost is one row per
        /// assembly per restart, and duplicate reports of the same fingerprint collapse in the query
        /// because the fingerprint is content-addressed.
        /// </para>
        /// <para>
        /// <c>Always</c> adds every heartbeat on top of that. It is expensive - a few hundred rows
        /// every <see cref="HeartbeatMinutes"/> per instance - and exists only for the case where
        /// ingestion sampling is dropping enough that one report per start cannot be relied on.
        /// </para>
        /// </remarks>
        public string InventoryMode { get; set; } = "OnChange";

        /// <summary>
        /// Gets or sets how often the one-row manifest is re-emitted.
        /// </summary>
        /// <remarks>
        /// This is what makes divergence detectable. An instance that started days ago and is
        /// healthy is otherwise silent, so a query asking "what is running right now" would see
        /// only the instances that happened to restart recently. The interval has to be shorter
        /// than the window any alert on it uses; 15 minutes against an observed rollout
        /// convergence time of around 26 minutes leaves room for both.
        /// <para>
        /// One row per instance per interval - on a six-instance farm, under 600 rows a day.
        /// </para>
        /// </remarks>
        public int HeartbeatMinutes { get; set; } = 15;

        /// <summary>
        /// Gets or sets how often the full inventory is re-emitted even when nothing has changed.
        /// </summary>
        /// <remarks>
        /// Without this, the answer to "which DLLs are on this environment" disappears once the
        /// last deployment ages out of the retention window, and a long-lived stable environment is
        /// exactly the one where that happens. Weekly against a 90-day retention is twelve chances
        /// to still have the answer.
        /// </remarks>
        public int InventoryReemitHours { get; set; } = 168;

        /// <summary>
        /// Gets or sets whether <c>System.*</c> and <c>Microsoft.*</c> assemblies are included.
        /// </summary>
        /// <remarks>
        /// True by default, which is the opposite of what it sounds like it should be. Only the
        /// application's own deployment directory is scanned, and on a framework-dependent
        /// deployment the shared framework is not in it - so the <c>System.*</c> files present are
        /// the ones this deployment actually ships and pins, which is precisely the set somebody
        /// asks about when a CVE lands. Excluding them saves a few hundred rows a week and loses
        /// the reason to have the data.
        /// </remarks>
        public bool IncludeSystemAssemblies { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the full path of each assembly is reported alongside its name.
        /// </summary>
        /// <remarks>
        /// Off by default. The paths add nothing a file name does not already give - everything
        /// scanned is in one directory - and they leak the host's directory layout, which on some
        /// hosts includes an account name, to anyone holding Application Insights reader rights.
        /// </remarks>
        public bool IncludeFilePaths { get; set; }

        /// <summary>
        /// Gets or sets whether the deployment fingerprint is stamped onto all other telemetry.
        /// </summary>
        /// <remarks>
        /// On by default, because the stamp is what turns this from a list of DLLs into an answer
        /// about a regression: with it, "did the deployment do this" is a group-by on
        /// <c>requests</c> rather than a join against a guessed time range. It costs one short
        /// dimension per telemetry item - about 45 bytes with the JSON overhead, so roughly a
        /// dollar a month on a site ingesting ten million items.
        /// <para>
        /// This only ever adds the custom dimension. It does not touch
        /// <c>Component.Version</c>/<c>application_Version</c>, which belongs to the host - see
        /// <see cref="DeploymentFingerprintInitializer"/>.
        /// </para>
        /// </remarks>
        public bool StampTelemetry { get; set; } = true;

        /// <summary>
        /// Gets or sets the cap on per-assembly change rows emitted for a single transition.
        /// </summary>
        /// <remarks>
        /// A normal deployment changes a handful of assemblies. A framework upgrade changes all of
        /// them, and there is no value in three hundred rows saying so when the manifest already
        /// carries the counts - so past this many the rest are summarised rather than listed.
        /// </remarks>
        public int MaxChangeEvents { get; set; } = 200;

        /// <summary>
        /// Gets or sets the cap on assemblies recorded in one manifest.
        /// </summary>
        /// <remarks>
        /// A guard against a scan directory that is not what this expects - a shared drop folder,
        /// or a host that puts every version of everything side by side. Hitting it is reported on
        /// the manifest as a truncation rather than passed off as a complete inventory.
        /// </remarks>
        public int MaxAssemblies { get; set; } = 2000;
    }
}
