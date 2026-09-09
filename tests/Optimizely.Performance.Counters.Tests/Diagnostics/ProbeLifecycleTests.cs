using System;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Diagnostics;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// Lifecycle tests, run against every probe on this target framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guarantees under test belong to <see cref="SamplingProbe"/> rather than to any one probe,
    /// so they are asserted once over all of them: a rule the base keeps for three probes and drops
    /// for the fourth is not a rule. Each probe is named rather than passed as a factory so a
    /// failure says which one broke.
    /// </para>
    /// <para>
    /// Why these sequences and not others: a probe holds a thread for the life of the process and is
    /// started from an Optimizely initialization module, which runs again across an app domain
    /// recycle. So the interesting cases are the ones that module can produce - double start, start
    /// after disposal, disposal without a start - and in that setting a thrown exception is not a
    /// missing metric but a site that will not start. Worse, an exception escaping a sampling thread
    /// is unhandled on a thread created by hand, which ends the process on .NET Core; several tests
    /// below therefore wait after the act, because the failure they are guarding against arrives on
    /// another thread and would otherwise be scored as a pass.
    /// </para>
    /// </remarks>
    public class ProbeLifecycleTests
    {
        private const string ThreadPool = "ThreadPool";
        private const string CacheLock = "CacheLock";
        private const string GarbageCollection = "GC";
        private const string Contention = "Contention";

        /// <summary>
        /// Every probe that exists on this target framework.
        /// </summary>
        public static TheoryData<string> AllProbes
        {
            get
            {
                var probes = new TheoryData<string> { ThreadPool, CacheLock };
#if NET6_0_OR_GREATER
                probes.Add(GarbageCollection);
                probes.Add(Contention);
#endif
                return probes;
            }
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Starting_twice_is_harmless(string probeName)
        {
            using var probe = CreateProbe(probeName);

            probe.Start();
            var exception = Record.Exception(() => probe.Start());

            Assert.Null(exception);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Disposing_without_starting_is_harmless(string probeName)
        {
            // Uninitialize runs even when Initialize failed part way through.
            var probe = CreateProbe(probeName);

            var exception = Record.Exception(() => probe.Dispose());

            Assert.Null(exception);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Disposing_twice_is_harmless(string probeName)
        {
            var probe = CreateProbe(probeName);
            probe.Start();
            probe.Dispose();

            var exception = Record.Exception(() => probe.Dispose());

            Assert.Null(exception);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Starting_after_disposal_is_harmless(string probeName)
        {
            var probe = CreateProbe(probeName);
            probe.Dispose();

            var exception = Record.Exception(() => probe.Start());

            Assert.Null(exception);
            Assert.False(probe.IsRunning);

            // Start returning cleanly is not the whole claim. If it did raise a sampling thread,
            // that thread would be running against state already torn down, and what it threw would
            // end the process rather than fail this assertion - hence the wait. An earlier revision
            // of this code, with a disposed CancellationTokenSource, failed in exactly that way.
            Thread.Sleep(250);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Disposing_while_running_stops_cleanly(string probeName)
        {
            var probe = CreateProbe(probeName);
            probe.Start();

            // Long enough for the sampler to have taken a sample and be waiting on the stop signal,
            // so disposal races the wait rather than arriving before it begins.
            Thread.Sleep(250);

            var exception = Record.Exception(() => probe.Dispose());

            Assert.Null(exception);
            Assert.False(probe.IsRunning);

            Thread.Sleep(250);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void A_disabled_probe_never_runs(string probeName)
        {
            using var probe = CreateProbe(probeName, enabled: false);

            probe.Start();

            // Disabled has to prevent the work, not merely the reporting. For the cache lock probe
            // in particular this is the switch that turns off the package's only reflection over
            // Optimizely internals, which is worth nothing if the reflection still runs.
            Assert.False(probe.IsRunning);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void Default_options_are_taken_when_none_are_supplied(string probeName)
        {
            using var probe = CreateProbe(probeName, explicitOptions: false);

            var exception = Record.Exception(() => probe.Start());

            Assert.Null(exception);
        }

        [Theory]
        [MemberData(nameof(AllProbes))]
        public void A_probe_will_not_take_a_null_tracker(string probeName)
        {
            // There is no useful degraded mode here. A probe with nowhere to publish burns a thread
            // sampling into nothing, so this fails at construction, in the initialization module,
            // where the stack still says which probe was misconfigured.
            Assert.Throws<ArgumentNullException>(() => CreateProbe(probeName, nullTracker: true));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void A_nonsensical_sample_interval_is_clamped(int seconds)
        {
            // Unclamped, this spins the sampling thread at full speed against whatever it measures -
            // for the thread pool probe, queueing work items in a tight loop to the pool whose
            // saturation it is reporting on. The probe becomes the load.
            Assert.True(
                EffectiveInterval(
                    new ThreadPoolProbeOptions { SampleIntervalSeconds = seconds }, "SampleInterval")
                        >= TimeSpan.FromSeconds(1));

            Assert.True(
                EffectiveInterval(
                    new CacheLockProbeOptions { SampleIntervalSeconds = seconds }, "SampleInterval")
                        >= TimeSpan.FromSeconds(1));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-30)]
        public void A_nonsensical_sample_timeout_is_clamped(int seconds)
        {
            // A zero timeout would report every sample as starvation, which is worse than reporting
            // none: the counter that means "the site is failing" would be permanently on.
            Assert.True(
                EffectiveInterval(
                    new ThreadPoolProbeOptions { SampleTimeoutSeconds = seconds }, "SampleTimeout")
                        >= TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Reads one of the clamped, derived intervals off an options object.
        /// </summary>
        /// <remarks>
        /// These are internal - they are the probe's view of its own options, not part of the
        /// package's API - and this assembly is not a friend of either library. Reached by reflection
        /// rather than by widening them, on the same reasoning as <c>MetricFlush</c>: a seam opened
        /// for a test is API the package then has to keep. Renaming one fails here loudly.
        /// <para>
        /// Public is searched as well as non-public because of the one exception:
        /// <c>CacheLockProbeOptions.SampleInterval</c> has to be public, since its probe lives in
        /// the CMS package rather than in Core. The visibility is not what this asserts on.
        /// </para>
        /// </remarks>
        private static TimeSpan EffectiveInterval(object options, string propertyName)
        {
            var property = options.GetType().GetProperty(
                propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.True(
                property != null,
                $"{options.GetType().Name} has no {propertyName} property.");

            return (TimeSpan)property!.GetValue(options)!;
        }

        private static SamplingProbe CreateProbe(
            string probeName,
            bool enabled = true,
            bool explicitOptions = true,
            bool nullTracker = false)
        {
            var metrics = nullTracker ? null! : (IMetricTracker)new RecordingMetricTracker();

            switch (probeName)
            {
                case ThreadPool:
                    return new ThreadPoolQueueDelayProbe(
                        metrics,
                        explicitOptions ? new ThreadPoolProbeOptions { Enabled = enabled } : null,
                        NullLogger<ThreadPoolQueueDelayProbe>.Instance);

                case CacheLock:
                    return new CacheLockProbe(
                        metrics,
                        explicitOptions ? new CacheLockProbeOptions { Enabled = enabled } : null,
                        NullLogger<CacheLockProbe>.Instance);

#if NET6_0_OR_GREATER
                case GarbageCollection:
                    return new GcPauseProbe(
                        metrics,
                        explicitOptions ? new GcPauseProbeOptions { Enabled = enabled } : null,
                        NullLogger<GcPauseProbe>.Instance);

                case Contention:
                    return new ContentionProbe(
                        metrics,
                        explicitOptions ? new ContentionProbeOptions { Enabled = enabled } : null,
                        NullLogger<ContentionProbe>.Instance);
#endif

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(probeName), probeName, "No probe goes by that name.");
            }
        }
    }
}
