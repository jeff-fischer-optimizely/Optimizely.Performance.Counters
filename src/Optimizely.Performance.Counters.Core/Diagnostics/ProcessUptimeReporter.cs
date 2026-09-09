using System;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.Runtime.Process;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Publishes how long the worker process has been running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cheapest counter in the package and the one that most often changes what a chart means.
    /// Every rate and average here describes a warm process; for the first minutes after a recycle
    /// they all describe a cold one instead, and read as a regression. Overlay this and the
    /// question answers itself.
    /// </para>
    /// <para>
    /// A reporter rather than a <see cref="SamplingProbe"/>, and the distinction is not
    /// bookkeeping. Probes exist because their subject has to be caught in the act - a thread pool
    /// delay has to be provoked, a lock's queue has to be read while threads are on it - so they
    /// each own a thread and sample on their own schedule. Uptime is arithmetic on a timestamp
    /// taken once. It loses nothing to a late sample, so it does not get a thread.
    /// </para>
    /// </remarks>
    public sealed class ProcessUptimeReporter : PeriodicMetricReporter
    {
        private readonly IMetricTracker _metrics;
        private readonly DateTime _startedUtc;

        /// <summary>
        /// Creates a reporter that publishes to the given tracker.
        /// </summary>
        /// <param name="metrics">Where the uptime is published.</param>
        public ProcessUptimeReporter(IMetricTracker metrics)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _startedUtc = ResolveProcessStartUtc();
        }

        /// <summary>
        /// Publishes the current uptime.
        /// </summary>
        /// <param name="state">Unused; required by the timer signature.</param>
        /// <remarks>
        /// A gauge, not a rate, so nothing is reset here. The value climbs for the life of the
        /// process and the reset is the process itself - which is the entire point of the counter.
        /// </remarks>
        protected override void ReportMetrics(object? state)
        {
            try
            {
                var uptime = (DateTime.UtcNow - _startedUtc).TotalSeconds;

                // A clock that went backwards - a virtual machine resuming, an NTP correction -
                // would otherwise publish a negative age, which no consumer of this counter is
                // prepared for. Zero is the honest floor.
                _metrics.TrackMetric(Names.UptimeSeconds, uptime > 0.0 ? uptime : 0.0);
            }
            catch
            {
                // On a timer thread, where an escape would take the whole site down over a
                // counter nobody would miss for a minute.
            }
        }

        /// <remarks>
        /// The real process start time when the host will give it up, which is the number worth
        /// having: it includes site startup, so a site that takes three minutes to warm shows
        /// three minutes rather than nothing. The call needs permissions that a hardened .NET
        /// Framework host can withhold, and on some containers it reads the container's start
        /// rather than the process's, so a failure falls back to the moment instrumentation
        /// started. That undercounts by the startup duration and by nothing else, which for the
        /// question this counter answers - did it restart, and when - is close enough to be worth
        /// having over no counter at all.
        /// </remarks>
        private static DateTime ResolveProcessStartUtc()
        {
            try
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                var started = current.StartTime.ToUniversalTime();

                // A start time in the future, or before this machine plausibly booted, means the
                // host handed back something that is not a start time. Prefer an honest
                // undercount to a nonsense number.
                if (started > DateTime.UtcNow || started < DateTime.UtcNow.AddYears(-1))
                {
                    return DateTime.UtcNow;
                }

                return started;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException ||
                ex is NotSupportedException ||
                ex is PlatformNotSupportedException ||
                ex is UnauthorizedAccessException ||
                ex is System.ComponentModel.Win32Exception)
            {
                return DateTime.UtcNow;
            }
        }
    }
}
