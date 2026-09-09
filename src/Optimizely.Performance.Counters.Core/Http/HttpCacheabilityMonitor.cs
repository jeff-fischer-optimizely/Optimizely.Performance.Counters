using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Owns the process-wide <see cref="HttpCacheabilityRecorder"/> that the response sinks report
    /// to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide and idempotent, for the same reason <c>LogWriteRateMonitor</c> is: the CMS and
    /// Commerce packages are routinely installed together and each has an initialization module
    /// that would otherwise start its own. Two recorders would not measure the traffic twice as
    /// well - they would each see every response, so the response rate would read double while the
    /// shares, being shares, read correctly. That combination is worse than either being wrong.
    /// </para>
    /// <para>
    /// The recorder is reached through <see cref="Current"/> rather than handed to the sinks. On
    /// V11 the HTTP module is constructed by ASP.NET before any Optimizely initialization module
    /// has run, and on V12 and V13 the middleware is built when the host builds its pipeline; in
    /// neither case is there anything to hand it yet. Both sinks are written to treat a null here
    /// as "do not measure", so they sit inert until this publishes one and go inert again when it
    /// is cleared.
    /// </para>
    /// </remarks>
    public static class HttpCacheabilityMonitor
    {
        private static readonly object Gate = new object();

        private static HttpCacheabilityRecorder? _recorder;

        /// <summary>
        /// The live recorder, or null when nothing has started one.
        /// </summary>
        /// <remarks>
        /// Null is the normal state before initialization and after shutdown. Responses sent while
        /// the site is still starting are therefore not classified, which is correct: they are
        /// startup traffic, and the request that triggered initialization would otherwise be
        /// measured under conditions no later request will ever see again.
        /// </remarks>
        public static HttpCacheabilityRecorder? Current => Volatile.Read(ref _recorder);

        /// <summary>
        /// Whether outbound responses are currently being classified in this process.
        /// </summary>
        public static bool IsRunning => Current != null;

        /// <summary>
        /// Starts classifying outbound responses, unless something already is.
        /// </summary>
        /// <param name="metrics">Where the shares are published.</param>
        /// <param name="options">Options. Null takes the defaults.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>True if this call is the one that started it.</returns>
        public static bool Start(
            IMetricTracker metrics,
            HttpCacheabilityOptions? options = null,
            ILogger? logger = null)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            options ??= new HttpCacheabilityOptions();

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

                // The logger goes to the recorder as well as being used here. It is the recorder
                // that finds the shared-cache conflicts, and a finding nobody can read is not one.
                Volatile.Write(ref _recorder, new HttpCacheabilityRecorder(metrics, options, logger));

                logger?.LogInformation(
                    "Outbound response cacheability counters are active. Every response this " +
                    "process sends is classified from its Cache-Control and Expires headers as it " +
                    "goes out; the nine Optimizely.Runtime.Http counters report the mix once a " +
                    "minute.");

                return true;
            }
        }

        /// <summary>
        /// Stops classifying outbound responses. Safe to call when nothing is running.
        /// </summary>
        public static void Stop()
        {
            HttpCacheabilityRecorder? recorder;

            lock (Gate)
            {
                recorder = _recorder;

                // Cleared first, so that a response racing this one sees null and does nothing
                // rather than reaching a recorder whose timer is about to be disposed.
                Volatile.Write(ref _recorder, null);
            }

            try
            {
                recorder?.Dispose();
            }
            catch
            {
                // Shutdown is not a place to raise anything.
            }
        }
    }
}
