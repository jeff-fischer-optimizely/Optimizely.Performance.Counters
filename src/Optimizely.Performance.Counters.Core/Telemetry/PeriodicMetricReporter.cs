using System;
using System.Threading;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Base for decorators that accumulate counts on the hot path and publish them on a timer.
    /// <para>
    /// Cache reads and event publishes happen thousands of times a second, which is far too often
    /// to write a counter per call. Those decorators increment an interlocked field instead and
    /// flush once per interval. The timer, the interval and the disposal semantics are identical
    /// wherever that pattern is used, so they live here rather than being restated per decorator.
    /// </para>
    /// </summary>
    public abstract class PeriodicMetricReporter : IDisposable
    {
        /// <summary>How often accumulated counts are published.</summary>
        protected const int ReportingIntervalMs = 60000;

        private readonly Timer _reportingTimer;

        /// <summary>
        /// Starts the flush timer. The first flush happens one full interval from now, so nothing
        /// is published before there is anything to publish.
        /// </summary>
        protected PeriodicMetricReporter() =>
            _reportingTimer = new Timer(ReportMetrics, null, ReportingIntervalMs, ReportingIntervalMs);

        /// <summary>
        /// The reporting interval in seconds, for turning a count into a rate.
        /// </summary>
        protected static double ReportingIntervalSeconds => ReportingIntervalMs / 1000.0;

        /// <summary>
        /// Publishes and resets the accumulated counts. Called on a timer thread, so it must not
        /// throw - an unhandled exception here would tear down the process.
        /// </summary>
        /// <param name="state">Unused; required by the <see cref="TimerCallback"/> signature.</param>
        protected abstract void ReportMetrics(object? state);

        /// <summary>
        /// Stops the flush timer. The decorated instance is owned by the container, not by the
        /// decorator, so derived types deliberately do not dispose what they wrap.
        /// </summary>
        public void Dispose()
        {
            _reportingTimer.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
