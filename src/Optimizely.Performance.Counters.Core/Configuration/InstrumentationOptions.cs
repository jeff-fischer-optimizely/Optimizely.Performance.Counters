using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Http;

namespace Optimizely.Performance.Counters.Core.Configuration
{
    /// <summary>
    /// Everything a site operator can change about this package, in the shape the configuration
    /// file has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One tree, bound from one section, on every Optimizely version. V12 and V13 read it from
    /// <c>appsettings.json</c>; V11 reads the same paths from <c>appSettings</c> keys in
    /// <c>web.config</c>. <see cref="InstrumentationConfiguration"/> is what does the reading.
    /// </para>
    /// <para>
    /// The section is <c>Optimizely:Instrumentation</c> rather than
    /// <c>Optimizely:PerformanceCounters</c>, which belongs to
    /// <c>Optimizely.Performance.DotNetCounters</c>. The two packages are routinely installed
    /// together and the names have to be distinct; this one is the instrumentation package - it
    /// measures things nothing else publishes - so that is what its section is called.
    /// </para>
    /// </remarks>
    public class InstrumentationOptions
    {
        /// <summary>
        /// The configuration section these options are read from.
        /// </summary>
        public const string SectionName = "Optimizely:Instrumentation";

        /// <summary>
        /// Gets or sets whether the package does anything at all.
        /// </summary>
        /// <remarks>
        /// The one switch that has to work even if everything else is misconfigured. False and the
        /// initialization modules return immediately: nothing is decorated, no probe starts, no
        /// counter is registered with Application Insights, and the only trace left is a single log
        /// line saying so. It exists so that an operator suspecting this package during an incident
        /// can rule it out with a setting rather than a deployment.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the probe options.</summary>
        public ProbeOptions Probes { get; set; } = new ProbeOptions();

        /// <summary>Gets or sets the cache instrumentation options.</summary>
        public CacheOptions Cache { get; set; } = new CacheOptions();

        /// <summary>Gets or sets the log write rate options.</summary>
        public LogWriteRateOptions Logging { get; set; } = new LogWriteRateOptions();

        /// <summary>Gets or sets the outbound response cacheability options.</summary>
        public HttpCacheabilityOptions Http { get; set; } = new HttpCacheabilityOptions();
    }

    /// <summary>
    /// Options for the cache instrumentation that is not a probe.
    /// </summary>
    /// <remarks>
    /// A node of its own rather than a single property, because the cache is measured in two
    /// unrelated ways: the cascade recorder counts what an invalidation discards, and the cache
    /// lock probe - under <see cref="ProbeOptions.CacheLock"/> - counts who is queued behind it.
    /// </remarks>
    public class CacheOptions
    {
        /// <summary>Gets or sets the cache dependency cascade options.</summary>
        public CacheCascadeOptions Cascade { get; set; } = new CacheCascadeOptions();
    }

    /// <summary>
    /// Options for cache dependency cascade instrumentation.
    /// </summary>
    /// <remarks>
    /// Declared in Core rather than beside <c>CacheCascadeRecorder</c> in the CMS package, so that
    /// the whole tree binds from one section - the same reasoning as
    /// <see cref="CacheLockProbeOptions"/>.
    /// </remarks>
    public class CacheCascadeOptions
    {
        /// <summary>
        /// Gets or sets whether cascades are measured at all.
        /// </summary>
        /// <remarks>
        /// False and <c>IMemoryCache</c> is left undecorated, so this removes the cost as well as
        /// the nine cascade counters. The cache hit, miss and invalidation rates are unaffected -
        /// they are measured a layer up, at <c>ISynchronizedObjectInstanceCache</c>, and that
        /// decorator reports rates only when nothing is counting entries beneath it.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the size of cascade at or above which the cascade is also written to the
        /// log, naming the key that triggered it.
        /// </summary>
        /// <remarks>
        /// Telemetry gives the shape of the problem; the log gives the culprit. A counter cannot
        /// carry a cache key - keys are unbounded, and one time series per key would be throttled
        /// long before it became useful - so logging is the only way to find out <em>which</em> key
        /// is collapsing the cache. The threshold keeps that to the few events worth reading.
        /// </remarks>
        public int LargeRemovalThreshold { get; set; } = 1000;

        /// <summary>
        /// Gets or sets whether cascades at or above <see cref="LargeRemovalThreshold"/> are
        /// logged. Turn this off to keep the counters and silence the log.
        /// </summary>
        public bool LogLargeRemovals { get; set; } = true;

        /// <summary>Gets or sets the cap on large-removal log entries per minute.</summary>
        public int LargeRemovalLogsPerMinute { get; set; } = 10;

        /// <summary>
        /// Gets or sets the number of consecutive failures after which the recorder switches itself
        /// off for the remaining lifetime of the process.
        /// </summary>
        public int FailureThreshold { get; set; } = 20;
    }
}
