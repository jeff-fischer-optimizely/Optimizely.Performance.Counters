using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Runs one <see cref="DeploymentSession"/> per process, on a heartbeat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide and idempotent for the same reason as <c>RuntimeProbes</c>: the CMS and Commerce
    /// packages are routinely installed side by side, each with its own initialization module, and
    /// two trackers would double every row without adding a fact.
    /// </para>
    /// <para>
    /// Nothing but lifetime lives here. What is scanned, compared and emitted is
    /// <see cref="DeploymentSession"/>, which has no timer and can be driven directly.
    /// </para>
    /// </remarks>
    public static class DeploymentTracker
    {
        private static readonly object Gate = new object();
        private static DeploymentSession? _session;
        private static Timer? _timer;

        /// <summary>Gets whether tracking is running in this process.</summary>
        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _session != null;
                }
            }
        }

        /// <summary>
        /// Starts tracking, unless it is already running or turned off.
        /// </summary>
        /// <param name="options">What to report and how often. Null takes the defaults.</param>
        /// <param name="optimizelyVersion">The Optimizely major, as text.</param>
        /// <param name="serviceProvider">The built container on V12 and V13; null on V11.</param>
        /// <param name="loggerFactory">Used for the tracker's log category. May be null.</param>
        /// <param name="sink">Overrides where events go; null in production.</param>
        /// <returns>True if this call is the one that started it.</returns>
        /// <remarks>
        /// Returns as soon as the timer is armed. The scan reads several hundred files, and doing
        /// that on the initialization thread would add its cost to every application start - so the
        /// first capture is due immediately but happens on the thread pool.
        /// </remarks>
        public static bool Start(
            DeploymentOptions? options,
            string optimizelyVersion,
            IServiceProvider? serviceProvider = null,
            ILoggerFactory? loggerFactory = null,
            IDeploymentEventSink? sink = null)
        {
            options ??= new DeploymentOptions();

            if (!options.Enabled)
            {
                return false;
            }

            lock (Gate)
            {
                if (_session != null)
                {
                    return false;
                }

                var logger = loggerFactory?.CreateLogger("Optimizely.Performance.Counters.Deployment");

                try
                {
                    var session = new DeploymentSession(
                        options, optimizelyVersion, serviceProvider, logger, sink);

                    var period = TimeSpan.FromMinutes(ClampMinutes(options.HeartbeatMinutes));

                    _session = session;
                    _timer = new Timer(_ => session.Capture(), null, TimeSpan.Zero, period);

                    return true;
                }
                catch (Exception ex)
                {
                    // A diagnostic must never be the reason a site fails to start. The same rule the
                    // probes and the configuration binder follow.
                    logger?.LogWarning(ex, "Deployment tracking could not be started.");

                    _timer?.Dispose();
                    _timer = null;
                    _session = null;

                    return false;
                }
            }
        }

        /// <summary>
        /// Stops tracking. Safe to call when it is not running.
        /// </summary>
        public static void Stop()
        {
            DeploymentSession? session;
            Timer? timer;

            lock (Gate)
            {
                session = _session;
                timer = _timer;
                _session = null;
                _timer = null;
            }

            try
            {
                timer?.Dispose();
                session?.Dispose();
            }
            catch
            {
                // Shutdown is not a place to raise anything.
            }
        }

        /// <summary>
        /// Clamps an interval expressed in minutes into a range a timer can be given.
        /// </summary>
        /// <param name="minutes">The configured value.</param>
        /// <returns>The value, clamped to between one minute and seven days.</returns>
        /// <remarks>
        /// Clamped rather than validated. A zero or negative interval configured by hand would
        /// otherwise be a timer firing continuously, which is a worse outcome than ignoring the
        /// value, and the upper bound stops a heartbeat so long that the divergence query has
        /// nothing to look at.
        /// </remarks>
        internal static long ClampMinutes(long minutes)
        {
            const long Week = 7 * 24 * 60;

            if (minutes < 1)
            {
                return 1;
            }

            return minutes > Week ? Week : minutes;
        }
    }
}
