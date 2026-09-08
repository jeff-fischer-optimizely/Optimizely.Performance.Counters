# Optimizely CMS and Commerce Performance Counters

Application Insights can tell you that a page took four seconds. It cannot tell you that the page
made 380 content loads, that 340 of them missed the cache, or that the cart save underneath the
checkout took 1.8 of those seconds. Optimizely does not publish that. There is no EventSource, no
`ActivitySource`, no `DiagnosticListener` and no Windows performance counter category anywhere in
`EPiServer.dll`, `EPiServer.Framework.dll` or the Commerce assemblies, on any of V11, V12 or V13 —
so there is nothing for a collector to subscribe to, and no amount of configuring your APM will
make these numbers appear.

This package makes them appear. It wraps the Optimizely services that do the work in instrumented
decorators, publishes the timings and rates as .NET EventCounters on an EventSource named
`Optimizely-Performance`, and — where the host supports it — subscribes Application Insights to
them automatically. The metrics land in `customMetrics` next to your existing telemetry, queryable
in Kusto and chartable against request duration. Install, restart, no code changes.

It is the CMS-and-Commerce half of a pair.
[Optimizely.Performance.DotNetCounters](https://github.com/jeff-fischer-optimizely/Optimizely.Performance.DotNetCounters)
covers the runtime beneath your site — GC, thread pool, lock contention, request queue — and is a
hard dependency here, so the two always ship together. Between them you get the process and the
application: *the CLR is fine, your cache hit rate collapsed* is a conclusion neither one reaches
alone.

## Why you'd want this

- **Attribute the latency.** Request duration says the site is slow. `Optimizely.CMS.Content.LoadTimeMs`,
  `Optimizely.CMS.Cache.HitRate` and `Optimizely.Commerce.Orders.SaveTimeMs` say which layer is
  slow, and whether the cause is volume or per-call cost.
- **See the N+1 before it reaches production.** `LoadOperations` against `LoadTimeMs` separates
  "one slow load" from "four hundred fast ones", which is the single most common Optimizely
  performance defect and the one request telemetry hides best.
- **Watch the cache, which is where Optimizely performance actually lives.** Hit rate, miss rate
  and invalidation rate are not exposed by anything else. A publish storm that flushes the cache
  across a load-balanced cluster shows up here as an invalidation spike and a hit-rate cliff,
  minutes before it shows up as a support ticket.
- **Prove an upgrade or a code change.** The same counter names are emitted on V11, V12 and V13, so
  a before-and-after comparison across a CMS migration is a Kusto query rather than an argument.
- **Nothing to write.** Auto-registers through `IConfigurableModule`. No `Startup.cs` change, no
  attribute, no wrapper of your own.
- **Telemetry-agnostic.** EventCounters are an open .NET mechanism. Application Insights is
  auto-wired, DataDog auto-discovers the source, `dotnet-counters` attaches with no configuration
  at all, and none of them are a dependency of this library.

## When you'd want it

| Situation | What it gives you |
| --- | --- |
| A site is intermittently slow and the APM only shows "SQL was slow" | Whether the SQL is one query or a content-load loop, and whether the cache was serving |
| Load-testing before a launch | A per-operation baseline you can regress against, not just a p95 on the whole page |
| Tuning cache settings or a custom `ContentProvider` | Hit and miss rates that move while you change things |
| A Commerce checkout that degrades under load | Cart save and load timings separated from the rest of the request |
| Planning or validating a V11 → V12 → V13 upgrade | The same metric names on both sides of the move |
| A multi-server cluster with remote-event problems (V13) | Remote event rate, failure rate and delivery time |

And when you wouldn't: if you only need to know whether the *process* is healthy — GC, memory,
threads — the DotNetCounters package alone covers that and this one adds nothing. If you have a
commercial APM with full .NET call-tree profiling already deployed and paid for, it will show you
much of this and a great deal more; the case for these packages is that they are in-process,
already in your Application Insights bill, and specific to Optimizely's own seams.

---

## Installation

Install the package for what your site runs. Both may be installed side by side on a Commerce site.

```bash
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce
```

`Optimizely.Performance.Counters.Core` and `Optimizely.Performance.DotNetCounters` come in
transitively; there is no reason to reference either directly.

### Supported configurations

The target framework selects the Optimizely major, and the mapping is exact — the package is
compiled against that major's API surface, and the initialization module verifies the site it
loaded into matches.

| Optimizely | CMS | Commerce | Target framework |
| --- | --- | --- | --- |
| **V11** | 11.11.1+ | 13.0.0+ | `net472` |
| **V12** | 12.10.0+ | 14.5.0+ | `net6.0`, `net7.0`, `net8.0`, `net9.0` |
| **V13** | 13.0.2+ | 15.0.0+ | `net10.0` |

These are minimums, not pins — a site ahead of them installs the same package. They are set as low
as each major allows, on the grounds that a site which has drifted behind is exactly the one most
likely to want performance counters. Only the V12 CMS minimum is higher than we would like: 12.10.0
is inherited from `Optimizely.Performance.DotNetCounters`, which these packages always ship beside.
The reasoning behind each number is recorded in [Directory.Build.props](Directory.Build.props).

V12 on .NET 8 and .NET 9 is supported — those are ordinary V12 builds, not V13 builds. The only
combination that cannot work is a CMS major on a target framework mapped to a different one, and
that fails loudly at startup rather than misbehaving quietly.

> **NU1608 on restore is expected and harmless.** Commerce names `EPiServer.CMS.Core` as a range and
> its sibling `EPiServer.CMS.AspNet(Core)` pins that range's floor exactly, so NuGet lifts
> `CMS.Core` past the pin to reach the minimum `Optimizely.Performance.DotNetCounters` asks for. A
> site already running a current CMS lifts `AspNet(Core)` to match through its own reference, so
> this shows up on a bare test project rather than in production. See the note in
> [Directory.Build.props](Directory.Build.props).

### Making the counters visible

The package publishes whether or not anything is listening. To see the numbers you need one of:

- **Application Insights** on V12 or V13 — subscribed automatically at startup, nothing to
  configure beyond the connection string you already have.
- **DataDog** — discovers the `Optimizely-Performance` source on its own.
- **`dotnet-counters`** on V12 or V13 — `dotnet-counters monitor --process-id <pid> Optimizely-Performance`.
- **PerfView or your own `EventListener`** on V11, where `dotnet-counters` cannot attach.

> **The V11 wire format is different.** `EventCounter` polling is a .NET Core construct, so the
> `net472` build writes each measurement as a raw ETW event whose payload is `(name, value)` rather
> than creating a named `EventCounter`. The counter *names* are identical — they arrive as the first
> payload field instead of as the counter's identity — but a V11 listener reads events and does its
> own aggregation, where a V12 or V13 collector reads pre-aggregated counters.

See [examples/](examples/) for the settings to merge into a V11, V12 or V13 site, and
[docs/SMOKE_TEST.md](docs/SMOKE_TEST.md) for how to confirm each stage on a real one.

---

## What is instrumented

Thirty counters, from five decorated services. This is the whole list — the package instruments a
finite, hand-maintained set of seams rather than crawling for things to wrap.

| Prefix | Counters |
| --- | --- |
| `Optimizely.CMS.Content.` | `LoadTimeMs`, `LoadOperations`, `SaveTimeMs`, `SaveOperations`, `PublishTimeMs`, `PublishOperations`, `DeleteTimeMs`, `DeleteOperations`, `MoveTimeMs`, `MoveOperations`, `ItemsLoaded` |
| `Optimizely.CMS.Cache.` | `HitRate`, `MissRate`, `InvalidationsPerSecond`, `Operations` |
| `Optimizely.CMS.Events.` | `EventsPerSecond`, `RemoteEventsPerSecond`, `RemoteEventFailuresPerSecond`, `AverageRemoteEventDeliveryTimeMs` *(V13 only)* |
| `Optimizely.Commerce.Orders.` | `SaveTimeMs`, `SaveOperations`, `LoadTimeMs`, `LoadOperations`, `CreateTimeMs`, `CreateOperations`, `DeleteTimeMs`, `DeleteOperations`, `CartLineItemCount`, `CartTotal`, `CartsLoaded` |

Every timed operation emits a `TimeMs` and an `Operations` counter as a pair, so a duration can
always be read against the call count that produced it.

The decorated services:

| Service | Package | Versions |
| --- | --- | --- |
| `IContentLoader` | CMS | V11, V12, V13 |
| `IContentRepository` | CMS | V11, V12, V13 |
| `ISynchronizedObjectInstanceCache` | CMS | V11, V12, V13 |
| `IEventPublisher` | CMS | V13 only — V11 and V12 raise events through the static `Event` class, which has no seam to decorate |
| `IOrderRepository` | Commerce | V11, V12, V13 |

Counter names carry no dimensions. An overload that could be distinguished — a load by GUID versus
by `ContentReference` — reports under the same base name, because Application Insights matches
EventCounter names exactly and a name with a dimension baked into it is a name nobody subscribed to.

---

## How it works

```
  InstrumentedContentLoader
  InstrumentedContentRepository
  InstrumentedSynchronizedObjectInstanceCache   ──→  IMetricTracker
  InstrumentedEventPublisher        (V13)                  |
  InstrumentedOrderRepository                              v
                                              EventCounterMetricTracker
                                                           |
                                                           v
                             OptimizelyPerformanceEventSource ("Optimizely-Performance")
                                                           |
                    +--------------------------------------+--------------------+
                    v                                      v                    v
     AI EventCounterCollectionModule                    DataDog          dotnet-counters
```

An `[InitializableModule] : IConfigurableModule` in each package runs during container
construction, before any module initializes. It logs the detected and expected Optimizely version
and throws if they disagree, detects the telemetry systems present, registers
`EventCounterMetricTracker` as `IMetricTracker`, and wraps the services above using
`context.Services.Intercept<T>()`. The Commerce module does the same and is idempotent about the
shared parts, so installing both packages registers telemetry once.

Nothing in the library listens to its own EventSource, and it does not republish the counters that
`Optimizely.Performance.DotNetCounters` collects. Collection is entirely the host's business.

**Overhead.** Timing is allocation-free: `OperationTimer` is a `readonly struct` over a single
`Stopwatch.GetTimestamp()`, and the decorators forward through explicit try/catch blocks rather
than a `Measure<T>(name, () => ...)` helper, because that helper allocates a closure per call. When
no collector is attached, `EventSource.IsEnabled()` short-circuits and the cost is a branch. The
cache and event decorators sit on paths that fire thousands of times a second, so those accumulate
into interlocked fields and flush on a 60-second timer instead of writing per call. What your
collector *reads* is on its own schedule: `EventCounterCollectionModule` polls at 60 seconds by
default, `dotnet-counters` at one.

### Startup log

Set `Optimizely.Performance.Counters.CMS.Initialization` and
`...Commerce.Initialization` to `Information`:

```
[INFO] CMS Version Detection:
       Detected Version: V12
       Expected Version: V12
       CMS.Core: 12.24.1
       ...
[INFO] Telemetry Detection: Telemetry Systems: Application Insights 2.22.0.997, EventCounters
[INFO] Registered 30 Optimizely EventCounters with Application Insights
[INFO] Registered IMetricTracker: EventCounterMetricTracker
[INFO] Registering CMS performance counter decorators
[INFO] Registered decorators: IContentLoader, IContentRepository, ISynchronizedObjectInstanceCache.
       IEventPublisher is V13-only and was not registered.
[INFO] Optimizely CMS Performance Counters configured successfully
```

A version mismatch throws out of `ConfigureContainer` and takes the site down at startup. That is
deliberate: a counters package that silently instruments the wrong API surface is worse than one
that refuses to start.

---

## Alternatives

Worth knowing what else exists before adopting anything, and the honest answer differs sharply by
version. Everything below was verified against the shipped assemblies rather than the
documentation.

### The common ground: Optimizely publishes no telemetry of its own

On every version checked — CMS 11.21.5, 12.24.1 and 13.1.1, and Commerce 13, 14 and 15 —
`EPiServer.dll`, `EPiServer.Framework.dll` and the Commerce assemblies contain no reference to
`EventSource`, `ActivitySource`, `DiagnosticSource`, `DiagnosticListener`,
`System.Diagnostics.Metrics`, `PerformanceCounterCategory` or `CounterCreationData`. There is no
hook. Any approach that works by subscribing to something the product emits has nothing to
subscribe to, which rules out the whole OpenTelemetry auto-instrumentation family for
Optimizely-specific metrics — those libraries cover ASP.NET Core, `HttpClient` and `SqlClient`, and
none of them knows what an `IContentLoader` is.

That leaves four real families of alternative.

#### 1. `ContentProvider`'s built-in statistics — the closest thing that already exists

`EPiServer.Core.ContentProvider` has exposed a public statistics surface since long before V11, and
it is still there in V13:

| Member | |
| --- | --- |
| `PageFetchCount`, `PageFetchCacheHits`, `PageFetchDatabaseReads` | Content fetches, and how they were served |
| `ListingFetchCount`, `ListingFetchCacheHits`, `ListingFetchDatabaseReads` | The same for child listings |
| `StatisticsCollectedSince` | When the current window started |
| `ResetCounters()` | Starts a new window |

Genuinely useful, and genuinely the built-in answer to "is my cache working". The trade-offs: they
are cumulative in-process totals with no publishing mechanism, so you write the polling loop, the
delta arithmetic and the telemetry call yourself; they are per-`ContentProvider` instance, so a
site with several providers needs enumerating and summing; the window is global, so anything else
that calls `ResetCounters()` moves your baseline underneath you; the numbers are process-local, so
each node of a cluster is separate; and the scope is content *fetching* only — no save, publish,
move or delete timings, no durations at all, and nothing whatsoever from Commerce. If cache
effectiveness is the only question you have, this is a reasonable afternoon's work and costs you no
dependency.

#### 2. Application Insights dependency and request telemetry

Already in your site, and it does real work: the SQL round trips underneath a content load appear
as dependency telemetry with durations, and the Profiler can sample call stacks on a slow request.

What it cannot do is attribute. A dependency call tells you a query ran; it does not tell you that
`IContentLoader.Get<T>` was the caller, that it ran 380 times on one page, or — most importantly —
anything at all about the loads that were served *from cache* and therefore issued no query. The
cache is invisible to dependency tracking by construction, and the cache is where Optimizely
performance is won or lost. Request telemetry has the same shape of gap one level up: it times the
page, not the layers inside it.

#### 3. Write the decorators yourself

Entirely viable — `Intercept<T>()` is a supported, documented Optimizely extension point and this
package is not doing anything you could not do. Budget realistically, though. `IContentLoader`
alone has twenty-three members to forward; a decorator that forwards twenty-two of them and quietly
drops one is a silent behaviour change in the CMS. The counter names have to match on the emit side
and the subscribe side exactly, or Application Insights collects nothing and never says so. And on
a path that runs hundreds of times per request, the tidy `Measure<T>(name, () => inner.Get(...))`
helper you will be tempted to write allocates a closure per call — which is why this library
deliberately does not have one.

#### 4. Commercial APM

New Relic, Dynatrace and AppDynamics will profile the .NET call tree and surface `IContentLoader`
timings without anyone instrumenting anything, along with a great deal this package does not
attempt. The cost is a second agent, a second pipeline and a second bill, and on a DXP site the
question of whether you are allowed to install one at all.

### On older Optimizely specifically

**V11 (CMS 11, Commerce 13, .NET Framework 4.7.2)** is where the alternatives are thinnest and the
gap is widest:

- **The modern diagnostics stack does not reach it.** EventCounters and EventPipe are .NET Core
  constructs. `dotnet-counters` cannot attach to a V11 site at all, and OpenTelemetry's runtime
  instrumentation is built around the .NET Core runtime. PerfView and a hand-written `EventListener`
  are the tools, and both are attach-and-watch rather than continuous collection.
- **PerfMon shows you nothing Optimizely-specific.** V11 registers no performance counter category
  — verified: `EPiServer.dll` 11.21.5 contains no `PerformanceCounterCategory` and no
  `CounterCreationData`. What PerfMon gives you is the CLR, ASP.NET and IIS counters, which is
  exactly the ground the DotNetCounters package covers. Content, cache and Commerce remain dark.
- **`EPiServer.Diagnostics.Internal.IPerformanceCounter` is not a way in.** It exists in V11 and
  V12, and it is in an `Internal` namespace: no support commitment, no published surface, and it
  appears in the graph only as a constructor parameter of an internal dependency helper. Building
  on it means building on something Optimizely may remove in a patch release.
- **`EPiServer.Framework.Initialization.TimeMeters` measures startup only.** Present in 11, 12 and
  13, useful for finding an initialization module that takes eight seconds, and silent about
  everything after the site is up.
- **The `ContentProvider` statistics above are the one real built-in**, with all the caveats listed.

So on V11 the practical menu is: poll `ContentProvider` yourself, buy an APM, or instrument the
seams. This package is the third, and the V11 build is the same code and the same counter names as
the V12 and V13 builds, which is the point if you are running mixed versions through a migration.

One honest caveat for V11: **Application Insights registration is not implemented there yet.**
`TelemetryStartup.Configure` needs an `IServiceCollection` and V11 configures services through
`IServiceConfigurationProvider`. Detection runs and is logged; the counters are published to the
EventSource and are readable with PerfView or your own `EventListener`, but nothing is subscribed to
Application Insights automatically. See the gaps below.

**V12 and V13** have a slightly better menu — `dotnet-counters` works, Application Insights
subscription is automatic, and OpenTelemetry is a credible pipeline for everything *except* the
Optimizely-specific metrics, which it still has no source for. The `ContentProvider` and
commercial-APM options are unchanged. The argument for this package on V12 and V13 is less "nothing
else exists" and more "this is the vetted, maintained version of the thing you would otherwise
build, and it reports the same names your V11 sites do".

---

## Kusto

```kusto
// Everything this package publishes
customMetrics
| where name startswith "Optimizely."
| summarize avg(value), count() by name, bin(timestamp, 1m)
| order by name asc
```

```kusto
// Content load cost, split into volume and per-call latency
customMetrics
| where name in ("Optimizely.CMS.Content.LoadOperations", "Optimizely.CMS.Content.LoadTimeMs")
| summarize avg(value) by name, bin(timestamp, 5m)
| evaluate pivot(name, avg(avg_value))
| render timechart
```

```kusto
// Cache effectiveness against invalidation - a publish storm shows as both moving at once
customMetrics
| where name in ("Optimizely.CMS.Cache.HitRate", "Optimizely.CMS.Cache.InvalidationsPerSecond")
| summarize avg(value) by name, bin(timestamp, 1m)
| render timechart
```

```kusto
// Commerce cart save latency percentiles
customMetrics
| where name == "Optimizely.Commerce.Orders.SaveTimeMs"
| summarize percentiles(value, 50, 95, 99) by bin(timestamp, 15m)
| render timechart
```

---

## Troubleshooting

**The startup log shows nothing from either module.** The module never ran. Confirm the package is
in the site's `bin`, not merely referenced by a project that is not deployed.

**The site fails to start with a version mismatch.** The target framework and the CMS major
disagree — see the support matrix above. This is the intended failure, not a bug.

**Counters appear in `dotnet-counters` but not in Application Insights.** Collection is fine and
export is not. Check the connection string, and turn adaptive sampling off while you verify: a
counter that is being sampled away is indistinguishable from one that is not being collected.

**A counter is missing entirely.** EventCounters that have never been written are not listed at
all. Drive the matching traffic first. `CartLineItemCount` and `CartTotal` in particular need a
populated cart to be saved or loaded.

**Startup log is clean but no counters move.** The decorators registered but are not on the path
your traffic takes. Resolve `IContentLoader` from the site's container and confirm the concrete
type is `InstrumentedContentLoader`.

---

## Current gaps

Stated plainly, because a monitoring package that overstates its coverage is worse than one that
does not exist:

- **Application Insights auto-registration on V11.** Described above.
- **No configuration.** Counters cannot be disabled individually or collectively yet.
- **Commerce is `IOrderRepository` only.** Pricing, inventory, promotions and payments are not
  instrumented.
- **No search counters.** Neither Find (V11/V12) nor Graph (V13).
- **No infrastructure counters.** Database, blob storage, Dynamic Data Store and Service Bus are
  not instrumented here; the runtime-level equivalents are in the DotNetCounters package.

---

## Repository layout

| | |
| --- | --- |
| [src/](src/) | The three packages, plus `src/Shared` for code that touches EPiServer types from both |
| [tests/](tests/) | 116 tests across net472, net8.0 and net10.0 — container registration, published counter names, and that the emitted set matches the subscribed set |
| [examples/](examples/) | Per-version settings, and `verify-package-install`, which installs the built packages from a local feed and asserts they bind and detect the right major. Builds on all six target frameworks; runs on whichever runtimes are installed |
| [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md) | Five-stage verification on a real site |
| [ARCHITECTURE.md](ARCHITECTURE.md), [TELEMETRY_ARCHITECTURE.md](TELEMETRY_ARCHITECTURE.md) | Design notes |

---

## Related packages

- [Optimizely.Performance.DotNetCounters](https://github.com/jeff-fischer-optimizely/Optimizely.Performance.DotNetCounters)
  — .NET runtime and ASP.NET counters. Required, and installed transitively.

## License

Apache-2.0
