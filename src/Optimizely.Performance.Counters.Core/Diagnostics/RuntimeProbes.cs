using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Starts and stops the probes that measure the process rather than Optimizely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide and idempotent, because the CMS and Commerce packages are routinely installed
    /// side by side and each has an initialization module that would otherwise start its own set.
    /// Two thread pool probes do not measure the pool twice as well; they measure it slightly worse
    /// and cost twice as much. Same reasoning as the <c>TryAdd</c> on the metric tracker.
    /// </para>
    /// <para>
    /// What these probes report is available on .NET 6 and later only, apart from the thread pool,
    /// which works everywhere. That is not a gap worth apologising for: the counters they replace
    /// are the ones a V11 site can already read from Windows performance counters, and the
    /// runtime APIs the others need do not exist on .NET Framework.
    /// </para>
    /// </remarks>
    public static class RuntimeProbes
    {
        private static readonly object Gate = new object();
        private static List<SamplingProbe>? _running;

        // Not a probe, so it cannot go in the list, but it has the same lifetime and the same
        // reason to be started once per process. Started and stopped here rather than given its
        // own holder, because a second holder would be a second thing each module has to remember
        // to call and the only thing it would hold is this.
        private static ProcessUptimeReporter? _uptime;

        /// <summary>
        /// Whether the probes are currently running in this process.
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _running != null;
                }
            }
        }

        /// <summary>
        /// Starts the runtime probes, unless they are already running.
        /// </summary>
        /// <param name="metrics">Where samples are published.</param>
        /// <param name="options">
        /// Probe options. Null takes the defaults throughout. <see cref="ProbeOptions.CacheLock"/>
        /// is ignored here - that probe reads an Optimizely internal and is started by the CMS
        /// module, which is the only caller that has any business running it.
        /// </param>
        /// <param name="loggerFactory">Used to give each probe its own log category. May be null.</param>
        /// <returns>True if this call is the one that started them.</returns>
        public static bool Start(
            IMetricTracker metrics,
            ProbeOptions? options = null,
            ILoggerFactory? loggerFactory = null)
        {
            if (metrics == null)
            {
                throw new ArgumentNullException(nameof(metrics));
            }

            options ??= new ProbeOptions();

            lock (Gate)
            {
                if (_running != null)
                {
                    return false;
                }

                var probes = new List<SamplingProbe>
                {
                    new ThreadPoolQueueDelayProbe(
                        metrics,
                        options.ThreadPool,
                        loggerFactory?.CreateLogger<ThreadPoolQueueDelayProbe>())
                };

#if NET6_0_OR_GREATER
                probes.Add(new GcPauseProbe(
                    metrics,
                    options.GarbageCollection,
                    loggerFactory?.CreateLogger<GcPauseProbe>()));

                probes.Add(new ContentionProbe(
                    metrics,
                    options.Contention,
                    loggerFactory?.CreateLogger<ContentionProbe>()));
#endif

                foreach (var probe in probes)
                {
                    // Start swallows its own failures, so one probe declining does not stop the
                    // others; this is belt and braces around the list itself.
                    probe.Start();
                }

                // No switch of its own. It costs one subtraction a minute, it has no dependency
                // that could fail, and the case for turning a probe off - it perturbs what it
                // measures, or it reads something the host will not give up - does not arise for
                // arithmetic on a timestamp. Optimizely:Instrumentation:Enabled still turns it off
                // along with everything else.
                _uptime = new ProcessUptimeReporter(metrics);

                _running = probes;
                return true;
            }
        }

        /// <summary>
        /// Stops the runtime probes. Safe to call when they are not running.
        /// </summary>
        public static void Stop()
        {
            List<SamplingProbe>? probes;
            ProcessUptimeReporter? uptime;

            lock (Gate)
            {
                probes = _running;
                uptime = _uptime;
                _running = null;
                _uptime = null;
            }

            try
            {
                uptime?.Dispose();
            }
            catch
            {
                // Shutdown is not a place to raise anything.
            }

            if (probes == null)
            {
                return;
            }

            foreach (var probe in probes)
            {
                try
                {
                    probe.Dispose();
                }
                catch
                {
                    // Shutdown is not a place to raise anything.
                }
            }
        }
    }
}
