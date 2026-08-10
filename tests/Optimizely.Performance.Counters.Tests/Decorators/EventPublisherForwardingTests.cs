#if CMS13
using System.Linq;
using System.Threading;
using EPiServer.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    /// <summary>
    /// CMS 13 only. Versions 11 and 12 raise events through the static <c>Event</c> class, which
    /// has no seam to decorate, so there is nothing to test there.
    /// </summary>
    public class EventPublisherForwardingTests
    {
        private static InstrumentedEventPublisher Wrap(IEventPublisher inner, RecordingMetricTracker tracker) =>
            new InstrumentedEventPublisher(inner, tracker, NullLogger<InstrumentedEventPublisher>.Instance);

        [Fact]
        public void Every_IEventPublisher_member_reaches_the_wrapped_publisher()
        {
            var (inner, recorder) = RecordingProxy.Create<IEventPublisher>();
            using var decorator = Wrap(inner, new RecordingMetricTracker());

            ForwardingAssert.AllForwarded(ForwardingSweep.Run(decorator, typeof(IEventPublisher), recorder), atLeast: 4);
        }

        [Fact]
        public async System.Threading.Tasks.Task Publishes_are_not_reported_per_call()
        {
            var (inner, _) = RecordingProxy.Create<IEventPublisher>();
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(inner, tracker);

            for (var i = 0; i < 50; i++)
            {
                await decorator.PublishAsync(typeof(string), "payload", CancellationToken.None);
            }

            // Publishing is a hot path during content operations, so rates accumulate and flush
            // on a timer rather than writing to the EventSource per event.
            Assert.Empty(tracker.Metrics);
        }

        [Fact]
        public async System.Threading.Tasks.Task The_flush_reports_the_rates_the_publishes_produced()
        {
            var (inner, _) = RecordingProxy.Create<IEventPublisher>();
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(inner, tracker);

            await decorator.PublishAsync(typeof(string), "local", CancellationToken.None);
            await decorator.PublishAsync(typeof(string), "local", CancellationToken.None);
            await decorator.PublishAsync(typeof(string), "remote", new EventPublishingOptions { Broadcast = true },
                CancellationToken.None);

            MetricFlush.Run(decorator);

            // Three events over the 60 second reporting window, one of them a broadcast.
            Assert.Equal(3.0 / 60.0, ValueOf(tracker, "Optimizely.CMS.Events.EventsPerSecond"), precision: 6);
            Assert.Equal(1.0 / 60.0, ValueOf(tracker, "Optimizely.CMS.Events.RemoteEventsPerSecond"), precision: 6);
            Assert.Equal(0.0, ValueOf(tracker, "Optimizely.CMS.Events.RemoteEventFailuresPerSecond"), precision: 6);
            Assert.Contains("Optimizely.CMS.Events.AverageRemoteEventDeliveryTimeMs", tracker.Names);
        }

        [Fact]
        public async System.Threading.Tasks.Task A_failed_broadcast_is_counted_as_a_remote_failure_and_still_throws()
        {
            var tracker = new RecordingMetricTracker();
            using var decorator = Wrap(ThrowingProxy.Create<IEventPublisher>(), tracker);

            await Assert.ThrowsAsync<InnerFailureException>(() =>
                decorator.PublishAsync(typeof(string), "remote", new EventPublishingOptions { Broadcast = true },
                    CancellationToken.None));

            MetricFlush.Run(decorator);

            Assert.Equal(1.0 / 60.0, ValueOf(tracker, "Optimizely.CMS.Events.RemoteEventFailuresPerSecond"), precision: 6);

            // A failed broadcast never delivered, so it must not be folded into the delivery-time
            // average - that average is read as "how slow is the message bus", not "did it work".
            Assert.DoesNotContain("Optimizely.CMS.Events.AverageRemoteEventDeliveryTimeMs", tracker.Names);
        }

        [Fact]
        public void The_publisher_does_not_dispose_the_publisher_it_wraps()
        {
            var (inner, recorder) = RecordingProxy.Create<IEventPublisher>();
            var decorator = Wrap(inner, new RecordingMetricTracker());

            decorator.Dispose();

            // The container owns the wrapped instance. Disposing it here would tear down event
            // publishing for the whole site the first time a decorator went out of scope.
            Assert.Empty(recorder.Invocations);
        }

        private static double ValueOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.Last(m => m.Name == name).Value;
    }
}
#endif
