# Gap analysis

What this package does not measure, why each gap matters, and what it would take to close it.
Written against Optimizely CMS 13.1.1 / Commerce 15.1.0 and the shipped assemblies in the local
NuGet cache, not against documentation — every seam named below was confirmed to exist in the
binaries for the majors it is claimed for.

The [Current gaps](../README.md#current-gaps) section of the README is the honest short list. This
document is the long one: it adds the gaps that section does not know about, ranks them, and names
the actual interface to decorate.

---

## Tier 0: the delivery path is on a clock

**The Application Insights EventCounter collector has no future version, and the counters go dark
when a site upgrades its SDK.**

This is the only gap that can take the whole package to zero, and it is not hypothetical.

| Package | Latest version |
| --- | --- |
| `Microsoft.ApplicationInsights` | **3.1.2** |
| `Microsoft.ApplicationInsights.AspNetCore` | **3.1.2** |
| `Microsoft.ApplicationInsights.EventCounterCollector` | **2.23.0** — no 3.x |
| `Microsoft.ApplicationInsights.PerfCounterCollector` | **2.23.0** — no 3.x |

Application Insights Classic API SDK 2.x is deprecated and retires **31 March 2027**. SDK 3.x is
OpenTelemetry underneath, keeps most of `TelemetryClient`, and explicitly does not republish some
2.x packages. `EventCounterCollector` is one of them, and it is the single type
`ApplicationInsightsRegistration` reflects for — `Microsoft.AI.EventCounterCollector`. The moment a
site moves to 3.x, or to `UseAzureMonitor()`, that lookup returns null, registration logs its
warning, and all seventy-five counters are published to nobody. The V11 `PerformanceCollectorModule`
path used for the SQL pool counters dies the same way, one major later.

There is no drop-in replacement to reflect for instead. The Azure Monitor OpenTelemetry Distro has
no `EventCounterCollectionModule` equivalent; the community bridge,
`OpenTelemetry.Instrumentation.EventCounters`, has never left alpha (latest `1.18.0-alpha.1`) and
Microsoft explicitly disclaims support for community instrumentation. The supported migration for a
publisher in our position is `EventSource`/`EventCounter` → `System.Diagnostics.Metrics.Meter`.

**What to do.** Emit through a `Meter` *in addition to* the EventSource, on net6.0 and up. The
architecture is already shaped for this: everything goes through `IMetricTracker`, so this is a
second tracker (`MeterMetricTracker`) plus a composite, not a rewrite of a single decorator. The
`CounterNames` members become instrument names under a meter called `Optimizely-Performance`, which
a consumer registers with one line — `.WithMetrics(m => m.AddMeter("Optimizely-Performance"))` —
and which then works for the Azure Monitor Distro, AI SDK 3.x, OTLP, Prometheus and Grafana alike.
The EventSource stays: it is the only path on net472, and it is what `dotnet-counters` and DataDog
already discover.

Two things to get right, because they are where an EventCounter mind gets a Meter wrong:

- **Instrument type carries the aggregation.** A `TimeMs` counter is a `Histogram<double>`, not a
  gauge of the mean. This is a straight upgrade — the EventCounter path loses the distribution and
  reports mean/min/max per interval, whereas a histogram keeps buckets, so p95 becomes available
  for the first time.
- **Meters have dimensions and EventCounters do not.** Resist using them at first. The whole
  "one counter per GC generation, one per eviction reason" design exists because EventCounters
  cannot carry a tag, and splitting that decision by tracker means the two backends disagree about
  what the counter set even is. Ship name-parity first; add tags later, deliberately.

---

## Tier 1: V13 is a different product and we instrument it as if it were V12

The V13 build compiles, detects its version, decorates the same six services and finds the cache
lock in its new home in `EPiServer.Cache`. What it does not do is measure anything that is new
about V13 — and what is new about V13 is where the time now goes.

### 1. Optimizely Graph, query side — nothing

Graph is not an optional add-on on 13. Search & Navigation is gone from the product, Graph and Opti
ID are mandatory components of the licence, and Content Manager — the interface that replaces the
content tree as the editors' primary navigation — is a Graph client. Every search, every editor
content lookup and every `Content Variations` resolution is now an outbound HTTPS call to a service
Optimizely runs, and this package cannot see a single one of them.

The seam is clean and confirmed: `Optimizely.Graph.Core.Client.GraphClient : IGraphClient`,
registered by `AddGraphCore` as a typed `HttpClient` with `AddStandardResilienceHandler`.

That resilience handler is the reason a decorator is worth more here than a dependency chart in
Application Insights. It wraps every call in `HttpRateLimiterStrategyOptions`,
`HttpRetryStrategyOptions` and a circuit breaker, so a Graph call that took 4 s may have been one
slow call, or three fast failures and a retry, or a rate limiter holding the request before it ever
went out. AI dependency telemetry shows the attempts; nothing shows the *shape*.

Counters worth having:

| Counter | Why |
| --- | --- |
| `QueryTimeMs` / `QueryOperations` | The pair, as everywhere else |
| `QueryFailures` | Split from the total; a Graph outage is a site outage on 13 |
| `ThrottledQueries` | HTTP 429. Graph is a shared multi-tenant service with rate limits, and this is the counter that turns "Graph is slow" into "we are over our budget" |
| `RetriedQueries` | Attempts above one, from the resilience pipeline |
| `CircuitBreakerOpenPercent` | Duty cycle, same reasoning as `LockWriteHeldPercent` |
| `ResponseBytes` | Optimizely documents that content items over 1 MB cause timeouts, and large queries hitting the complexity threshold changes how Graph plans them. Payload size is the early warning |

### 2. Optimizely Graph, sync side — nothing, and this is the one editors feel

Optimizely's own documentation puts content sync latency at **5–15 minutes for scheduled sync and
1–3 seconds for event-driven sync**, longer for bulk. Schema propagation takes several minutes.
That is a documented, expected, invisible delay between "an editor published" and "the site can
find it" — and it is the number every "why isn't my page showing up" ticket is actually about.

The seams are all in `Optimizely.Graph.Cms`, and there are more of them than expected:
`DeltaSynchronizationJob` / `DeltaSynchronizationJobStore` / `DeltaSynchronizationJobState`,
`ContentIndexingJob` / `ContentIndexingJobService`, `EventIndexer` (the event-driven path),
`Optimizely.Graph.Cms.Client.SyncClient`, and batching controlled by `BatchMaxCount`,
`BatchMaxSize` and `BatchMaxConcurrency`.

Counters worth having: sync lag in seconds (publish timestamp to indexed), items and batches
synced, `AppendFailedBatches` as a failure counter, delta job duration, and whether a smooth
rebuild is in progress — `__smoothRebuildState` exists precisely because a rebuild changes the
performance envelope, and a chart that does not know one is running is misleading for its duration.

Two CMS 13 fixes in the release notes are the argument for this section on their own: CMS-51700, a
synchronous content comparison in the Graph sync event handler slowing the publish UI, and
CMS-54163, sync batches exceeding the 29 MB request limit and failing the indexing job with a 400.
Both are exactly the class of fault a sync counter names in one glance and a log search takes a day
to find.

### 3. Outbound HTTP generally — we subscribe to SqlClient's counters but not to the network's

The package already does the right thing once: it subscribes to the twelve
`Microsoft.Data.SqlClient` connection pool counters on the grounds that they are the next question
after a cache miss storm. The same argument now applies with more force to HTTP, because on 13 the
CMS itself is an HTTP client — Graph, Opti ID, DAM, ODP — and `HttpClient` misuse and socket
exhaustion are on every Optimizely performance checklist that exists.

`System.Net.Http`, `System.Net.Sockets` and `System.Net.NameResolution` all publish EventCounters
that require nothing but subscription: requests started/failed per second, current connections,
requests queued, connection queue duration, DNS lookup duration. On .NET 8+ the same ground is
covered better by the built-in `System.Net.Http` *Meter*, with `http.client.request.duration`
tagged by host — which arrives for free once the Tier 0 Meter work is done and the consumer
registers the meter.

This is the cheapest item in this document: additions to `SqlClientCounters`-shaped lists, no new
decorator, no new thread.

---

## Tier 2: cross-version gaps that cause real incidents today

These are not V13 gaps. They apply to V11, V12 and V13 equally, and the seams exist on all three.

### 4. Remote event propagation — measured on the wrong side, on one major

`InstrumentedEventPublisher` measures how long *publishing* an event took, on V13 only. Publishing
is the cheap half and the half that never fails interestingly. The expensive, failing, invisible
half is **receipt**: how long a cache invalidation raised on node A takes to reach node B, and
whether it arrives at all.

This matters more than any other item here, because it is a known, open, unfixed platform
limitation rather than a misconfiguration. Optimizely's cache is not distributed — each instance
holds its own in-process cache and only *invalidation* is broadcast over Service Bus. There is a
standing platform feedback item stating the event system "isn't fast enough to keep up" on
high-traffic multi-instance sites, producing stale data and `IOrderGroup` exceptions in Commerce.
CMS-50193 raised the default Service Bus `PrefetchCount` from 100 to 500 for exactly this. And a
site running without an event provider silently falls back to `NullEventProvider`, which discards
everything, with no symptom other than nodes disagreeing.

Nothing in the product reports the lag. `EPiServer.Events` hands us everything needed to:

| Counter | Source |
| --- | --- |
| `EventDeliveryLagMs` | `EventMessage.Sent` against `TimeReceived` — the cluster consistency number |
| `RemoteEventsReceivedPerSecond` | `RaisedByRemoteSite` on the received event |
| `EventSequenceGaps` | `EventSequence` / `VerifySequence` / `RemoteRaiserSequences` already detect dropped and out-of-order messages internally. A gap is a node whose cache is now silently wrong |

`IEventRegistry` (`EPiServer.Events.Clients`) is referenced from `EPiServer.dll` on 11.11.1,
12.10.0 and 13.1.1, so the receive side is reachable on every major — unlike `IEventPublisher`.
That makes this the one item that would extend event instrumentation *down* the matrix rather than
leaving V11 and V12 with nothing.

Worth adding alongside: a single startup log line naming the configured event provider, because
`NullEventProvider` is a black hole that currently announces itself nowhere.

### 5. `RemoveLocal` versus `Remove` — a counter we could ship this afternoon

`InstrumentedSynchronizedObjectInstanceCache` decorates both, and currently records both as
`CacheRemovalPath.Local` — indistinguishable in the output.

Splitting them is close to free and surfaces the single most commonly documented cause of stale
content on secondary nodes: application code that caches with `Insert` and evicts with
`RemoveLocal`, which does not broadcast. The bug is invisible on a single-instance environment,
appears only under load balancing, and presents as "one node is serving old content" with nothing
in any log. A counter that says *this site calls RemoveLocal four hundred times a minute* answers
it immediately.

One new name, one changed argument, inside a decorator that already exists. Highest value per line
of code in this document.

### 6. Scheduled jobs — nothing, on any major

`IScheduledJobExecutor` is present on 11.11.1, 12.10.0 and 13.1.1. Scheduled jobs are where a site
does its heaviest database and blob work, they run unattended, they overlap when one runs long, and
a job that has quietly gone from four minutes to fifty is a classic cause of "the site is slow every
night" that no request telemetry can explain. CMS 13 shipping scheduled job monitoring in the admin
UI is Optimizely conceding the point.

Counters: duration and count per execution, failures, concurrent executions, and time since last
successful run. The last of those is the one that catches a job that stopped running altogether,
which is the failure mode no duration chart can show.

### 7. Blob storage — nothing

`IBlobFactory` is present on all three majors. DXP guidance is that everything except code lives in
blob storage, and writing to local disk instead is documented as a cause of app restarts. On a
media-heavy site blob read latency is a direct component of response time, and it is remote I/O
that presents as an unexplained gap in a request trace. Read/write duration and byte counts, from
one decorator.

### 8. Process restarts and uptime — nothing, and it is context for everything else

App restarts are near the top of every Optimizely DXP troubleshooting list, and a restart resets
every cache, every counter accumulator and every warm path in the process. A cold instance rejoining
a load balancer without warm-up is documented as a cause of response-time spikes.

A process uptime counter is a handful of lines and makes every other chart in this package readable:
a cache hit rate that collapses at the same instant uptime resets is a restart, not a cache problem,
and right now those two are indistinguishable.

---

## Tier 3: breadth we have already admitted to

The README states these. They are listed here with their verified seams so that closing them is a
scheduling decision rather than a research task.

### Commerce beyond `IOrderRepository`

Every one of these was confirmed present in Commerce **13.0.0, 14.5.0 and 15.1.0** — the three
floors — so a single decorator serves the whole matrix:

| Interface | Covers |
| --- | --- |
| `IPriceService`, `IPriceDetailService` | Price lookup, the hottest read path in a catalog |
| `IInventoryService` | Inventory checks, frequently a remote call |
| `IPromotionEngine` | Promotion evaluation, superlinear in cart size and rule count |
| `IPaymentProcessor` | Payment gateway latency — third-party, and the checkout step users abandon |
| `ITaxCalculator`, `IShippingCalculator` | Both usually third-party calls on the checkout path |
| `ICatalogSystem` | Legacy catalog access, still on the hot path in older solutions |

Priority order within that list is promotions, pricing, payments: promotions because it is the one
that degrades nonlinearly, pricing because it is the most frequent, payments because it is the one
where the latency belongs to somebody else and you need evidence to say so.

### Search on V11 and V12

`EPiServer.Find.IClient` exists and Find remains supported on 12. It is dropped in 13, which makes
this a diminishing asset — but V11 and V12 sites will outnumber V13 sites for years, and Find query
latency is a top-three cause of slow pages on the sites that use it. Worth doing only if V11/V12
reach is a goal in its own right; skip it if effort is better spent on Graph.

### Dynamic Data Store

Real, uninstrumented, and a genuine performance trap on sites that misuse it. Lowest priority here
because the sites that abuse DDS tend to know they do.

---

## Non-gaps, checked and closed

Recorded so nobody re-investigates them:

- **The V13 cache lock still resolves.** `MemoryObjectInstanceCache` moved from `EPiServer.Framework`
  (12.24.1) to `EPiServer.Cache` (13.0.2 and 13.1.1). `CacheLockLocator` already tries
  `EPiServer.Cache` first and matches the field by shape rather than name, so the move that broke
  the naive version of this probe is handled.
- **The Optimizely majors and floors are current.** CMS 13.1.1 and Commerce 15.1.0 exist; our floors
  are 13.0.2 and 15.0.0. Floors, not pins — nothing to do.
- **Newtonsoft → System.Text.Json in CMS 13** does not touch this package; it serialises nothing.
- **CMS 13's DI reshuffle** (`AddCmsFramework()`, `AddCmsCache()`, `FrameworkInitialization` no
  longer `IConfigurableModule`) does not affect our modules — `IConfigurableModule` and
  `Intercept<T>` are intact, and the `IMemoryCache` decoration is already deferred to
  `ConfigurationComplete` for exactly this class of ordering problem.
- **SaaS CMS is out of scope** by construction. No code deploys there, so no decorator can run.

---

## Suggested order

Ranked by consequence divided by effort, not by tier.

1. ~~**`RemoveLocal` split** — hours. A named bug class, inside an existing decorator.~~ **Done.**
   Shipped with process uptime, as `SynchronizedInvalidationsPerSecond`,
   `LocalOnlyInvalidationsPerSecond`, `RemoteInvalidationsPerSecond` and
   `Optimizely.Runtime.Process.UptimeSeconds`. `InvalidationsPerSecond` is unchanged and still the
   total; `Clear()` on V11 and V12 is counted there and under no route, so the three do not sum to
   it. Cascade attribution was deliberately left alone — `Remove` and `RemoveLocal` still both
   report as `CacheRemovalPath.Local`, because the fan-out does not differ by whether the removal
   broadcast.
2. **Meter emission path** — days. Nothing else matters if the counters cannot be collected in 2027.
3. **Remote event receive side** — days. The highest-value *new* measurement in this document, and
   the only one that improves V11 and V12.
4. **Graph query decorator** — days. Without it the V13 build is blind to V13's defining subsystem.
5. **Graph sync lag** — days. The number editors actually complain about.
6. **Scheduled jobs and the HTTP/socket/DNS counter subscriptions** — each small, independently
   shippable, all three majors. Process uptime came out of this group early, with item 1.
7. **Commerce breadth** — promotions, pricing, payments, in that order.
8. **Blob storage, Find, DDS** — real, and last.

## Verification still owed

Three assumptions in this document are inferred from assembly metadata and should be confirmed
against a running site before code is written against them:

- That `IGraphClient` is registered in a form `context.Services.Intercept<T>()` can wrap, given it
  is created through `IHttpClientFactory`.
- That `IScheduledJobExecutor` is resolvable and interceptable on V11, whose container is not
  Microsoft DI.
- That the receive-side event hook is reachable without an `EventListener`, keeping the rule in
  [How it works](../README.md#how-it-works) intact — `IEventRegistry` and the `Raised` event are
  ordinary .NET events, so this should hold, but it is the one design constraint worth checking
  first rather than last.
