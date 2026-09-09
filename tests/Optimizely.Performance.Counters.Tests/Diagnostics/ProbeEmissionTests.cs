using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// Covers the two counters <see cref="ProbeSweep"/> deliberately leaves alone, because reaching
    /// them through the sweep would mean degrading the process the whole test run shares.
    /// <para>
    /// Both are driven against their own probe through the branch that owns them, so what is proved
    /// is the probe's own choice of counter name rather than a name repeated in a test.
    /// </para>
    /// </summary>
    public class ProbeEmissionTests
    {
        [Fact]
        public void A_sample_the_pool_never_started_is_reported_as_starvation()
        {
            var tracker = new RecordingMetricTracker();

            var probe = new ThreadPoolQueueDelayProbe(
                tracker, new ThreadPoolProbeOptions(), NullLogger<ThreadPoolQueueDelayProbe>.Instance);

            using (probe)
            {
                // The recording branch, driven with the outcome a starved pool produces. Forcing a
                // real starvation would need every pool thread blocked, and the test runner is on
                // that pool - see ProbeSweep.OutOfReach.
                Record(probe, delayMilliseconds: 10_000, completed: false);
            }

            Assert.Contains(CounterNames.Runtime.ThreadPool.StarvationSamples, tracker.Names);
        }

        [Fact]
        public void A_completed_sample_is_not_reported_as_starvation()
        {
            var tracker = new RecordingMetricTracker();

            var probe = new ThreadPoolQueueDelayProbe(
                tracker, new ThreadPoolProbeOptions(), NullLogger<ThreadPoolQueueDelayProbe>.Instance);

            using (probe)
            {
                Record(probe, delayMilliseconds: 1, completed: true);
            }

            // The delay itself is always reported; only the starvation counter is conditional.
            Assert.Contains(CounterNames.Runtime.ThreadPool.QueueDelayMs, tracker.Names);
            Assert.DoesNotContain(CounterNames.Runtime.ThreadPool.StarvationSamples, tracker.Names);
        }

        private static void Record(ThreadPoolQueueDelayProbe probe, double delayMilliseconds, bool completed)
        {
            var method = typeof(ThreadPoolQueueDelayProbe).GetMethod(
                "Record", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.True(method != null, "ThreadPoolQueueDelayProbe has no private Record method.");

            method!.Invoke(probe, new object[] { delayMilliseconds, completed });
        }

#if NET6_0_OR_GREATER
        [Theory]
        [InlineData(0, false, "Optimizely.Runtime.GC.Gen0PauseMs")]
        [InlineData(1, false, "Optimizely.Runtime.GC.Gen1PauseMs")]
        [InlineData(2, false, "Optimizely.Runtime.GC.Gen2PauseMs")]
        [InlineData(2, true, "Optimizely.Runtime.GC.Gen2BackgroundPauseMs")]
        public void Each_generation_reports_to_its_own_counter(int generation, bool concurrent, string expected)
        {
            // Background gen 2 is here rather than in the sweep because whether a collection runs
            // concurrently is the runtime's decision and cannot be forced. The mapping is the part
            // that can go wrong, and it is the part being checked.
            var method = typeof(GcPauseProbe).GetMethod(
                "CounterForGeneration", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.True(method != null, "GcPauseProbe has no private CounterForGeneration method.");

            var actual = method!.Invoke(null, new object[] { generation, concurrent });

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Every_generation_counter_is_one_the_probe_can_choose()
        {
            // The other direction: a counter added to the GarbageCollection group that no generation
            // maps to would chart empty, and the registry cannot tell the difference.
            var method = typeof(GcPauseProbe).GetMethod(
                "CounterForGeneration", BindingFlags.Static | BindingFlags.NonPublic);

            var reachable = new[] { (0, false), (1, false), (2, false), (2, true) }
                .Select(c => (string)method!.Invoke(null, new object[] { c.Item1, c.Item2 })!)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                new[]
                {
                    CounterNames.Runtime.GarbageCollection.Gen0PauseMs,
                    CounterNames.Runtime.GarbageCollection.Gen1PauseMs,
                    CounterNames.Runtime.GarbageCollection.Gen2PauseMs,
                    CounterNames.Runtime.GarbageCollection.Gen2BackgroundPauseMs,
                }.ToHashSet(StringComparer.Ordinal),
                reachable);
        }
#endif
    }
}
