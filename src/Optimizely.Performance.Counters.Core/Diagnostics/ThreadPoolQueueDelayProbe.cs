using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// Measures how long the thread pool takes to start a queued work item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Queue length counters say how many items are waiting; they do not say how long anything
    /// waits, and the two are not interchangeable. A queue of ten items is harmless if the pool
    /// drains it instantly and fatal if the pool is injecting one thread per second. This probe
    /// measures the quantity that actually matters to a request: the delay between handing work to
    /// the pool and the pool starting it. Every asynchronous continuation in the site pays that
    /// delay, which is why thread pool starvation presents as uniform slowness across endpoints
    /// that have nothing else in common.
    /// </para>
    /// <para>
    /// The sampler runs on a dedicated thread, for the reason given on <see cref="SamplingProbe"/>:
    /// a timer-driven probe is delayed by the very condition it exists to detect.
    /// </para>
    /// </remarks>
    public sealed class ThreadPoolQueueDelayProbe : SamplingProbe
    {
        private readonly IMetricTracker _metrics;
        private readonly ThreadPoolProbeOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadPoolQueueDelayProbe"/> class.
        /// </summary>
        /// <param name="metrics">Where samples are published.</param>
        /// <param name="options">Probe options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        public ThreadPoolQueueDelayProbe(
            IMetricTracker metrics,
            ThreadPoolProbeOptions? options = null,
            ILogger<ThreadPoolQueueDelayProbe>? logger = null)
            : this(metrics, options ?? new ThreadPoolProbeOptions(), (ILogger?)logger)
        {
        }

        private ThreadPoolQueueDelayProbe(
            IMetricTracker metrics,
            ThreadPoolProbeOptions options,
            ILogger? logger)
            : base(
                logger,
                "Optimizely ThreadPool Probe",
                options.Enabled,
                options.SampleInterval,
                options.LogsPerMinute)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options;
        }

        /// <inheritdoc />
        protected override void Sample()
        {
            var signal = new ManualResetEventSlim(false);
            var enqueued = Stopwatch.GetTimestamp();

            // Unsafe because the probe has no interest in the ambient execution context, and
            // capturing it would add allocation and flow cost to the thing being measured.
            ThreadPool.UnsafeQueueUserWorkItem(SignalCallback, signal);

            var completed = signal.Wait(_options.SampleTimeout);
            var delayMilliseconds = (Stopwatch.GetTimestamp() - enqueued) * 1000.0 / Stopwatch.Frequency;

            if (completed)
            {
                // Only safe to dispose once the callback has certainly run. On timeout the work
                // item is still pending and disposal would race it, so those events are left for
                // the garbage collector instead.
                signal.Dispose();
            }

            Record(delayMilliseconds, completed);
        }

        // Static so no closure is allocated per sample. The catch matters: on timeout the probe
        // abandons the event, and an ObjectDisposedException escaping here would be unhandled on a
        // pool thread, which terminates the process on .NET Core.
        private static readonly WaitCallback SignalCallback = state =>
        {
            try
            {
                ((ManualResetEventSlim)state!).Set();
            }
            catch
            {
                // The sampler already gave up on this one.
            }
        };

        private void Record(double delayMilliseconds, bool completed)
        {
            _metrics.TrackMetric(CounterNames.Runtime.ThreadPool.QueueDelayMs, delayMilliseconds);

            ThreadPool.GetMaxThreads(out var maxWorkers, out var maxCompletionPort);
            ThreadPool.GetAvailableThreads(out var freeWorkers, out var freeCompletionPort);

            var busyWorkers = maxWorkers - freeWorkers;

            _metrics.TrackMetric(CounterNames.Runtime.ThreadPool.BusyWorkerThreads, busyWorkers);
            _metrics.TrackMetric(
                CounterNames.Runtime.ThreadPool.BusyIoThreads,
                maxCompletionPort - freeCompletionPort);

            if (!completed)
            {
                _metrics.TrackMetric(CounterNames.Runtime.ThreadPool.StarvationSamples, 1);

                TryLog(log => log.LogError(
                    "The thread pool did not start a queued work item within {TimeoutSeconds:F0} s. " +
                    "{BusyWorkers} of {MaxWorkers} worker threads are in use. Every asynchronous " +
                    "continuation in the site is waiting at least this long, so requests will be " +
                    "timing out for reasons that have nothing to do with the work they are doing. " +
                    "The usual cause is blocking on asynchronous calls, which occupies threads " +
                    "faster than the pool injects replacements.",
                    _options.SampleTimeout.TotalSeconds,
                    busyWorkers,
                    maxWorkers));

                return;
            }

            if (delayMilliseconds >= _options.SlowSampleThresholdMilliseconds)
            {
                TryLog(log => log.LogWarning(
                    "Thread pool queue delay was {DelayMs:F0} ms, with {BusyWorkers} of " +
                    "{MaxWorkers} worker threads in use. Work queued to the pool is waiting this " +
                    "long before it starts, which is added to every request regardless of what the " +
                    "request itself does.",
                    delayMilliseconds,
                    busyWorkers,
                    maxWorkers));
            }
        }
    }
}
