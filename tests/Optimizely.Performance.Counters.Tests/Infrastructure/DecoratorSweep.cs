using System.Collections.Generic;
using EPiServer;
using EPiServer.Commerce.Order;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Commerce.Decorators;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Drives every decorator available on this target framework through enough of its surface to
    /// emit each counter it owns.
    /// <para>
    /// Shared by the tests that check the registry against the decorators and the tests that check
    /// what actually reaches the EventSource. Both need the same exhaustive drive, and a drive that
    /// missed a decorator would make either of them pass for the wrong reason.
    /// </para>
    /// </summary>
    public static class DecoratorSweep
    {
        /// <summary>
        /// Registered counters <see cref="DriveEverything"/> cannot produce, and why.
        /// <para>
        /// Cart size and value need a populated <c>ICart</c>, which needs a Commerce database.
        /// They are covered by <c>InstrumentedOrderRepository.TrackCartMetrics</c> instead.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyCollection<string> OutOfReach = new[]
        {
            CounterNames.CommerceOrders.CartLineItemCount,
            CounterNames.CommerceOrders.CartTotal,
        };

        /// <summary>
        /// Exercises every decorator, reporting through the supplied tracker.
        /// </summary>
        /// <param name="tracker">Sink the decorators are constructed with.</param>
        public static void DriveEverything(IMetricTracker tracker)
        {
            DriveContentRepository(tracker);
            DriveCache(tracker);
            DriveOrderRepository(tracker);
#if CMS13
            DriveEventPublisher(tracker);
#endif
        }

        private static void DriveContentRepository(IMetricTracker tracker)
        {
            var (inner, recorder) = RecordingProxy.Create<IContentRepository>();
            var decorator = new InstrumentedContentRepository(
                inner, tracker, NullLogger<InstrumentedContentRepository>.Instance);

            ForwardingSweep.Run(decorator, typeof(IContentRepository), recorder);
        }

        private static void DriveCache(IMetricTracker tracker)
        {
            var inner = new StubCache();
            using var decorator = new InstrumentedSynchronizedObjectInstanceCache(
                inner, tracker, NullLogger<InstrumentedSynchronizedObjectInstanceCache>.Instance);

            inner.Stored["hit"] = new object();
            decorator.Get("hit");
            decorator.Get("miss");
            decorator.Remove("hit");

            // Hit and miss rates are only emitted when the window saw traffic, hence the reads.
            MetricFlush.Run(decorator);
        }

        private static void DriveOrderRepository(IMetricTracker tracker)
        {
            var (inner, recorder) = RecordingProxy.Create<IOrderRepository>();
            var decorator = new InstrumentedOrderRepository(
                inner, tracker, NullLogger<InstrumentedOrderRepository>.Instance);

            ForwardingSweep.Run(decorator, typeof(IOrderRepository), recorder);
        }

#if CMS13
        private static void DriveEventPublisher(IMetricTracker tracker)
        {
            var (inner, _) = RecordingProxy.Create<EPiServer.Events.IEventPublisher>();
            using var decorator = new InstrumentedEventPublisher(
                inner, tracker, NullLogger<InstrumentedEventPublisher>.Instance);

            // A broadcast, so the delivery-time average has something to average.
            decorator.PublishAsync(
                    typeof(string),
                    "payload",
                    new EPiServer.Events.EventPublishingOptions { Broadcast = true },
                    System.Threading.CancellationToken.None)
                .GetAwaiter().GetResult();

            MetricFlush.Run(decorator);
        }
#endif
    }
}
