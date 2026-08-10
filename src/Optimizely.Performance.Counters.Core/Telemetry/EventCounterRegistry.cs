using System.Collections.Generic;
using static Optimizely.Performance.Counters.Core.Telemetry.CounterNames;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Registry of all EventCounters published by Optimizely performance counters.
    /// These should be registered with Application Insights EventCounterCollectionModule.
    /// <para>
    /// Every name here is read from <see cref="CounterNames"/>, the same members the decorators
    /// emit through. Application Insights matches counter names exactly, so a registry that spelled
    /// its own names would silently collect nothing for any name that drifted.
    /// </para>
    /// </summary>
    public static class EventCounterRegistry
    {
        /// <summary>
        /// EventSource name for all Optimizely custom counters.
        /// </summary>
        public const string EventSourceName = CounterNames.EventSourceName;

        private static readonly IReadOnlyList<string> AllCounterNames = BuildCounterNames();

        /// <summary>
        /// Gets all counter names that are published by Optimizely-Performance EventSource.
        /// Application Insights should be configured to collect these.
        /// </summary>
        public static IEnumerable<string> GetAllCounterNames() => AllCounterNames;

        private static IReadOnlyList<string> BuildCounterNames()
        {
            var names = new List<string>();

            void AddPair(CounterPair pair)
            {
                names.Add(pair.TimeMs);
                names.Add(pair.Operations);
            }

            // CMS - Content operations, from InstrumentedContentLoader and
            // InstrumentedContentRepository.
            AddPair(CmsContent.Load);
            AddPair(CmsContent.Save);
            AddPair(CmsContent.Publish);
            AddPair(CmsContent.Delete);
            AddPair(CmsContent.Move);
            names.Add(CmsContent.ItemsLoaded);

            // CMS - Cache, from InstrumentedSynchronizedObjectInstanceCache.
            names.Add(CmsCache.HitRate);
            names.Add(CmsCache.MissRate);
            names.Add(CmsCache.InvalidationsPerSecond);
            names.Add(CmsCache.Operations);

            // CMS - Events, from InstrumentedEventPublisher. CMS 13 only; versions 11 and 12 raise
            // events through a static class with no seam to decorate. Registering them everywhere
            // is harmless - Application Insights simply never sees a value on the older versions.
            names.Add(CmsEvents.EventsPerSecond);
            names.Add(CmsEvents.RemoteEventsPerSecond);
            names.Add(CmsEvents.RemoteEventFailuresPerSecond);
            names.Add(CmsEvents.AverageRemoteEventDeliveryTimeMs);

            // Commerce - Orders, from InstrumentedOrderRepository. Save covers Save,
            // SaveAsPaymentPlan and SaveAsPurchaseOrder; Load covers all four load overloads.
            AddPair(CommerceOrders.Save);
            AddPair(CommerceOrders.Load);
            AddPair(CommerceOrders.Create);
            AddPair(CommerceOrders.Delete);
            names.Add(CommerceOrders.CartLineItemCount);
            names.Add(CommerceOrders.CartTotal);
            names.Add(CommerceOrders.CartsLoaded);

            // TODO: Commerce - Pricing, Inventory, Promotions

            return names;
        }
    }
}
