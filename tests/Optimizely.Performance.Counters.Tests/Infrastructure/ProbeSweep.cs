using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Diagnostics;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Drives every probe available on this target framework through enough of its surface to emit
    /// each counter it owns.
    /// <para>
    /// The probe counterpart to <see cref="DecoratorSweep"/>. Where a decorator emits because
    /// something called it, a probe emits because it went and looked, so driving one means putting
    /// the runtime into the state it looks for: forcing collections for the GC probe, and generating
    /// real lock contention for the contention probe. That makes this slower than the decorator
    /// sweep and worth more - it is the only place the <c>EventListener</c> capture path runs
    /// end to end.
    /// </para>
    /// </summary>
    public static class ProbeSweep
    {
        /// <summary>
        /// Registered counters <see cref="DriveEverything"/> cannot produce, and why.
        /// <para>
        /// Both are reachable only by degrading the process the test run is sharing, which would
        /// make every other test in the assembly slower and less deterministic to cover two names.
        /// Each is instead driven directly, against its own probe, in <c>ProbeEmissionTests</c>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <para>
        /// Starvation only happens when the thread pool fails to start a queued work item, and the
        /// test host runs on that same pool - saturating it hard enough to starve the probe starves
        /// the runner with it, which is a hang rather than a test.
        /// </para>
        /// <para>
        /// Whether a generation 2 collection runs concurrently is the runtime's decision, taken from
        /// the process's GC configuration. <c>GC.Collect</c> can request a non-blocking collection
        /// but cannot guarantee one is what happens, so expecting this counter would fail on a
        /// machine configured for non-concurrent GC and pass everywhere else.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyCollection<string> OutOfReach = new[]
        {
            CounterNames.Runtime.ThreadPool.StarvationSamples,
#if NET6_0_OR_GREATER
            CounterNames.Runtime.GarbageCollection.Gen2BackgroundPauseMs,
#endif
        };

        /// <summary>
        /// Exercises every probe, reporting through the supplied tracker.
        /// </summary>
        /// <param name="tracker">Sink the probes are constructed with.</param>
        public static void DriveEverything(IMetricTracker tracker)
        {
            DriveThreadPoolProbe(tracker);
            DriveCacheLockProbe(tracker);
#if NET6_0_OR_GREATER
            DriveGcPauseProbe(tracker);
            DriveContentionProbe(tracker);
#endif
        }

        private static void DriveThreadPoolProbe(IMetricTracker tracker)
        {
            // A timeout short enough that the starvation path is reachable, and a threshold of zero
            // so the slow-sample log runs too.
            using var probe = new ThreadPoolQueueDelayProbe(
                tracker,
                new ThreadPoolProbeOptions { SampleTimeoutSeconds = 1, SlowSampleThresholdMilliseconds = 0 },
                NullLogger<ThreadPoolQueueDelayProbe>.Instance);

            ProbeDriver.Begin(probe);
            ProbeDriver.SampleOnce(probe);
        }

        private static void DriveCacheLockProbe(IMetricTracker tracker)
        {
            using var probe = new CacheLockProbe(
                tracker,
                new CacheLockProbeOptions { QueueDepthThreshold = 0 },
                NullLogger<CacheLockProbe>.Instance);

            if (ProbeDriver.Begin(probe))
            {
                ProbeDriver.SampleOnce(probe);
            }
        }

#if NET6_0_OR_GREATER
        private static void DriveGcPauseProbe(IMetricTracker tracker)
        {
            using var probe = new GcPauseProbe(
                tracker,
                new GcPauseProbeOptions { SlowPauseThresholdMilliseconds = 0 },
                NullLogger<GcPauseProbe>.Instance);

            ProbeDriver.Begin(probe);

            // Twice through, because the probe reports the last collection rather than a specific
            // one: any collection the rest of the process happens to trigger between the forced one
            // and the sample takes that generation's slot. A second pass costs milliseconds and
            // makes losing the same slot twice the only way to miss a counter.
            for (var pass = 0; pass < 2; pass++)
            {
                // Forced rather than Default, so the runtime collects the generation asked for
                // instead of escalating to whichever one its tuning prefers.
                for (var generation = 0; generation <= 2; generation++)
                {
                    GC.Collect(generation, GCCollectionMode.Forced, blocking: true);
                    ProbeDriver.SampleOnce(probe);
                }
            }
        }

        private static void DriveContentionProbe(IMetricTracker tracker)
        {
            // Trigger of zero so the burst always opens, and the shortest window the options allow.
            using var probe = new ContentionProbe(
                tracker,
                new ContentionProbeOptions
                {
                    BurstTriggerContentionsPerSecond = 0,
                    BurstDurationSeconds = 1,
                    BurstCooldownSeconds = 0
                },
                NullLogger<ContentionProbe>.Instance);

            ProbeDriver.Begin(probe);

            using var contention = new ContentionGenerator();
            contention.Start();

            // The burst is opened from inside Sample and stays open for its window, so the
            // generator has to be running across this call rather than before it.
            ProbeDriver.SampleOnce(probe);
        }

        /// <summary>
        /// Produces genuine Monitor contention for as long as it is running.
        /// </summary>
        /// <remarks>
        /// Several threads competing for one lock, each holding it just long enough that the others
        /// have to wait rather than spin through. Nothing simulated: these are the same contention
        /// events the runtime raises on a site, which is the point - the probe's listener is being
        /// tested against the real event, not a stand-in for it.
        /// </remarks>
        private sealed class ContentionGenerator : IDisposable
        {
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly List<Thread> _threads = new List<Thread>();

            internal void Start()
            {
                for (var i = 0; i < Math.Max(4, Environment.ProcessorCount); i++)
                {
                    var thread = new Thread(Churn) { IsBackground = true, Name = "Contention Generator" };
                    _threads.Add(thread);
                    thread.Start();
                }
            }

            public void Dispose()
            {
                _stop.Cancel();

                foreach (var thread in _threads)
                {
                    thread.Join(TimeSpan.FromSeconds(5));
                }

                _stop.Dispose();
            }

            private void Churn()
            {
                while (!_stop.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        Thread.SpinWait(2000);
                    }
                }
            }
        }
#endif
    }
}
