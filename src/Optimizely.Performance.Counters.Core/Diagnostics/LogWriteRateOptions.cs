namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Options for the log write rate counters.
    /// </summary>
    /// <remarks>
    /// Not a probe, and deliberately not under <c>Probes</c> in the configuration file. A probe
    /// goes and looks at something on its own thread; this counts events as the site produces
    /// them, on the site's own threads, which is a different bargain for an operator to accept and
    /// belongs where they will see it.
    /// </remarks>
    public class LogWriteRateOptions
    {
        /// <summary>
        /// Gets or sets whether log writes are counted at all.
        /// </summary>
        /// <remarks>
        /// False and nothing is attached to the host's logging: no <c>ILoggerProvider</c> is
        /// registered on V12 and V13, no appender is added on V11, and the three counters stay
        /// empty. This is the switch to reach for if the site's logging is itself under suspicion,
        /// because it removes the measurement from the path entirely rather than leaving it in
        /// place reporting zero.
        /// </remarks>
        public bool Enabled { get; set; } = true;
    }
}
