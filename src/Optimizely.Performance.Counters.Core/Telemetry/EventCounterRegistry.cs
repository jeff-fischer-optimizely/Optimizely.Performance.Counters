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

            // The same invalidations again, split by the call that asked for them. Registered on
            // every version: all three routes exist on ISynchronizedObjectInstanceCache as far back
            // as CMS 11, so unlike the cascade counters below these are populated everywhere.
            names.Add(CmsCache.SynchronizedInvalidationsPerSecond);
            names.Add(CmsCache.LocalOnlyInvalidationsPerSecond);
            names.Add(CmsCache.RemoteInvalidationsPerSecond);

            // CMS - Cache dependency cascade, from InstrumentedSynchronizedObjectInstanceCache and
            // InstrumentedMemoryCache. V12 and V13 only: the measurement counts entries as they
            // reach IMemoryCache.Remove, and CMS 11 caches through System.Web instead, so there is
            // no IMemoryCache underneath it to count at. Registered on every version anyway, on the
            // same reasoning as the V13-only event counters below.
            names.Add(CmsCache.RemovalFanOut);
            names.Add(CmsCache.RemoteRemovalFanOut);
            names.Add(CmsCache.InsertFanOut);
            names.Add(CmsCache.RemovalDurationMs);
            names.Add(CmsCache.InsertTtlSeconds);

            names.Add(CmsCache.EvictionsExpired);
            names.Add(CmsCache.EvictionsCapacity);
            names.Add(CmsCache.EvictionsReplaced);
            names.Add(CmsCache.EvictionsTokenExpired);

            // CMS - Cache lock, from CacheLockProbe. Absent rather than zero when the probe cannot
            // find the lock, which is a thing that can happen across an Optimizely upgrade; the
            // names are registered regardless, on the same reasoning as the V13-only event
            // counters above.
            //
            // Never populated on V11 in particular: CMS 11 has no MemoryObjectInstanceCache and
            // caches through System.Web instead, so there is no process-wide reader/writer lock for
            // anything to queue on. That is a real difference in how the two versions cache rather
            // than a missing feature, and it is asserted in CacheLockLocatorTests.
            names.Add(CmsCache.LockWaitingWriters);
            names.Add(CmsCache.LockWaitingReaders);
            names.Add(CmsCache.LockCurrentReaders);
            names.Add(CmsCache.LockWriteHeldPercent);

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

            // Runtime - measured by the Core probes rather than read from a counter source. These
            // describe the process, not Optimizely, so they are published on a CMS site and a
            // Commerce site alike.
            names.Add(Runtime.ThreadPool.QueueDelayMs);
            names.Add(Runtime.ThreadPool.BusyWorkerThreads);
            names.Add(Runtime.ThreadPool.BusyIoThreads);
            names.Add(Runtime.ThreadPool.StarvationSamples);

            names.Add(Runtime.GarbageCollection.Gen0PauseMs);
            names.Add(Runtime.GarbageCollection.Gen1PauseMs);
            names.Add(Runtime.GarbageCollection.Gen2PauseMs);
            names.Add(Runtime.GarbageCollection.Gen2BackgroundPauseMs);
            names.Add(Runtime.GarbageCollection.PauseTimePercent);

            // Registered on every target framework though only .NET 8 and later can produce them;
            // an unfilled counter costs a name in the registry and nothing else.
            names.Add(Runtime.GarbageCollection.IntervalPauseMs);
            names.Add(Runtime.GarbageCollection.PauseDutyCyclePercent);

            names.Add(Runtime.Contention.ContentionsPerSecond);
            names.Add(Runtime.Contention.BurstContentions);
            names.Add(Runtime.Contention.BurstWaitP50Ms);
            names.Add(Runtime.Contention.BurstWaitP95Ms);
            names.Add(Runtime.Contention.BurstWaitMaxMs);

            // Runtime - logging, from LogWriteRateRecorder. Fed by an ILoggerProvider on V12 and
            // V13 and by a log4net appender on V11, so unlike the cache and event counters above
            // these are populated on every version.
            names.Add(Runtime.Logging.WritesPerSecond);
            names.Add(Runtime.Logging.WarningsPerSecond);
            names.Add(Runtime.Logging.ErrorsPerSecond);

            // Runtime - outbound response cacheability, from HttpCacheabilityRecorder. Fed by
            // middleware on V12 and V13 and by an HTTP module on V11, so like the logging counters
            // above these are populated on every version.
            names.Add(Runtime.Http.ResponsesPerSecond);
            names.Add(Runtime.Http.PublicPercent);
            names.Add(Runtime.Http.PrivatePercent);
            names.Add(Runtime.Http.RevalidatePercent);
            names.Add(Runtime.Http.NoStorePercent);
            names.Add(Runtime.Http.NoDirectivePercent);
            names.Add(Runtime.Http.FreshnessSeconds);
            names.Add(Runtime.Http.ValidatorPercent);
            names.Add(Runtime.Http.SharedCacheConflictPercent);

            // Runtime - the process, from ProcessUptimeReporter. Started alongside the probes on
            // every version, and the one counter here that needs nothing from the host to work.
            names.Add(Runtime.Process.UptimeSeconds);

            return names;
        }
    }
}
