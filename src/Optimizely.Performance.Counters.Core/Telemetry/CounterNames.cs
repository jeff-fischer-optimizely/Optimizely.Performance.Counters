namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// A duration counter and its companion rate counter. Every instrumented operation emits both,
    /// so they are defined and handed around as one thing rather than as two strings that could
    /// drift apart.
    /// </summary>
    public readonly struct CounterPair
    {
        internal CounterPair(string timeMs, string operations)
        {
            TimeMs = timeMs;
            Operations = operations;
        }

        /// <summary>How long the operation took, in milliseconds.</summary>
        public string TimeMs { get; }

        /// <summary>How many times the operation ran.</summary>
        public string Operations { get; }
    }

    /// <summary>
    /// The single source of truth for every counter this library publishes.
    /// <para>
    /// Nothing else may spell a counter name. The decorators emit through these members, and
    /// <see cref="EventCounterRegistry"/> enumerates the same members to tell Application Insights
    /// what to collect - so the set that is emitted and the set that is collected cannot disagree.
    /// They previously did: the tracker appended <c>[Operation=Get]</c> to the name while the
    /// registry advertised the bare name, and Application Insights matches exactly, so most
    /// counters were published to nobody.
    /// </para>
    /// </summary>
    public static class CounterNames
    {
        /// <summary>
        /// EventSource name for all Optimizely custom counters. Also the name baked into
        /// <see cref="OptimizelyPerformanceEventSource"/>'s attribute, which is why this is a
        /// <c>const</c> rather than a computed value.
        /// </summary>
        public const string EventSourceName = "Optimizely-Performance";

        /// <summary>Content read and write operations, from the CMS content decorators.</summary>
        public static class CmsContent
        {
            private const string Prefix = "Optimizely.CMS.Content.";

            /// <summary>Content loads, from <c>InstrumentedContentLoader</c>.</summary>
            public static readonly CounterPair Load = Pair("Load");

            /// <summary>Content saves that do not publish.</summary>
            public static readonly CounterPair Save = Pair("Save");

            /// <summary>Content saves that carry the Publish action, plus the V13 Publish members.</summary>
            public static readonly CounterPair Publish = Pair("Publish");

            /// <summary>Deletes, including moves to the wastebasket.</summary>
            public static readonly CounterPair Delete = Pair("Delete");

            /// <summary>Moves and copies.</summary>
            public static readonly CounterPair Move = Pair("Move");

            /// <summary>Items returned by a batch load, when the count is available for free.</summary>
            public const string ItemsLoaded = Prefix + "ItemsLoaded";

            private static CounterPair Pair(string operation) =>
                new CounterPair(Prefix + operation + "TimeMs", Prefix + operation + "Operations");
        }

        /// <summary>Cache effectiveness, from the synchronized cache decorator.</summary>
        public static class CmsCache
        {
            private const string Prefix = "Optimizely.CMS.Cache.";

            /// <summary>Percentage of reads served from cache over the reporting window.</summary>
            public const string HitRate = Prefix + "HitRate";

            /// <summary>Percentage of reads that missed over the reporting window.</summary>
            public const string MissRate = Prefix + "MissRate";

            /// <summary>Invalidations per second over the reporting window.</summary>
            public const string InvalidationsPerSecond = Prefix + "InvalidationsPerSecond";

            /// <summary>Total reads over the reporting window.</summary>
            public const string Operations = Prefix + "Operations";
        }

        /// <summary>Event publishing, from the V13 event publisher decorator.</summary>
        public static class CmsEvents
        {
            private const string Prefix = "Optimizely.CMS.Events.";

            /// <summary>All events published per second.</summary>
            public const string EventsPerSecond = Prefix + "EventsPerSecond";

            /// <summary>Broadcast events published per second.</summary>
            public const string RemoteEventsPerSecond = Prefix + "RemoteEventsPerSecond";

            /// <summary>Broadcast events that threw, per second.</summary>
            public const string RemoteEventFailuresPerSecond = Prefix + "RemoteEventFailuresPerSecond";

            /// <summary>Mean time to publish a broadcast event that succeeded.</summary>
            public const string AverageRemoteEventDeliveryTimeMs = Prefix + "AverageRemoteEventDeliveryTimeMs";
        }

        /// <summary>Cart and order operations, from the Commerce order decorator.</summary>
        public static class CommerceOrders
        {
            private const string Prefix = "Optimizely.Commerce.Orders.";

            /// <summary>Saves, covering Save, SaveAsPaymentPlan and SaveAsPurchaseOrder.</summary>
            public static readonly CounterPair Save = Pair("Save");

            /// <summary>Loads, covering every load overload.</summary>
            public static readonly CounterPair Load = Pair("Load");

            /// <summary>Order group creation.</summary>
            public static readonly CounterPair Create = Pair("Create");

            /// <summary>Order group deletion.</summary>
            public static readonly CounterPair Delete = Pair("Delete");

            /// <summary>Line items on a cart that was saved or loaded.</summary>
            public const string CartLineItemCount = Prefix + "CartLineItemCount";

            /// <summary>Value of a cart that was saved or loaded.</summary>
            public const string CartTotal = Prefix + "CartTotal";

            /// <summary>Carts returned by a batch load.</summary>
            public const string CartsLoaded = Prefix + "CartsLoaded";

            private static CounterPair Pair(string operation) =>
                new CounterPair(Prefix + operation + "TimeMs", Prefix + operation + "Operations");
        }
    }
}
