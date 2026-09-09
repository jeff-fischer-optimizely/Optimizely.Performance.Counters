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

            /// <summary>Invalidations per second over the reporting window, by every route.</summary>
            /// <remarks>
            /// The total, kept as it always was so that existing charts do not quietly change
            /// meaning now that the routes below are published separately. It is not the sum of
            /// those three on V11 and V12: the obsolete <c>Clear()</c> is a bulk invalidation with
            /// no route of its own and is counted here only.
            /// </remarks>
            public const string InvalidationsPerSecond = Prefix + "InvalidationsPerSecond";

            /// <summary>
            /// Invalidations per second that were broadcast to the rest of the cluster.
            /// </summary>
            /// <remarks>
            /// <c>Remove</c>, the call almost all application code means to make. Interesting
            /// mainly as the denominator for <see cref="LocalOnlyInvalidationsPerSecond"/>: the
            /// two together say what share of this site's invalidations the other nodes never
            /// heard about.
            /// </remarks>
            public const string SynchronizedInvalidationsPerSecond =
                Prefix + "SynchronizedInvalidationsPerSecond";

            /// <summary>
            /// Invalidations per second that were deliberately not broadcast.
            /// </summary>
            /// <remarks>
            /// <c>RemoveLocal</c>, and a bug signal on any site running more than one instance.
            /// Every one of these drops an entry here and leaves the same entry stale on every
            /// other node, until something else happens to evict it. There is a legitimate use -
            /// discarding an entry the local node alone got wrong - and it is rare; the common
            /// case is application code that reached for the wrong overload, or a
            /// single-instance-era habit that survived the move to load balancing.
            /// <para>
            /// Nothing else in the platform reports this. It is invisible in development, where
            /// there is only one node for the entry to be stale on, it logs nothing, and it
            /// presents in production as content that is correct on one server and wrong on
            /// another - the class of report that gets closed as unreproducible.
            /// </para>
            /// </remarks>
            public const string LocalOnlyInvalidationsPerSecond =
                Prefix + "LocalOnlyInvalidationsPerSecond";

            /// <summary>
            /// Invalidations per second arriving from another node in the cluster.
            /// </summary>
            /// <remarks>
            /// <c>RemoveRemote</c>: work this instance was told to do rather than work it chose.
            /// The rate-side companion to <see cref="RemoteRemovalFanOut"/>, and unlike that
            /// counter it is available on V11 as well, where there is no memory cache underneath
            /// to count cascaded entries at.
            /// </remarks>
            public const string RemoteInvalidationsPerSecond =
                Prefix + "RemoteInvalidationsPerSecond";

            /// <summary>Total reads over the reporting window.</summary>
            public const string Operations = Prefix + "Operations";

            /// <summary>
            /// Threads waiting to take Optimizely's cache lock for writing.
            /// </summary>
            /// <remarks>
            /// The number that matters. Optimizely's memory cache serialises every write behind
            /// one process-wide reader/writer lock, and writers exclude readers - so a queue here
            /// is the whole site waiting on cache invalidation. Hit rate cannot show this: every
            /// one of those threads is about to record a hit, once it is let through.
            /// </remarks>
            public const string LockWaitingWriters = Prefix + "LockWaitingWriters";

            /// <summary>Threads stalled by an invalidation already in progress.</summary>
            public const string LockWaitingReaders = Prefix + "LockWaitingReaders";

            /// <summary>Threads currently reading under the cache lock.</summary>
            public const string LockCurrentReaders = Prefix + "LockCurrentReaders";

            /// <summary>
            /// Percentage of samples in which the cache was closed to readers.
            /// </summary>
            /// <remarks>
            /// A duty cycle rather than an event count, which is what makes it comparable across
            /// sites of different sizes.
            /// </remarks>
            public const string LockWriteHeldPercent = Prefix + "LockWriteHeldPercent";

            /// <summary>
            /// Entries actually discarded by one removal requested on this node.
            /// </summary>
            /// <remarks>
            /// The amplification factor, and the reason a cache can collapse from a single call.
            /// Optimizely hangs entries off master keys, so removing one walks the dependency graph
            /// and takes the whole subtree with it. The caller sees one <c>Remove</c>. Read the
            /// counter's own aggregates: count is removals asked for, mean is the amplification,
            /// max is the worst single cascade in the interval.
            /// </remarks>
            public const string RemovalFanOut = Prefix + "RemovalFanOut";

            /// <summary>
            /// Entries discarded by an invalidation broadcast from another node.
            /// </summary>
            /// <remarks>
            /// Split from <see cref="RemovalFanOut"/> because the two lead somewhere different: a
            /// cascade counted here was caused by a different server in the cluster, so nothing this
            /// instance did explains it and profiling this instance will not find it.
            /// </remarks>
            public const string RemoteRemovalFanOut = Prefix + "RemoteRemovalFanOut";

            /// <summary>
            /// Entries discarded by an insert that displaced something other than itself.
            /// </summary>
            /// <remarks>
            /// Inserting is not only a write. Optimizely removes the existing entry first, and that
            /// removal cascades - so writing a key that others depend on discards the subtree beneath
            /// it. Counted separately because it is the least obvious way a site evicts its own
            /// cache, and it does not look like an invalidation from anywhere else.
            /// </remarks>
            public const string InsertFanOut = Prefix + "InsertFanOut";

            /// <summary>
            /// Milliseconds a removal spent holding the cache's write lock.
            /// </summary>
            /// <remarks>
            /// Worth watching on its own: the lock is process-wide and writers exclude readers, so
            /// this is time during which no thread could read the cache at all.
            /// </remarks>
            public const string RemovalDurationMs = Prefix + "RemovalDurationMs";

            /// <summary>
            /// Lifetime, in seconds, requested for an entry as it was inserted.
            /// </summary>
            /// <remarks>
            /// Taken from what the caller asked for rather than inferred from evictions, so it
            /// describes intent. A low mean is how the implementations churning the cache faster
            /// than it can pay for itself are found.
            /// </remarks>
            public const string InsertTtlSeconds = Prefix + "InsertTtlSeconds";

            /// <summary>Entries evicted because their requested lifetime ran out.</summary>
            public const string EvictionsExpired = Prefix + "EvictionsExpired";

            /// <summary>
            /// Entries evicted to relieve memory pressure.
            /// </summary>
            /// <remarks>
            /// The one eviction reason that means the site is sized below its working set. Entries
            /// dropped this way take their dependents with them, so the miss storm that follows is
            /// larger than the number counted here.
            /// </remarks>
            public const string EvictionsCapacity = Prefix + "EvictionsCapacity";

            /// <summary>Entries evicted because the same key was written again.</summary>
            public const string EvictionsReplaced = Prefix + "EvictionsReplaced";

            /// <summary>Entries evicted because a change token they depended on fired.</summary>
            public const string EvictionsTokenExpired = Prefix + "EvictionsTokenExpired";
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

        /// <summary>
        /// Runtime conditions measured by the probes rather than read from a counter source.
        /// </summary>
        /// <remarks>
        /// These describe the process rather than Optimizely, so they are published by the Core
        /// package and appear on a CMS site and a Commerce site alike. They exist because the
        /// stock counters that cover the same ground report the wrong quantity: queue
        /// <em>length</em> rather than queue <em>delay</em>, a GC <em>percentage</em> rather than
        /// the pause durations behind it, and a contention <em>count</em> with no idea how long
        /// any wait lasted.
        /// </remarks>
        public static class Runtime
        {
            /// <summary>Thread pool scheduling delay, from the thread pool probe.</summary>
            public static class ThreadPool
            {
                private const string Prefix = "Optimizely.Runtime.ThreadPool.";

                /// <summary>
                /// Milliseconds between queueing a work item and the pool starting it.
                /// </summary>
                /// <remarks>
                /// Every asynchronous continuation in the site pays this, which is why thread pool
                /// starvation presents as uniform slowness across endpoints that have nothing else
                /// in common. <c>threadpool-queue-length</c> cannot substitute: a queue of ten is
                /// harmless if the pool drains it instantly and fatal if it is injecting one
                /// thread per second.
                /// </remarks>
                public const string QueueDelayMs = Prefix + "QueueDelayMs";

                /// <summary>Worker threads in use.</summary>
                public const string BusyWorkerThreads = Prefix + "BusyWorkerThreads";

                /// <summary>Completion port threads in use.</summary>
                public const string BusyIoThreads = Prefix + "BusyIoThreads";

                /// <summary>Samples that never started within the probe's timeout.</summary>
                public const string StarvationSamples = Prefix + "StarvationSamples";
            }

            /// <summary>Garbage collection pauses, from the GC pause probe.</summary>
            /// <remarks>
            /// Split by generation rather than dimensioned by it. EventCounters carry no
            /// dimensions, and gen 0 and gen 2 pauses differ by three orders of magnitude - one
            /// series holding both says nothing useful about either.
            /// </remarks>
            public static class GarbageCollection
            {
                private const string Prefix = "Optimizely.Runtime.GC.";

                /// <summary>Pause duration of a generation 0 collection.</summary>
                public const string Gen0PauseMs = Prefix + "Gen0PauseMs";

                /// <summary>Pause duration of a generation 1 collection.</summary>
                public const string Gen1PauseMs = Prefix + "Gen1PauseMs";

                /// <summary>
                /// Pause duration of a blocking generation 2 collection.
                /// </summary>
                /// <remarks>
                /// The one that shows up in request duration. Every thread in the process is
                /// stopped for this long, so requests in flight take at least this much longer for
                /// reasons that appear nowhere in their own telemetry.
                /// </remarks>
                public const string Gen2PauseMs = Prefix + "Gen2PauseMs";

                /// <summary>
                /// Pause duration at an edge of a background generation 2 collection.
                /// </summary>
                /// <remarks>
                /// Separate from the blocking case because it is the benign one: the process is
                /// only suspended at the start and end of the concurrent phase. Charted together
                /// with a blocking gen 2, an ordinary background collection looks like a stall.
                /// </remarks>
                public const string Gen2BackgroundPauseMs = Prefix + "Gen2BackgroundPauseMs";

                /// <summary>Share of time spent paused, as the runtime last computed it.</summary>
                public const string PauseTimePercent = Prefix + "PauseTimePercent";

                /// <summary>
                /// Total milliseconds paused during the sampling interval.
                /// </summary>
                /// <remarks>
                /// Exact rather than sampled, being a delta of a cumulative runtime total, but
                /// only available on .NET 8 and later. This is the number to alert on; the
                /// per-generation durations are for reading the shape.
                /// </remarks>
                public const string IntervalPauseMs = Prefix + "IntervalPauseMs";

                /// <summary>Percentage of wall clock time in the interval spent paused.</summary>
                public const string PauseDutyCyclePercent = Prefix + "PauseDutyCyclePercent";
            }

            /// <summary>Monitor lock contention, from the contention probe.</summary>
            public static class Contention
            {
                private const string Prefix = "Optimizely.Runtime.Contention.";

                /// <summary>
                /// Monitor contentions per second.
                /// </summary>
                /// <remarks>
                /// The always-on half of the probe, and free: a delta of a counter the runtime
                /// already maintains. It is also what triggers a capture burst.
                /// </remarks>
                public const string ContentionsPerSecond = Prefix + "ContentionsPerSecond";

                /// <summary>Contentions observed during a capture burst.</summary>
                public const string BurstContentions = Prefix + "BurstContentions";

                /// <summary>Median wait during a capture burst.</summary>
                public const string BurstWaitP50Ms = Prefix + "BurstWaitP50Ms";

                /// <summary>95th percentile wait during a capture burst.</summary>
                public const string BurstWaitP95Ms = Prefix + "BurstWaitP95Ms";

                /// <summary>Longest wait during a capture burst.</summary>
                public const string BurstWaitMaxMs = Prefix + "BurstWaitMaxMs";
            }

            /// <summary>
            /// How much the site is logging, counted where every write passes through.
            /// </summary>
            /// <remarks>
            /// Under <c>Runtime</c> rather than <c>CMS</c> because it is a property of the process
            /// and not of Optimizely: the counter sits in the host's logging pipeline, so it
            /// includes the framework, the site's own code and any library either of them calls.
            /// <para>
            /// Logging is on the hot path of everything and is almost never measured, which is why
            /// this is here at all. It is the denominator for a class of incident that otherwise
            /// has no evidence - a release that quietly doubles the write rate pays for it in
            /// synchronous I/O on request threads, and presents as latency with no matching change
            /// in the work a request does.
            /// </para>
            /// </remarks>
            public static class Logging
            {
                private const string Prefix = "Optimizely.Runtime.Logging.";

                /// <summary>
                /// All log writes per second.
                /// </summary>
                /// <remarks>
                /// The volume signal, and the one to chart against request rate. A ratio that
                /// holds steady as traffic changes is a site logging per unit of work; a ratio
                /// that climbs on its own is a level someone left turned up.
                /// </remarks>
                public const string WritesPerSecond = Prefix + "WritesPerSecond";

                /// <summary>Log writes at warning level per second.</summary>
                public const string WarningsPerSecond = Prefix + "WarningsPerSecond";

                /// <summary>
                /// Log writes at error level or worse per second.
                /// </summary>
                /// <remarks>
                /// Separate from the total because the two diverge in the cases worth catching. A
                /// flat total with a climbing error rate is a fault the site is absorbing quietly;
                /// a climbing total with a flat error rate is cost without a cause.
                /// </remarks>
                public const string ErrorsPerSecond = Prefix + "ErrorsPerSecond";
            }

            /// <summary>
            /// How cacheable the site's own responses are, read from the headers as they go out.
            /// </summary>
            /// <remarks>
            /// Under <c>Runtime</c> for the same reason as <see cref="Logging"/>: the reading is
            /// taken from the host's response pipeline, so it covers everything the process sends -
            /// pages, the editorial UI, static files, API endpoints - and not only what came out of
            /// Optimizely.
            /// <para>
            /// This is the one part of a site's performance that is decided entirely by the site
            /// and measured entirely somewhere else. A response that says nothing about caching is
            /// requested again, and the evidence lands in a CDN's dashboard, a browser's network
            /// tab or nowhere at all - never in the site's own telemetry, which sees only that it
            /// was asked. The counters here are the origin's side of that story: not how many
            /// requests arrived, but how many of the responses it sent gave anyone a reason not to
            /// ask twice.
            /// </para>
            /// </remarks>
            public static class Http
            {
                private const string Prefix = "Optimizely.Runtime.Http.";

                /// <summary>
                /// Responses classified per second.
                /// </summary>
                /// <remarks>
                /// The denominator for every share below, and the reading that tells a genuine
                /// zero from a gap: the shares are only published for intervals that carried
                /// traffic, so a flat zero here beside an empty share is a quiet site rather than a
                /// broken counter.
                /// </remarks>
                public const string ResponsesPerSecond = Prefix + "ResponsesPerSecond";

                /// <summary>
                /// Percentage of responses any cache may store and reuse, shared ones included.
                /// </summary>
                /// <remarks>
                /// The number a CDN sits on top of. Everything else being equal this is the ceiling
                /// on how much traffic can be kept away from the origin, and on most Optimizely
                /// sites it is far lower than whoever bought the CDN believes.
                /// </remarks>
                public const string PublicPercent = Prefix + "PublicPercent";

                /// <summary>Percentage reusable by the one browser that asked, and nothing else.</summary>
                public const string PrivatePercent = Prefix + "PrivatePercent";

                /// <summary>
                /// Percentage that may be stored but not reused without asking first.
                /// </summary>
                /// <remarks>
                /// A request reaches the site for every one of these. Read it with
                /// <see cref="ValidatorPercent"/>, which decides whether that request costs a 304
                /// or the whole body again.
                /// </remarks>
                public const string RevalidatePercent = Prefix + "RevalidatePercent";

                /// <summary>Percentage no cache may store at all.</summary>
                public const string NoStorePercent = Prefix + "NoStorePercent";

                /// <summary>
                /// Percentage that said nothing about caching at all.
                /// </summary>
                /// <remarks>
                /// Usually the largest share, and the one worth moving. These are not uncacheable -
                /// they are unspecified, left to whatever heuristic each client applies, which
                /// means the site cannot say how long its own output is being reused for or
                /// whether it is being reused at all.
                /// </remarks>
                public const string NoDirectivePercent = Prefix + "NoDirectivePercent";

                /// <summary>
                /// Stated freshness lifetime, in seconds, of a response that has one.
                /// </summary>
                /// <remarks>
                /// Only recorded for the reusable responses. A revalidating response is fresh for
                /// zero seconds by definition, and folding those zeros in would drag the mean
                /// towards a number no response ever stated. Read the counter's own aggregates:
                /// count is how many responses committed to a lifetime, mean is how long for.
                /// </remarks>
                public const string FreshnessSeconds = Prefix + "FreshnessSeconds";

                /// <summary>Percentage carrying an <c>ETag</c> or a <c>Last-Modified</c>.</summary>
                public const string ValidatorPercent = Prefix + "ValidatorPercent";

                /// <summary>
                /// Percentage claiming to be shared-cacheable while also setting a cookie.
                /// </summary>
                /// <remarks>
                /// Wasted headers, and the most common way a site undoes its own caching. A shared
                /// cache will not store a response carrying a <c>Set-Cookie</c> - doing so would
                /// hand a second visitor the first visitor's session - so the CDN declines it and
                /// the origin keeps serving it. Nothing errors and no header is wrong on its own,
                /// which is why this needs a counter to be visible at all.
                /// </remarks>
                public const string SharedCacheConflictPercent = Prefix + "SharedCacheConflictPercent";
            }

            /// <summary>The process itself, from the uptime reporter.</summary>
            public static class Process
            {
                private const string Prefix = "Optimizely.Runtime.Process.";

                /// <summary>Seconds since the worker process started.</summary>
                /// <remarks>
                /// The counter that makes the other counters readable. Almost everything else in
                /// this package is a rate or an average over a warm process, and every one of them
                /// lies for the first few minutes after a restart: the cache hit rate is low
                /// because the cache is empty, the GC pause series is short because the heap is
                /// small, and the thread pool is injecting threads. A hit rate that collapses at
                /// the same moment this drops to zero is a restart, not a cache problem, and
                /// telling those two apart currently means leaving the chart and going to look at
                /// the platform's own logs.
                /// <para>
                /// A sawtooth here is the finding in its own right. Unexplained recycles are
                /// common on Optimizely sites - memory limits, idle timeouts, an overlapped
                /// deployment, a crash the host restarted quietly - and the interval between the
                /// teeth is the thing to take to whoever owns the hosting.
                /// </para>
                /// </remarks>
                public const string UptimeSeconds = Prefix + "UptimeSeconds";
            }
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
