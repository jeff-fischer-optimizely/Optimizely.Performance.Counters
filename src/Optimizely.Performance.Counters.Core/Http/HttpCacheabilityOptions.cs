namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Options for the outbound response cacheability counters.
    /// </summary>
    /// <remarks>
    /// Sits beside <c>Logging</c> in the configuration tree rather than under <c>Probes</c>, for the
    /// same reason: nothing here goes and looks at anything on a timer. It reads four headers on the
    /// thread that is already sending the response, which is a different bargain for an operator to
    /// accept and belongs where they will see it.
    /// </remarks>
    public class HttpCacheabilityOptions
    {
        /// <summary>
        /// Gets or sets whether outbound responses are classified at all.
        /// </summary>
        /// <remarks>
        /// False and nothing is inserted into the response pipeline: no middleware is added on V12
        /// and V13, the HTTP module returns without subscribing on V11, and the nine counters stay
        /// empty. This removes the measurement from the path rather than leaving it in place
        /// reporting zero, which is the switch to reach for if response latency is itself under
        /// suspicion.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether responses that are shared-cacheable and also set a cookie are
        /// written to the log, naming the path.
        /// </summary>
        /// <remarks>
        /// The counter says how much of the site is doing this; only the log can say which part of
        /// it is. A counter cannot carry a path - paths are unbounded, and one time series per path
        /// would be throttled long before it became useful - so this is the only way to get from
        /// "eight percent of responses are wasting their cache headers" to a route to go and fix.
        /// </remarks>
        public bool LogSharedCacheConflicts { get; set; } = true;

        /// <summary>
        /// Gets or sets the cap on shared-cache conflict log entries per minute.
        /// </summary>
        /// <remarks>
        /// Low by default because the finding repeats: one misconfigured route serving steadily
        /// produces the same log entry thousands of times a minute, and the second one adds nothing
        /// the first did not already say.
        /// </remarks>
        public int SharedCacheConflictLogsPerMinute { get; set; } = 5;

        /// <summary>
        /// Gets or sets the number of consecutive failures after which the recorder switches itself
        /// off for the remaining lifetime of the process.
        /// </summary>
        public int FailureThreshold { get; set; } = 20;
    }
}
