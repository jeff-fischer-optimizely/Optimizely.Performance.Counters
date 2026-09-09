using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Owns the process-wide <see cref="LogWriteRateRecorder"/> and, on V11, the log4net appender
    /// that feeds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide and idempotent, for the same reason <see cref="RuntimeProbes"/> is: the CMS and
    /// Commerce packages are routinely installed together and each has an initialization module
    /// that would otherwise start its own. Two recorders would not measure logging twice as well.
    /// They would double every counter, because each sink would feed both.
    /// </para>
    /// <para>
    /// The recorder is reached through <see cref="Current"/> rather than handed to the sinks,
    /// because on V12 and V13 the sink is an <c>ILoggerProvider</c> the host constructs while it
    /// is building its logging - which happens before any initialization module runs, so there is
    /// nothing to hand it yet. Reading a volatile static per write costs less than the interlocked
    /// increment that follows it.
    /// </para>
    /// </remarks>
    public static class LogWriteRateMonitor
    {
        private static readonly object Gate = new object();

        private static LogWriteRateRecorder? _recorder;
        private static IDisposable? _attachment;

        /// <summary>
        /// The live recorder, or null when nothing has started one.
        /// </summary>
        /// <remarks>
        /// Null is the normal state before initialization and after shutdown, and the sinks are
        /// written to treat it as "do not count" rather than as a fault. Writes made before the
        /// site finished starting are therefore not counted, which is correct: they are startup,
        /// not steady state, and including them would put a spike at the left edge of every chart.
        /// </remarks>
        public static LogWriteRateRecorder? Current => Volatile.Read(ref _recorder);

        /// <summary>
        /// Whether log writes are currently being counted in this process.
        /// </summary>
        public static bool IsRunning => Current != null;

        /// <summary>
        /// Starts counting log writes, unless something already is.
        /// </summary>
        /// <param name="metrics">Where the rates are published.</param>
        /// <param name="options">Options. Null takes the defaults.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>True if this call is the one that started it.</returns>
        public static bool Start(
            IMetricTracker metrics,
            LogWriteRateOptions? options = null,
            ILogger? logger = null)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            options ??= new LogWriteRateOptions();

            if (!options.Enabled)
            {
                return false;
            }

            lock (Gate)
            {
                if (_recorder != null)
                {
                    return false;
                }

                var recorder = new LogWriteRateRecorder(metrics);

                // Published before the sink is attached, not after. On V11 the appender starts
                // receiving events the instant it is added, and it reads the recorder from here.
                Volatile.Write(ref _recorder, recorder);

                try
                {
                    _attachment = Attach(recorder, logger);
                }
                catch (Exception ex)
                {
                    // A site with no logging counters is a gap in a chart. A site that will not
                    // start is an outage.
                    logger?.LogWarning(
                        ex,
                        "Log write rate counters could not attach to the host's logging. The three " +
                        "Optimizely.Runtime.Logging counters will stay at zero; nothing else is affected.");
                }

                return true;
            }
        }

        /// <summary>
        /// Stops counting log writes. Safe to call when nothing is running.
        /// </summary>
        public static void Stop()
        {
            LogWriteRateRecorder? recorder;
            IDisposable? attachment;

            lock (Gate)
            {
                recorder = _recorder;
                attachment = _attachment;

                // Cleared first, so that any write racing this one sees null and does nothing
                // rather than reaching a recorder whose timer is about to be disposed.
                Volatile.Write(ref _recorder, null);
                _attachment = null;
            }

            try
            {
                attachment?.Dispose();
            }
            catch
            {
                // Shutdown is not a place to raise anything.
            }

            try
            {
                recorder?.Dispose();
            }
            catch
            {
                // As above.
            }
        }

#if NET472
        /// <remarks>
        /// V11 logs through log4net, so the sink has to be installed rather than registered - see
        /// <see cref="Log4NetWriteRateSink"/> for what "installed" means and what it costs.
        /// </remarks>
        private static IDisposable? Attach(LogWriteRateRecorder recorder, ILogger? logger) =>
            Log4NetWriteRateSink.TryAttach(recorder, logger);
#else
        /// <remarks>
        /// Nothing to attach. On V12 and V13 the sink is <see cref="LogWriteRateLoggerProvider"/>,
        /// which the host constructs as part of its own logging and which finds the recorder
        /// through <see cref="Current"/>. Registering it is the initialization module's job and
        /// happens while the container is being configured, long before this runs.
        /// </remarks>
        private static IDisposable? Attach(LogWriteRateRecorder recorder, ILogger? logger)
        {
            _ = recorder;

            logger?.LogInformation(
                "Log write rate counters are active. Writes are counted at the host's default " +
                "minimum level, which is what an unconfigured logging provider sees.");

            return null;
        }
#endif
    }
}
