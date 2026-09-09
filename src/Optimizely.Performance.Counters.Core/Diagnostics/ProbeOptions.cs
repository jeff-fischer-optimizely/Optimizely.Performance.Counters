using System;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Options for all four probes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bound from the host's configuration under
    /// <c>Optimizely:Instrumentation:Probes</c> - see
    /// <c>Optimizely.Performance.Counters.Core.Configuration.InstrumentationConfiguration</c>.
    /// A host that would rather not configure through a file can construct this and pass it to
    /// <see cref="RuntimeProbes.Start"/> itself; nothing here reads configuration on its own.
    /// </para>
    /// <para>
    /// <see cref="CacheLock"/> sits alongside the other three even though
    /// <see cref="RuntimeProbes"/> does not start it. The cache lock probe reads an Optimizely
    /// internal, so it belongs to the CMS package and is started by the CMS module - but an
    /// operator setting it thinks of it as one probe among four, and splitting it into a second
    /// configuration section to match our assembly boundaries would be organising their file
    /// around our build.
    /// </para>
    /// </remarks>
    public class ProbeOptions
    {
        /// <summary>Options for the thread pool queue delay probe.</summary>
        public ThreadPoolProbeOptions ThreadPool { get; set; } = new ThreadPoolProbeOptions();

        /// <summary>Options for the garbage collection pause probe.</summary>
        public GcPauseProbeOptions GarbageCollection { get; set; } = new GcPauseProbeOptions();

        /// <summary>Options for the lock contention probe.</summary>
        public ContentionProbeOptions Contention { get; set; } = new ContentionProbeOptions();

        /// <summary>Options for the cache lock probe, started by the CMS package.</summary>
        public CacheLockProbeOptions CacheLock { get; set; } = new CacheLockProbeOptions();
    }

    /// <summary>
    /// Options for <see cref="ThreadPoolQueueDelayProbe"/>.
    /// </summary>
    public class ThreadPoolProbeOptions
    {
        /// <summary>Gets or sets whether the probe runs at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the seconds between samples.
        /// </summary>
        /// <remarks>
        /// Twelve samples a minute is enough to produce a meaningful maximum over a collection
        /// interval without the probe itself becoming load. Each sample is one queued work item.
        /// </remarks>
        public int SampleIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets how long to wait for a sample before giving up on it.
        /// </summary>
        /// <remarks>
        /// A sample that does not complete inside this window is recorded as starvation. The
        /// duration recorded is a floor, not a measurement: the true delay was at least this long
        /// and may have been far longer.
        /// </remarks>
        public int SampleTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets the delay, in milliseconds, above which a sample is logged.
        /// </summary>
        /// <remarks>
        /// Well clear of ordinary scheduling jitter. A healthy pool services a queued item in
        /// under a millisecond; tens of milliseconds means requests are waiting on threads before
        /// any of their own work begins.
        /// </remarks>
        public int SlowSampleThresholdMilliseconds { get; set; } = 100;

        /// <summary>Gets or sets the cap on probe log entries per minute.</summary>
        public int LogsPerMinute { get; set; } = 4;

        internal TimeSpan SampleInterval => TimeSpan.FromSeconds(Math.Max(1, SampleIntervalSeconds));

        internal TimeSpan SampleTimeout => TimeSpan.FromSeconds(Math.Max(1, SampleTimeoutSeconds));
    }

    /// <summary>
    /// Options for the garbage collection pause probe.
    /// </summary>
    /// <remarks>
    /// Compiled on every target framework, though the probe it configures runs on .NET 6 and later
    /// only. The alternative - conditioning the type on the target framework - would mean the
    /// configuration file an operator writes has a different shape on V11, and the same template
    /// could not be handed to every version. Ignored settings are cheaper than a per-version schema.
    /// </remarks>
    public class GcPauseProbeOptions
    {
        /// <summary>Gets or sets whether the probe runs at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the seconds between samples.
        /// </summary>
        /// <remarks>
        /// A sample reads state the runtime has already recorded and does not itself provoke a
        /// collection. The interval governs how many individual pauses are sampled, not how much
        /// of the total is captured - see the probe for why those differ.
        /// </remarks>
        public int SampleIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets the pause duration, in milliseconds, above which a sample is logged.
        /// </summary>
        public int SlowPauseThresholdMilliseconds { get; set; } = 200;

        /// <summary>Gets or sets the cap on probe log entries per minute.</summary>
        public int LogsPerMinute { get; set; } = 4;

        internal TimeSpan SampleInterval => TimeSpan.FromSeconds(Math.Max(1, SampleIntervalSeconds));
    }

    /// <summary>
    /// Options for the lock contention probe and its triggered capture bursts.
    /// </summary>
    /// <remarks>
    /// Compiled on every target framework though the probe runs on .NET 6 and later only, for the
    /// reason given on <see cref="GcPauseProbeOptions"/>.
    /// </remarks>
    public class ContentionProbeOptions
    {
        /// <summary>Gets or sets whether the probe runs at all.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether a capture burst may be triggered.
        /// </summary>
        /// <remarks>
        /// Turning this off leaves the contention rate running. That is the free half of the probe
        /// and it stays useful alone; what it cannot tell you is how long the waits were, which is
        /// the whole reason the burst exists.
        /// </remarks>
        public bool BurstCaptureEnabled { get; set; } = true;

        /// <summary>Gets or sets the seconds between rate samples.</summary>
        public int SampleIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets the contentions per second at or above which a burst is triggered.
        /// </summary>
        /// <remarks>
        /// Contention is normal, and a busy site produces a steady background rate that costs
        /// nothing measurable because most waits resolve in microseconds. The default sits well
        /// above that, so a burst means something changed rather than that the site is serving
        /// traffic.
        /// </remarks>
        public int BurstTriggerContentionsPerSecond { get; set; } = 500;

        /// <summary>
        /// Gets or sets how long a capture burst listens for.
        /// </summary>
        /// <remarks>
        /// Short on purpose. During a burst the runtime raises one event per contention, which at
        /// the rate that triggered it is a great many events - the window is sized to characterise
        /// the wait distribution, not to observe the whole episode.
        /// </remarks>
        public int BurstDurationSeconds { get; set; } = 3;

        /// <summary>Gets or sets the minimum seconds between the end of one burst and the next.</summary>
        public int BurstCooldownSeconds { get; set; } = 300;

        /// <summary>
        /// Gets or sets the maximum number of bursts in any rolling hour.
        /// </summary>
        /// <remarks>
        /// A second limit behind the cooldown, because what this guards against is a site that
        /// sits above the trigger threshold indefinitely. The cooldown alone would let that site
        /// capture twelve times an hour forever.
        /// </remarks>
        public int MaxBurstsPerHour { get; set; } = 6;

        /// <summary>
        /// Gets or sets the maximum number of wait samples retained during a burst.
        /// </summary>
        /// <remarks>
        /// Bounds what a burst can cost in memory. Past this the burst keeps counting contentions
        /// but stops retaining durations, so the count stays exact while the percentiles describe
        /// the window's opening rather than all of it.
        /// </remarks>
        public int MaxSamplesPerBurst { get; set; } = 20000;

        /// <summary>Gets or sets the cap on probe log entries per minute.</summary>
        public int LogsPerMinute { get; set; } = 2;

        internal TimeSpan SampleInterval => TimeSpan.FromSeconds(Math.Max(1, SampleIntervalSeconds));

        internal TimeSpan BurstDuration =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, BurstDurationSeconds)));

        internal TimeSpan BurstCooldown => TimeSpan.FromSeconds(Math.Max(0, BurstCooldownSeconds));

        internal int SampleCap => Math.Max(1, MaxSamplesPerBurst);
    }

    /// <summary>
    /// Options for the cache lock contention probe.
    /// </summary>
    /// <remarks>
    /// Declared here rather than beside the probe, which lives in the CMS package, so that the
    /// whole options tree can be bound from one configuration section by an assembly both packages
    /// share. Core already owns the CMS counter <em>names</em> for the same reason: what Core
    /// avoids is a reference on the EPiServer assemblies, not knowledge that CMS exists.
    /// </remarks>
    public class CacheLockProbeOptions
    {
        /// <summary>
        /// Gets or sets whether the probe runs at all.
        /// </summary>
        /// <remarks>
        /// This is the switch that turns off the only reflection over Optimizely internals in the
        /// package. A site that would rather not have it can set this to false and lose nothing
        /// else.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the seconds between samples.
        /// </summary>
        /// <remarks>
        /// A sample is four property reads, so this can be frequent. Lock queues form and clear in
        /// well under a second, and a sparse sample will miss them entirely - the point of the
        /// metric is the distribution of a spiky quantity, which needs enough samples per
        /// aggregation interval to have a meaningful maximum.
        /// </remarks>
        public int SampleIntervalSeconds { get; set; } = 2;

        /// <summary>
        /// Gets or sets the total queue depth above which a sample is logged.
        /// </summary>
        /// <remarks>
        /// Set to zero to disable logging and keep only the metrics.
        /// </remarks>
        public int QueueDepthThreshold { get; set; } = 10;

        /// <summary>Gets or sets the cap on probe log entries per minute.</summary>
        public int LogsPerMinute { get; set; } = 4;

        /// <summary>
        /// <see cref="SampleIntervalSeconds"/> as a <see cref="TimeSpan"/>, floored at one second.
        /// </summary>
        /// <remarks>
        /// Public, unlike the identical helper on every other options class in this file, because
        /// this is the one whose probe lives in another assembly - <c>CacheLockProbe</c> is in the
        /// CMS package, which has no <c>InternalsVisibleTo</c> here and should not need one for a
        /// unit conversion.
        /// </remarks>
        public TimeSpan SampleInterval => TimeSpan.FromSeconds(Math.Max(1, SampleIntervalSeconds));
    }
}
