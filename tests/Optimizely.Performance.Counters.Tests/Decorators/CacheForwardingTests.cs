using System.Linq;
using EPiServer.Framework.Cache;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    public class CacheForwardingTests
    {
        private static InstrumentedSynchronizedObjectInstanceCache Wrap(
            ISynchronizedObjectInstanceCache inner, RecordingMetricTracker tracker) =>
            new InstrumentedSynchronizedObjectInstanceCache(
                inner, tracker, NullLogger<InstrumentedSynchronizedObjectInstanceCache>.Instance);

        [Fact]
        public void Every_ISynchronizedObjectInstanceCache_member_reaches_the_wrapped_cache()
        {
            var (inner, recorder) = RecordingProxy.Create<ISynchronizedObjectInstanceCache>();
            using var decorator = Wrap(inner, new RecordingMetricTracker());

            ForwardingAssert.AllForwarded(
                ForwardingSweep.Run(decorator, typeof(ISynchronizedObjectInstanceCache), recorder),
                atLeast: 9);
        }

        [Fact]
        public void Cache_reads_are_not_reported_per_call()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            for (var i = 0; i < 100; i++)
            {
                decorator.Get($"key{i}");
            }

            // Reads accumulate into interlocked counters and are flushed on a timer. Emitting per
            // call would put an EventSource write on the hottest path in the CMS.
            Assert.Empty(tracker.Metrics);
        }

        [Fact]
        public void The_flush_reports_the_hit_rate_the_reads_produced()
        {
            var inner = new StubCache();
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(inner, tracker);

            inner.Stored["hit"] = new object();

            decorator.Get("hit");
            decorator.Get("hit");
            decorator.Get("hit");
            decorator.Get("miss");

            MetricFlush.Run(decorator);

            Assert.Equal(75.0, ValueOf(tracker, "Optimizely.CMS.Cache.HitRate"), precision: 6);
            Assert.Equal(25.0, ValueOf(tracker, "Optimizely.CMS.Cache.MissRate"), precision: 6);
            Assert.Equal(4.0, ValueOf(tracker, "Optimizely.CMS.Cache.Operations"), precision: 6);
        }

        [Fact]
        public void Removals_are_counted_as_invalidations()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.Remove("a");
            decorator.RemoveLocal("b");
            decorator.RemoveRemote("c");

            MetricFlush.Run(decorator);

            // Three invalidations over the 60 second reporting window.
            Assert.Equal(3.0 / 60.0, ValueOf(tracker, "Optimizely.CMS.Cache.InvalidationsPerSecond"), precision: 6);
        }

        [Fact]
        public void Each_removal_route_is_counted_under_its_own_name()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.Remove("a");
            decorator.Remove("b");
            decorator.Remove("c");
            decorator.RemoveLocal("d");
            decorator.RemoveLocal("e");
            decorator.RemoveRemote("f");

            MetricFlush.Run(decorator);

            // The whole point of the split: the total says six invalidations a minute and gives no
            // hint that two of them left five other nodes holding stale entries.
            Assert.Equal(
                3.0 / 60.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.SynchronizedInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                2.0 / 60.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                1.0 / 60.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.RemoteInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                6.0 / 60.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.InvalidationsPerSecond"),
                precision: 6);
        }

        [Fact]
        public void A_route_that_saw_nothing_still_reports_zero()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.Remove("a");

            MetricFlush.Run(decorator);

            // A site with no RemoveLocal calls is the good case, and the counter has to be able to
            // say so. Skipping the emission would make "no problem here" indistinguishable from
            // "the decorator is not installed", which is the one confusion this counter cannot
            // afford - it is read to rule a fault out.
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.RemoteInvalidationsPerSecond"),
                precision: 6);
        }

        [Fact]
        public void The_routes_reset_after_a_flush()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.RemoveLocal("a");
            MetricFlush.Run(decorator);
            MetricFlush.Run(decorator);

            // ValueOf takes the last emission, so a rate that carried over would still read as one
            // per minute here rather than dropping back to nothing.
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond"),
                precision: 6);
        }

#if !CMS13
        [Fact]
        [System.Obsolete("Exercises the obsolete Clear member on purpose.")]
        public void A_clear_is_counted_in_the_total_and_under_no_route()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.Clear();

            MetricFlush.Run(decorator);

            // A bulk invalidation with no route of its own, which is why the three routes are
            // documented as not summing to the total rather than asserted to.
            Assert.Equal(
                1.0 / 60.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.InvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.SynchronizedInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond"),
                precision: 6);
            Assert.Equal(
                0.0,
                ValueOf(tracker, "Optimizely.CMS.Cache.RemoteInvalidationsPerSecond"),
                precision: 6);
        }
#endif

        [Fact]
        public void The_counters_reset_after_a_flush()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(new StubCache(), tracker);

            decorator.Get("miss");
            MetricFlush.Run(decorator);

            var afterFirst = tracker.Metrics.Count;
            MetricFlush.Run(decorator);

            // The second flush saw no traffic, so it reports operations but not a hit rate.
            // A carried-over hit rate would read as real traffic in a dashboard.
            var second = tracker.Metrics.Skip(afterFirst).Select(m => m.Name).ToList();
            Assert.DoesNotContain("Optimizely.CMS.Cache.HitRate", second);
            Assert.Contains("Optimizely.CMS.Cache.Operations", second);
        }

        private static double ValueOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.Last(m => m.Name == name).Value;
    }
}
