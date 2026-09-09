using System;
using System.Threading;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// How serious a log write was, reduced to the three bands the counters distinguish.
    /// </summary>
    /// <remarks>
    /// Deliberately coarser than any logging framework's own level set, and named after none of
    /// them. The recorder is fed by <c>Microsoft.Extensions.Logging</c> on V12 and V13 and by
    /// log4net on V11, whose level sets do not line up - log4net has Fatal and Notice, MEL has
    /// Critical and Trace - and a counter that meant a slightly different thing per version would
    /// be worse than useless when comparing two sites.
    /// </remarks>
    public enum LogWriteSeverity
    {
        /// <summary>Anything below a warning: trace, debug, information.</summary>
        Normal = 0,

        /// <summary>A warning.</summary>
        Warning = 1,

        /// <summary>An error, or worse. Covers log4net's Fatal and MEL's Critical.</summary>
        Error = 2
    }

    /// <summary>
    /// Counts log writes as they happen and publishes the rate once a minute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Logging is the one piece of site infrastructure that is on the hot path of everything and
    /// is almost never measured. A site whose write rate quietly doubles after a release is paying
    /// for it in synchronous file or network I/O on request threads, and nothing in the stock
    /// counters says so - the symptom arrives as request latency with no matching change in the
    /// work the request does. The number this publishes is the missing denominator for that
    /// conversation.
    /// </para>
    /// <para>
    /// Three counters rather than one because the volume signal and the health signal move
    /// independently, and the interesting incidents are the ones where they diverge: a flat total
    /// with a climbing error rate is a fault, a climbing total with a flat error rate is a
    /// debug level someone left switched on in production.
    /// </para>
    /// <para>
    /// Telemetry-agnostic and framework-agnostic on purpose. Nothing here knows what a
    /// <c>LogLevel</c> or a <c>LoggingEvent</c> is; the two sinks that do - the
    /// <c>ILoggerProvider</c> on V12 and V13, and the log4net appender on V11 - reduce their own
    /// vocabulary to <see cref="LogWriteSeverity"/> before they call in.
    /// </para>
    /// </remarks>
    public sealed class LogWriteRateRecorder : PeriodicMetricReporter
    {
        private readonly IMetricTracker _metrics;

        private long _writes;
        private long _warnings;
        private long _errors;

        /// <summary>
        /// Creates a recorder that publishes to the given tracker.
        /// </summary>
        /// <param name="metrics">Where the rates are published.</param>
        public LogWriteRateRecorder(IMetricTracker metrics) =>
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

        /// <summary>
        /// Records one log write.
        /// </summary>
        /// <param name="severity">How serious the write was.</param>
        /// <remarks>
        /// Called on whatever thread is doing the logging, which is every thread in the site, so
        /// it does the least it possibly can: one or two interlocked increments and no allocation,
        /// no formatting, and in particular no rendering of the message. The sinks are careful not
        /// to render either - the whole cost of measuring logging has to stay below the noise
        /// floor of the logging itself, or the measurement changes what it measures.
        /// </remarks>
        public void Record(LogWriteSeverity severity)
        {
            Interlocked.Increment(ref _writes);

            switch (severity)
            {
                case LogWriteSeverity.Warning:
                    Interlocked.Increment(ref _warnings);
                    break;

                case LogWriteSeverity.Error:
                    Interlocked.Increment(ref _errors);
                    break;
            }
        }

        /// <summary>
        /// Publishes the rates for the interval just ended and starts the next one.
        /// </summary>
        /// <param name="state">Unused; required by the timer signature.</param>
        /// <remarks>
        /// Zeros are published rather than skipped. For a rate counter, "the site logged nothing
        /// this minute" is a reading, and on a site that logs continuously it is an alarming one -
        /// a gap in the series would look identical to the collector not running.
        /// <para>
        /// Nothing here writes to the log, unlike every other reporter in this package. A recorder
        /// that logged once per flush would put a floor under its own counter and, on a site with
        /// nothing else logging, would be the only thing it ever measured.
        /// </para>
        /// </remarks>
        protected override void ReportMetrics(object? state)
        {
            try
            {
                var writes = Interlocked.Exchange(ref _writes, 0);
                var warnings = Interlocked.Exchange(ref _warnings, 0);
                var errors = Interlocked.Exchange(ref _errors, 0);

                _metrics.TrackMetric(
                    CounterNames.Runtime.Logging.WritesPerSecond, writes / ReportingIntervalSeconds);
                _metrics.TrackMetric(
                    CounterNames.Runtime.Logging.WarningsPerSecond, warnings / ReportingIntervalSeconds);
                _metrics.TrackMetric(
                    CounterNames.Runtime.Logging.ErrorsPerSecond, errors / ReportingIntervalSeconds);
            }
            catch
            {
                // On a timer thread, so an escape here would take the process down. There is also
                // nowhere to report it: the thing that has just failed is the logging counter, and
                // the obvious place to say so is the log.
            }
        }
    }
}
