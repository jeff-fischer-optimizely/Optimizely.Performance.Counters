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
