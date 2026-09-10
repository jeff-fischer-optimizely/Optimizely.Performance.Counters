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
`Optimizely-Performance` — and, on V12 and V13, to a `Meter` of the same name for OpenTelemetry —
and where the host supports it, subscribes Application Insights to them automatically. The metrics
land in `customMetrics` next to your existing telemetry, queryable in Kusto and chartable against
request duration. Install, restart, no code changes.

Alongside the decorators it runs a small set of **probes** — components that measure a condition
nothing publishes, rather than reading a counter somebody else maintains. Thread pool queue
*delay* instead of queue *length*, garbage collection *pause durations* instead of a percentage,
lock wait *distributions* instead of a contention count, and the depth of the queue on Optimizely's
own cache lock. It also subscribes your APM to the `Microsoft.Data.SqlClient` connection pool
counters, because a cache problem in Optimizely becomes a connection pool problem about thirty
seconds later and you want both series on one chart.

And because the first question after any of these moves is *did we ship something*, it records what
is in the bin folder: a fingerprint over every assembly's compiled identity, emitted on startup and
on a heartbeat, and stamped onto every request the site serves. Comparing a release against the one
before it becomes a `summarize` over a dimension rather than an argument about timestamps.

It is the CMS-and-Commerce half of a pair.
[Optimizely.Performance.DotNetCounters](https://github.com/jeff-fischer-optimizely/Optimizely.Performance.DotNetCounters)
subscribes your APM to the stock .NET, ASP.NET, IIS and OS counters beneath your site — GC, thread
pool, lock contention, request queue — and is a hard dependency here, so the two always ship
together. Between them you get the process and the application: *the CLR is fine, your cache hit
rate collapsed* is a conclusion neither one reaches alone.

The division is by *origin*, not by subject. Anything the platform already publishes is that
package's business; anything that has to be measured is this one's. So both have something to say
about the garbage collector, and they do not overlap: DotNetCounters forwards `time-in-gc`, which
the runtime maintains, and the GC pause probe here reports how long individual gen 2 collections
actually stopped your threads, which nothing maintains.

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
- **See the cascade, not just the call.** Optimizely hangs cache entries off master keys, so one
  `Remove` can discard a whole subtree. `RemovalFanOut` is the amplification factor — count is
  removals asked for, mean is how many entries each one actually took. It is how a site collapses
  its own cache from a single innocuous-looking call.
- **Catch the stall that no counter can see.** Optimizely's memory cache serialises every write
  behind one process-wide reader/writer lock, and writers exclude readers, so during an
  invalidation cascade the entire site can be blocked while hit rate, CPU and GC all look healthy.
  `monitor-lock-contention-count` cannot see it either — a reader/writer lock is not a monitor.
- **Follow it downstream.** Cache miss storm, connection pool exhaustion, thread pool starvation:
  the cache counters, the SqlClient pool counters and the thread pool queue *delay* share one
  timeline, so the causal chain is a single chart rather than three tabs and an assumption.
- **See the load you told somebody else to send you.** Every other counter here measures work the
  process did; the cacheability counters measure what share of responses the site gave a browser or
  a CDN permission to reuse. When that permission is missing the request simply comes back looking
  like ordinary traffic, so the origin sees load it has no reason to question — including the case
  worth acting on immediately, a response marked `public` that also sets a cookie and which no
  shared cache will therefore store at all.
- **Prove an upgrade or a code change.** The same counter names are emitted on V11, V12 and V13, so
  a before-and-after comparison across a CMS migration is a Kusto query rather than an argument.
- **Nothing to write.** Auto-registers through `IConfigurableModule`. No `Startup.cs` change, no
  attribute, no wrapper of your own.
- **Telemetry-agnostic, on two mechanisms.** Every counter is published both as an EventCounter and
  — on V12 and V13 — to a `System.Diagnostics.Metrics` meter, under the same name. Application
  Insights is auto-wired, DataDog auto-discovers the EventSource, `dotnet-counters` attaches with no
  configuration at all, and OpenTelemetry or Azure Monitor collects the meter with one line. None of
  them are a dependency of this library, and no counter is reachable on only one of the two paths.

## When you'd want it

| Situation | What it gives you |
| --- | --- |
| A site is intermittently slow and the APM only shows "SQL was slow" | Whether the SQL is one query or a content-load loop, and whether the cache was serving |
| Load-testing before a launch | A per-operation baseline you can regress against, not just a p95 on the whole page |
| Tuning cache settings or a custom `ContentProvider` | Hit and miss rates that move while you change things |
| A Commerce checkout that degrades under load | Cart save and load timings separated from the rest of the request |
| Planning or validating a V11 → V12 → V13 upgrade | The same metric names on both sides of the move |
| A multi-server cluster with remote-event problems (V13) | Remote event rate, failure rate and delivery time |
| Origin traffic higher than the CDN report suggests it should be | What share of responses is actually cacheable, and whether cookies are stopping the CDN storing them |

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
  configure beyond the connection string you already have. This needs the *classic* 2.x SDK; see
  [OpenTelemetry, Azure Monitor and Application Insights 3.x](#opentelemetry-azure-monitor-and-application-insights-3x)
  if you are on anything newer.
- **OpenTelemetry, `UseAzureMonitor()` or Application Insights SDK 3.x** on V12 or V13 — one line,
  `.WithMetrics(m => m.AddMeter("Optimizely-Performance"))`.
- **DataDog** — discovers the `Optimizely-Performance` source on its own.
- **`dotnet-counters`** on V12 or V13 — `dotnet-counters monitor --process-id <pid> Optimizely-Performance`.
- **PerfView or your own `EventListener`** on V11, where `dotnet-counters` cannot attach.

On V11 the SQL connection pool counters are the exception and do reach Application Insights
automatically, because there they are Windows performance counters rather than EventCounters and
are collected by a `PerformanceCollectorModule` this package builds and initializes itself. That
path needs no `IServiceCollection`, which is what makes it possible where the EventCounter
registration is not — see [SQL connection pool counters on V11](#sql-connection-pool-counters-on-v11).

> **The V11 wire format is different.** `EventCounter` polling is a .NET Core construct, so the
> `net472` build writes each measurement as a raw ETW event whose payload is `(name, value)` rather
> than creating a named `EventCounter`. The counter *names* are identical — they arrive as the first
> payload field instead of as the counter's identity — but a V11 listener reads events and does its
> own aggregation, where a V12 or V13 collector reads pre-aggregated counters.

See [examples/](examples/) for the settings to merge into a V11, V12 or V13 site, and
[docs/SMOKE_TEST.md](docs/SMOKE_TEST.md) for how to confirm each stage on a real one.

### OpenTelemetry, Azure Monitor and Application Insights 3.x

The automatic Application Insights subscription works through `EventCounterCollectionModule`, which
lives in `Microsoft.ApplicationInsights.EventCounterCollector`. That package stopped at 2.23.0 and
has no 3.x. Application Insights SDK 3.x, the Azure Monitor OpenTelemetry distro
(`UseAzureMonitor()`) and plain OpenTelemetry collect meters instead, and none of them collect
EventCounters at all — so on those hosts the EventSource is a channel nobody is tuned to, and
nothing in the startup log or the portal would have told you. AI Classic 2.x retires on
31 March 2027, which puts a date on it.

So every counter is published twice on V12 and V13: to the EventSource, and to a
`System.Diagnostics.Metrics` meter named `Optimizely-Performance` — the same name, and the same
counter names on it. Subscribe with one line:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("Optimizely-Performance"));
```

or, on the Azure Monitor distro:

```csharp
builder.Services.AddOpenTelemetry()
    .UseAzureMonitor()
    .WithMetrics(metrics => metrics.AddMeter("Optimizely-Performance"));
```

Both paths run at once and neither is a fallback for the other, because neither reaches every host:
`dotnet-counters` and the DataDog tracer find the EventSource without being told, and .NET Framework
has no meters at all. A site on classic Application Insights that adds nothing keeps working exactly
as before.

Every instrument is a histogram, including the counters that read as gauges. An EventCounter *is* a
histogram — it accumulates observations over an interval and reports count, mean, min and max — so
this is the faithful translation rather than a reinterpretation, and it is what keeps the two paths
reporting the same numbers. It is also a straight upgrade: a histogram keeps buckets where an
EventCounter kept four aggregates, so percentiles are available on this path and not on the other.
For a counter published once per interval, such as a rate or the uptime, the mean is that value
exactly and the chart is identical.

Two things are deliberately absent. There are **no tags**: the whole counter set is built around
EventCounters having no dimensions — one name per GC generation, one per eviction reason, one per
cascade origin — and tagging one path and not the other would give a site a different counter shape
from each backend and make every dashboard backend-specific. And there are **no units**, because the
Prometheus exporter folds a unit into the exported metric name, which would cost the name parity
this path exists to provide. Both are worth revisiting later, on both paths at once or on neither.

Turn it off with `Optimizely:Instrumentation:Meter:Enabled` set to `false` — worth doing only to
keep a single publication path while diagnosing a discrepancy between the two. On V11 the setting is
ignored in silence, so one settings file still serves all three majors.

### SQL connection pool counters on V11

Eight of the twelve pool counters are collected with no configuration. The other four —
`NumberOfActiveConnections`, `NumberOfFreeConnections`, `SoftConnectsPerSecond` and
`SoftDisconnectsPerSecond` — are only published by ADO.NET when a trace switch says so, and they
are the four that answer *how full is the pool*. Add the switch to `web.config`:

```xml
<system.diagnostics>
  <switches>
    <add name="ConnectionPoolPerformanceCounterDetail" value="4" />
  </switches>
</system.diagnostics>
```

`4` is `TraceLevel.Verbose`, the only value ADO.NET accepts here. Restart the application pool
afterwards; the counters are created during provider initialization, not on demand.

Without the switch those four do not fail — they read a constant zero, which on a chart is
indistinguishable from a completely idle pool. This package reads the same switch ADO.NET does and
collects them only when they can be believed, so nothing on the chart is a number you cannot trust.
There is no equivalent switch on V12 or V13; the EventCounter versions are always available.

### Settings

Nothing has to be configured. Every value has a default and the defaults are what the package uses
as installed, so the rest of this section is only needed when you want to change something.

Settings live under `Optimizely:Instrumentation`, using the same key paths on all three majors —
`appSettings` in `web.config` on V11, `appsettings.json` on V12 and V13 — so what a site configures
survives an upgrade. A template listing every key at its default is packed with the package and
written to `App_Data\Optimizely.Performance.Counters\` on install; copy across the lines you are
changing and leave the rest out.

```json
{
  "Optimizely": {
    "Instrumentation": {
      "Enabled": true,
      "Probes": {
        "ThreadPool":        { "Enabled": true, "SampleIntervalSeconds": 5 },
        "GarbageCollection": { "Enabled": true, "SlowPauseThresholdMilliseconds": 200 },
        "Contention":        { "Enabled": true, "BurstCaptureEnabled": true },
        "CacheLock":         { "Enabled": true, "QueueDepthThreshold": 10 }
      },
      "Cache":   { "Cascade": { "Enabled": true, "LargeRemovalThreshold": 1000 } },
      "Logging": { "Enabled": true },
      "Http":    { "Enabled": true, "LogSharedCacheConflicts": true },
      "Meter":   { "Enabled": true },
      "Deployment": {
        "Enabled": true,
        "StatePath": "",
        "InventoryMode": "OnChange",
        "HeartbeatMinutes": 15,
        "StampTelemetry": true
      }
    }
  }
}
```

Five things worth knowing before you need them.

**The master switch is there for an incident.** `Optimizely:Instrumentation:Enabled` set to `false`
decorates nothing, starts no probe and registers no counter — each module logs one line and returns.
Ruling this package out as the cause of something should be a setting, not a deployment.

**The switches are per feature, not per counter.** A probe, the cascade instrumentation or the log
write rate goes off as a unit. That is deliberate: a chart that is empty because somebody
deprovisioned one counter looks exactly like a chart that is empty because the counter is broken.

**`Meter` is the one switch that is not about a measurement.** Everything else here decides whether
a counter is produced; `Meter:Enabled` decides how the counters that are produced leave the process.
It defaults to on for the reason given in
[OpenTelemetry, Azure Monitor and Application Insights 3.x](#opentelemetry-azure-monitor-and-application-insights-3x):
the failure it prevents is a site whose counters reach nobody and whose log does not say so.

**`Deployment` is not about a counter either.** Everything else in the tree measures the running
site; `Deployment` records what was deployed, so a change in one can be attributed to a change in
the other. It is described in [What was deployed](#what-was-deployed).

**A misspelled key is not silently ignored.** Anything unrecognised under `Optimizely:Instrumentation`
is listed in the startup log, because a typo and a correctly configured counter on a healthy site are
otherwise indistinguishable from the outside.

The binding is done by hand rather than through `Microsoft.Extensions.Configuration.Binder`. The
binder cannot be used on net472 — a V11 site has no `IConfiguration` to bind from — and it ignores
unrecognised keys, which is the one behaviour a settings file most needs to be told about.

---

## What is instrumented

Seventy-five counters, from six decorated services, four probes, the host's logging pipeline, the
responses the site sends and the process itself. This is the whole list — the package instruments a
finite, hand-maintained set of seams rather than crawling for things to wrap.

One thing under this heading is not a counter. [What was deployed](#what-was-deployed) records the
assemblies in the bin folder, so that a move in any of the seventy-five can be attributed to a
change in the build rather than to a change in traffic.

### From the decorators

| Prefix | Counters |
| --- | --- |
| `Optimizely.CMS.Content.` | `LoadTimeMs`, `LoadOperations`, `SaveTimeMs`, `SaveOperations`, `PublishTimeMs`, `PublishOperations`, `DeleteTimeMs`, `DeleteOperations`, `MoveTimeMs`, `MoveOperations`, `ItemsLoaded` |
| `Optimizely.CMS.Cache.` | `HitRate`, `MissRate`, `InvalidationsPerSecond`, `Operations`, `SynchronizedInvalidationsPerSecond`, `LocalOnlyInvalidationsPerSecond`, `RemoteInvalidationsPerSecond` |
| `Optimizely.CMS.Cache.` *(cascade — V12/V13)* | `RemovalFanOut`, `RemoteRemovalFanOut`, `InsertFanOut`, `RemovalDurationMs`, `InsertTtlSeconds`, `EvictionsExpired`, `EvictionsCapacity`, `EvictionsReplaced`, `EvictionsTokenExpired` |
| `Optimizely.CMS.Events.` | `EventsPerSecond`, `RemoteEventsPerSecond`, `RemoteEventFailuresPerSecond`, `AverageRemoteEventDeliveryTimeMs` *(V13 only)* |
| `Optimizely.Commerce.Orders.` | `SaveTimeMs`, `SaveOperations`, `LoadTimeMs`, `LoadOperations`, `CreateTimeMs`, `CreateOperations`, `DeleteTimeMs`, `DeleteOperations`, `CartLineItemCount`, `CartTotal`, `CartsLoaded` |

Every timed operation emits a `TimeMs` and an `Operations` counter as a pair, so a duration can
always be read against the call count that produced it.

The nine cascade counters are the ones worth dwelling on. Optimizely hangs cache entries off master
keys, so removing one entry walks the dependency graph and discards the subtree beneath it while
the caller sees a single `Remove`. `RemovalFanOut` measures that amplification directly: read the
counter's own aggregates, where count is removals asked for, mean is the fan-out and max is the
worst single cascade in the interval. `RemoteRemovalFanOut` splits out the cascades another node in
the cluster caused, because profiling *this* instance will never explain those. `InsertFanOut` is
the least obvious of the three — inserting a key removes the existing entry first, so writing a key
that others depend on evicts them too. The four `Evictions*` counters carry the reason as separate
names rather than as a dimension, for the reason given at the end of this section.

`LocalOnlyInvalidationsPerSecond` is the one to look at on a load-balanced site. Optimizely's cache
has two removals that look identical from the call site: `Remove` broadcasts the invalidation to the
rest of the cluster, and `RemoveLocal` deliberately does not. Every `RemoveLocal` on shared content
therefore drops the entry here and leaves the same entry stale on every other node until something
else happens to evict it. There is a legitimate use — discarding an entry only the local node got
wrong — and it is rare; the common case is code that reached for the wrong overload, or a
single-instance habit that survived the move behind a load balancer. Nothing reports it today: it is
invisible in development, where there is no second node for the entry to be stale on, it logs
nothing, and in production it presents as content that is right on one server and wrong on another,
which is the class of report that gets closed as unreproducible. A flat zero here is a reading in
its own right, which is why the counter is published even when nothing removed anything.

The other two are the context that makes it legible. `SynchronizedInvalidationsPerSecond` is the
`Remove` route, so the two together give the share of this node's invalidations the cluster never
heard about, and `RemoteInvalidationsPerSecond` is work this node was *told* to do by another — the
rate-side companion to `RemoteRemovalFanOut`, and unlike that counter it works on V11 too.
`InvalidationsPerSecond` is unchanged and still the total, so existing charts mean what they always
did. It is not the sum of the three on V11 and V12: the obsolete `Clear()` is a bulk invalidation
with no route of its own and is counted in the total only.

The decorated services:

| Service | Package | Versions |
| --- | --- | --- |
| `IContentLoader` | CMS | V11, V12, V13 |
| `IContentRepository` | CMS | V11, V12, V13 |
| `ISynchronizedObjectInstanceCache` | CMS | V11, V12, V13 |
| `IMemoryCache` | CMS | V12, V13 — the layer the cascade counters are measured at. V11 caches through `HttpRuntime.Cache`, which has no such layer beneath the object cache |
| `IEventPublisher` | CMS | V13 only — V11 and V12 raise events through the static `Event` class, which has no seam to decorate |
| `IOrderRepository` | Commerce | V11, V12, V13 |

### From the probes

A probe measures something no counter source publishes. Each runs one background thread at
below-normal priority, so on a saturated machine it yields to the work it is measuring.

| Prefix | Counters | Versions |
| --- | --- | --- |
| `Optimizely.CMS.Cache.` | `LockWaitingWriters`, `LockWaitingReaders`, `LockCurrentReaders`, `LockWriteHeldPercent` | V12, V13 |
| `Optimizely.Runtime.ThreadPool.` | `QueueDelayMs`, `BusyWorkerThreads`, `BusyIoThreads`, `StarvationSamples` | V11, V12, V13 |
| `Optimizely.Runtime.GC.` | `Gen0PauseMs`, `Gen1PauseMs`, `Gen2PauseMs`, `Gen2BackgroundPauseMs`, `PauseTimePercent`, `IntervalPauseMs`, `PauseDutyCyclePercent` | V12, V13 |
| `Optimizely.Runtime.Contention.` | `ContentionsPerSecond`, `BurstContentions`, `BurstWaitP50Ms`, `BurstWaitP95Ms`, `BurstWaitMaxMs` | V12, V13 |

- **Cache lock.** Samples the queue on the `ReaderWriterLockSlim` that Optimizely's memory cache
  serialises every write behind, without ever acquiring it — four property reads, so sampling
  cannot add the contention it measures. `LockWaitingWriters` is the number that matters: writers
  exclude readers, so a queue there is the whole site waiting on cache invalidation.
  `LockWriteHeldPercent` is a duty cycle, which is what makes it comparable across sites of
  different sizes. This is the only place the package reads an Optimizely internal; see
  [Cache lock counters are missing](#cache-lock-counters-are-missing).
- **Thread pool.** Queues one work item every five seconds and times how long the pool takes to
  start it. `threadpool-queue-length` cannot substitute: a queue of ten is harmless if the pool
  drains it instantly and fatal if it is injecting one thread per second. Dedicated thread rather
  than a timer, because timer callbacks are dispatched on the thread pool and a timer-based probe
  reports numbers biased towards health at exactly the moment the pool is starved.
- **GC pause.** The four pause counters are per generation because EventCounters carry no
  dimensions and a gen 0 pause and a gen 2 pause differ by three orders of magnitude. Blocking and
  background gen 2 are separated because charted together an ordinary background collection looks
  like a stall. `IntervalPauseMs` is exact rather than sampled — a delta of a cumulative runtime
  total — and is the one to alert on; the per-generation durations are for reading the shape. It
  needs .NET 8 or later.
- **Contention.** `ContentionsPerSecond` runs always and is free, being a delta of a number the
  runtime already keeps. Cross the trigger threshold and the probe opens a short capture burst that
  records actual wait durations and reports their percentiles — the thing a contention *count* can
  never tell you. Bursts are bounded three ways: a duration, a cooldown, and a cap per rolling hour.

### From the logging pipeline

| Prefix | Counters | Versions |
| --- | --- | --- |
| `Optimizely.Runtime.Logging.` | `WritesPerSecond`, `WarningsPerSecond`, `ErrorsPerSecond` | V11, V12, V13 |

Neither a decorator nor a probe. Logging is a genuine capacity problem rather than a diagnostic
afterthought — a site that starts writing a warning per request is spending real time formatting,
serialising and flushing it, and the symptom presents as slow requests with nothing in the request
telemetry to explain them. The write rate is the counter that names that, and the two severity
counters are what turn "we are logging a lot" into "we are logging a lot of *errors*".

The hook is the one place every write already passes through, which differs by major:

- **V12 and V13** register an `ILoggerProvider` named `OptimizelyLogWriteRate`.
  `EPiServer.Logging.LogManager` forwards to `Microsoft.Extensions.Logging`, so a provider sees
  everything every real sink sees. It counts at the host's default minimum level; widen it with
  `"Logging": { "OptimizelyLogWriteRate": { "LogLevel": { "Default": "Debug" } } }`, which changes
  what the counter sees and not what any sink writes. The counting logger always reports
  `IsEnabled` as false, so registering it cannot make a `logger.IsEnabled(...)` guard anywhere in
  the site start building messages nobody writes, and it never calls the formatter — rendering is
  most of what a log write costs and doing it twice to count it would be self-defeating.
- **V11** adds an appender to the root logger of every configured log4net repository. log4net
  resolves appenders by walking a logger's parent chain per event, so an appender added after
  startup still sees everything that follows. log4net is reached entirely by reflection, with the
  appender built as a `DispatchProxy` over `IAppender`: log4net's assembly version tracks its
  package version, so a compile-time reference would bind to one exact identity and every site on a
  different patch would need a binding redirect. Both V11 configurations are covered — a standalone
  `EPiServerLog.config` and an inline `<log4net>` section in `web.config` — because the sink
  enumerates all repositories rather than assuming the default one. Two things it does not see: a
  logger with `additivity="false"`, which never reaches root, and a repository created after
  initialization.

Counts accumulate on the writing thread with an interlocked increment and are published once a
minute, the same accumulate-and-flush pattern the cache counters use, because a counter write per
log write would make this the expensive thing on the page.

### From the response pipeline

| Prefix | Counters | Versions |
| --- | --- | --- |
| `Optimizely.Runtime.Http.` | `ResponsesPerSecond`, `PublicPercent`, `PrivatePercent`, `RevalidatePercent`, `NoStorePercent`, `NoDirectivePercent`, `FreshnessSeconds`, `ValidatorPercent`, `SharedCacheConflictPercent` | V11, V12, V13 |

Every other counter in this package measures work the process did. This one measures work it told
somebody else not to make it do again — and that is the only part of a site's performance whose
effects are invisible from inside it. When the instruction is missing the request simply comes back,
looking exactly like ordinary traffic, so the origin sees load it has no reason to question and the
evidence that it was avoidable lives in a CDN report or a browser nobody is watching.

Each response is classified from its `Cache-Control` and `Expires` headers into exactly one of five
buckets, so the five shares always add to a hundred and read as one stacked chart:

| Bucket | What the response said | What it costs you |
| --- | --- | --- |
| `PublicPercent` | Reusable, not marked `private` | Nothing — a CDN can serve it |
| `PrivatePercent` | Reusable by the browser only | Repeat visitors are free; new ones are not |
| `RevalidatePercent` | `no-cache`, `max-age=0` or `must-revalidate` | A round trip per use, though possibly a 304 |
| `NoStorePercent` | `no-store` | The full response, every time, by instruction |
| `NoDirectivePercent` | Nothing at all | The full response, every time, by accident |

The last two look the same on a traffic graph and are completely different findings.
`NoStorePercent` is a decision; `NoDirectivePercent` is the absence of one, and on most sites it is
the largest of the five. Shares rather than counts, because "four hundred responses set no cache
headers" depends on how busy the minute was and "sixty percent of what this site sends says nothing
about caching" is the same statement at any traffic level.

The three that are not shares of the bucket split:

- **`FreshnessSeconds`** is published per response rather than averaged here, so the counter's own
  aggregation keeps the maximum as well as the mean — one route with a ten-minute lifetime among a
  thousand five-second ones is the interesting reading, and a mean would hide it. Only responses
  that are actually reusable report one; a revalidating response is fresh for zero seconds by
  definition, and publishing those zeros would drag the mean towards a number no response stated.
  `max-age` wins over `s-maxage`, because the question is what the *client* may do.
- **`ValidatorPercent`** is the share carrying an `ETag` or a `Last-Modified`, counted across every
  bucket. Against `RevalidatePercent` it is the difference between a 304 and the whole body going
  out again.
- **`SharedCacheConflictPercent`** is the finding worth acting on: responses marked shared-cacheable
  that also set a cookie. No shared cache will store one, so the cache headers on that route are
  buying nothing at all while looking, in every other counter, as though they are. Because a counter
  cannot carry a route, each one is also written to the log naming the path — without the query
  string, deliberately — capped at five a minute so that one misconfigured route serving steadily
  cannot fill the log with the same sentence.

The measurement happens as the headers go on the wire, not on the way back out through the pipeline,
because the headers are not final until then. On V12 and V13 that is `Response.OnStarting`, from
middleware registered *first* so that its callback runs *last* — `OnStarting` callbacks run in
reverse registration order — and therefore sees the finished header set. On V11 it is
`AddOnSendingHeaders`, which matters more there than it sounds: System.Web does not materialise
`Cache-Control` from `Response.Cache` until it generates the headers, so a page that configured its
caching through `HttpCachePolicy` — on V11, most of them — would otherwise be counted as having said
nothing at all.

Nothing has to be added to `web.config` on V11: the module registers itself through
`PreApplicationStartMethod`, so installing the package installs it. Integrated pipeline only, because
classic mode has no managed response header collection to read.

Every response is counted, including redirects, 304s and errors. What a site says about caching its
failures is part of how cacheable it is, and excluding them would quietly change the denominator
every share is computed against.

### From the process

| Prefix | Counters | Versions |
| --- | --- | --- |
| `Optimizely.Runtime.Process.` | `UptimeSeconds` | V11, V12, V13 |

The cheapest counter here and the one that most often changes what another chart means. Almost
everything above is a rate or an average over a warm process, and for the first minutes after a
recycle every one of them describes a cold one instead and reads as a regression: the hit rate is
low because the cache is empty, the GC series is short because the heap is small, the thread pool
is still injecting threads. Overlay this and the question answers itself — a hit rate that collapses
at the moment uptime drops to zero is a restart, not a cache problem, and telling those two apart
otherwise means leaving the chart and going to read the platform's own logs.

A sawtooth here is a finding on its own. Unexplained recycles are ordinary on Optimizely sites —
memory limits, idle timeouts, an overlapped deployment, a crash the host restarted quietly — and the
interval between the teeth is the number to take to whoever owns the hosting. It is a gauge, not a
rate: nothing resets it, because the reset *is* the process.

It has no switch of its own. `Optimizely:Instrumentation:Enabled` turns it off with everything else,
but there is no per-feature flag, because the case for switching a measurement off — it perturbs
what it measures, or it reads something the host might not give up — does not arise for one
subtraction a minute against a timestamp taken at startup. Where the host declines to report the
process start time, it falls back to when instrumentation started, which undercounts by the site's
startup duration and by nothing else.

### Counters from elsewhere

The Application Insights registration also subscribes to the twelve `Microsoft.Data.SqlClient`
connection pool EventCounters — pooled and non-pooled connection counts, active and free
connections, hard and soft connect/disconnect rates, pool and pool-group counts, stasis and
reclaimed connections. Nothing needs enabling: the counters are created the first time something
enables the event source, which is exactly what registering them does.

These are not this package's counters and it does not claim them; they are here because they are
the next question after a cache miss storm, and having to go and find them by hand is the reason
nobody does. On V11 the same measurements are Windows performance counters under
`.NET Data Provider for SqlServer` rather than EventCounters, and four of them need a switch — see
[SQL connection pool counters on V11](#sql-connection-pool-counters-on-v11).

Counter names carry no dimensions. An overload that could be distinguished — a load by GUID versus
by `ContentReference` — reports under the same base name, because Application Insights matches
EventCounter names exactly and a name with a dimension baked into it is a name nobody subscribed to.
Where a dimension genuinely mattered it is baked into separate *names* instead: one counter per GC
generation, one per eviction reason, one per cascade origin.

### What was deployed

Everything above measures the running site. This measures what is *in* it, because the first
question after a counter moves is almost always whether anything shipped — and the honest answer on
most sites is a Slack search for a release announcement.

At startup and every `HeartbeatMinutes` the bin folder is read and reduced to a **fingerprint**: a
16-character hash over every assembly's name and identity. Identity is the assembly's **MVID**, the
GUID the compiler writes into the module to name that exact compilation, so the fingerprint has a
property a version number does not — a rebuild of the same version reads as a change, and a version
bump with no rebuild does not. Files the reader cannot parse as managed assemblies, native
dependencies among them, fall back to file version and length.

Four events go to Application Insights, all named `OptiCounters.*` so one query finds them:

| Event | One row per | Carries |
|---|---|---|
| `OptiCounters.DeploymentManifest` | process start, then per heartbeat | `Fingerprint`, `AssemblyCount`, `Reason` (`Startup`/`Heartbeat`), `Transition` (`Baseline`/`Changed`/`Unchanged`), `StateStore`, and when there is a previous manifest `PreviousFingerprint`, `Added`, `Changed`, `Removed` |
| `OptiCounters.AssemblyInventory` | assembly, on change and on the slow re-emit | `Fingerprint`, `Assembly`, `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `Mvid`, `SizeBytes`, `Managed` |
| `OptiCounters.AssemblyChanged` | assembly that differs from the last recorded manifest | `Change` (`Added`/`Changed`/`Removed`), `Assembly`, `Version`, `PreviousVersion`, `Mvid`, `PreviousMvid`, and `SameVersionDifferentBinary` when the version matched and the bits did not |
| `OptiCounters.FleetDivergence` | heartbeat at which a peer is visibly running something else | `PeerCount`, `DivergentPeerCount`, `PeerFingerprints` |

Every event except the inventory rows also carries the reporting instance — `InstanceId`,
`RoleName`, `MachineName`, `Runtime`, `OptimizelyVersion`, and `Slot`, `ContainerImage` and
`ProcessStartUtc` where the host offers them. That is what makes a per-instance diff a `summarize`
rather than a guess, and it is written explicitly rather than left to `cloud_RoleInstance`, which is
populated differently by the ASP.NET Core SDK, by the App Service codeless agent and by whatever
initializer a site added of its own — and is absent on V11. The inventory rows leave it out on
purpose: they are keyed by fingerprint, and a fingerprint means the same thing on every instance, so
repeating the instance on a few hundred rows would only make them look instance-specific.

**The fingerprint is also stamped onto everything else the site sends.** `StampTelemetry` adds a
`DeploymentFingerprint` custom dimension to every request, dependency, exception and trace, so
grouping any existing chart by it turns *did the release cause this* into a query. It does **not**
touch `application_Version`: on DXP that column already carries the site's own release — it moves
across a deployment, and the in-process SDK and the codeless agent agree on it — so filling it in
"when empty" would leave one column holding a release number on one host and a fingerprint on
another. The host keeps that column and this adds its own; the manifest event goes through the
host's `TelemetryClient`, so one row carries both.

**Change detection is best-effort, and the queries do not depend on it.** Reporting *what changed*
needs the previous manifest to still be readable, so the last one is written to disk: `StatePath` if
set, otherwise the first of a series of candidates that proves writable, with the winner and whether
it is expected to survive a deployment reported on `StateStore` on every row. On a DXP container
none of them survive — the container is replaced wholesale on release, `WEBSITES_ENABLE_APP_SERVICE_STORAGE`
is off, and `/home` is container-local — so every start there is a `Baseline` and the
`AssemblyChanged` events never fire. That is why the fingerprint is on *every* manifest row rather
than only on the ones that changed: the diff is done in the query, needs no state, and works
identically on a host where the state store happens to be durable.

`FleetDivergence` is the partial-swap detector, and it only works where the state store is shared —
several instances writing manifests into one directory, each keyed by `InstanceId`. Where that
holds, an instance that can see a peer on a different fingerprint says so and logs it. Where it does
not, the same condition is a query: one `summarize` over the manifest events, below.

Without Application Insights the events go to the log instead — the inventory at `Debug`, the rest
at `Information` — because a V11 site logging through log4net still deploys, and *what is in the bin
folder on this server* is a question the log can answer perfectly well. Only the querying needs
Azure.

---

## How it works

```
  InstrumentedContentLoader
  InstrumentedContentRepository
  InstrumentedSynchronizedObjectInstanceCache
  InstrumentedMemoryCache -> CacheCascadeRecorder  (V12/V13)
  InstrumentedEventPublisher        (V13)       ──→  IMetricTracker
  InstrumentedOrderRepository                              |
                                                           |
  CacheLockProbe                    (V12/V13)              v
  ThreadPoolQueueDelayProbe                       CompositeMetricTracker
  GcPauseProbe                      (V12/V13)              |
  ContentionProbe                   (V12/V13)     +--------+------------------+
                                                  v                           v
                              EventCounterMetricTracker          MeterMetricTracker (V12/V13)
                                                  |                           |
                                                  v                           v
                    OptimizelyPerformanceEventSource            Meter ("Optimizely-Performance")
                             ("Optimizely-Performance")                       |
                                                  |                           v
                    +-----------------------------+------+       OpenTelemetry / Azure Monitor
                    v                             v      v        / AI SDK 3.x  (.AddMeter)
     AI EventCounterCollectionModule           DataDog  dotnet-counters
             (AI SDK 2.x only)
                    ^
                    |
     Microsoft.Data.SqlClient.EventSource  (subscribed, not published, by this package)
```

An `[InitializableModule] : IConfigurableModule` in each package runs during container
construction, before any module initializes. It logs the detected and expected Optimizely version
and throws if they disagree, detects the telemetry systems present, registers the `IMetricTracker`
— `EventCounterMetricTracker` on V11, and on V12 and V13 a `CompositeMetricTracker` over that and
`MeterMetricTracker` — and wraps the services above using
`context.Services.Intercept<T>()`. The Commerce module does the same and is idempotent about the
shared parts, so installing both packages registers telemetry once.

The probes start in `Initialize` rather than `ConfigureContainer`, because they are not services:
nothing resolves them, they own their own threads, and they need a built container to get a tracker
out of. The three runtime probes are process-wide and idempotent behind `RuntimeProbes`, so a site
with both packages installed runs one set rather than two — two thread pool probes do not measure
the pool twice as well, they measure it slightly worse and cost twice as much. The cache lock probe
is owned by the CMS module alone, because it reads an Optimizely internal and has no business
running in a Commerce-only host.

The `IMemoryCache` decoration is deferred to `ConfigurationComplete`. `IMemoryCache` belongs to the
host, not to us, and nothing guarantees it has been registered by the time this module configures;
`ConfigurationComplete` runs after everything else has registered and registration is still open
there, so it is the one point that cannot depend on module ordering. If the decoration fails it is
logged as a warning and the site starts anyway — the rate counters are unaffected and only the
cascade counters go missing.

Nothing in the library listens to its own EventSource, and it does not republish the counters that
`Optimizely.Performance.DotNetCounters` collects. Collection is entirely the host's business.

**Overhead.** Timing is allocation-free: `OperationTimer` is a `readonly struct` over a single
`Stopwatch.GetTimestamp()`, and the decorators forward through explicit try/catch blocks rather
than a `Measure<T>(name, () => ...)` helper, because that helper allocates a closure per call. When
no collector is attached, `EventSource.IsEnabled()` short-circuits and the cost is a branch; the
meter path costs a second call that returns on a disabled instrument, which is why it can be left on
by default. The
cache and event decorators sit on paths that fire thousands of times a second, so those accumulate
into interlocked fields and flush on a 60-second timer instead of writing per call. What your
collector *reads* is on its own schedule: `EventCounterCollectionModule` polls at 60 seconds by
default, `dotnet-counters` at one.

The probes are budgeted the same way. A cache lock sample is four property reads and never acquires
the lock, so sampling cannot itself add contention; the reflection that finds the lock runs once, at
startup. A thread pool sample is one queued work item every five seconds — twelve a minute, enough
for a meaningful max and standard deviation without the probe becoming load itself. A GC sample
reads state the runtime already recorded and does not provoke a collection. The contention rate is
a delta of a number the runtime already keeps; only a triggered burst subscribes to per-contention
events, and it is bounded by duration, cooldown, a cap per hour and a cap on retained samples. All
probe logging is rate-limited, so sustained trouble cannot flood the log at the moment the site can
least afford it.

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
[INFO] Registered 83 EventCounters with Application Insights, from 2 event sources
[INFO] The same counters are also published to the 'Optimizely-Performance' meter. Collect them
       from OpenTelemetry, Azure Monitor or Application Insights SDK 3.x with
       .WithMetrics(m => m.AddMeter("Optimizely-Performance")), which is the only path that works
       once EventCounter collection is gone.
[INFO] Registered IMetricTracker: EventCounterMetricTracker + MeterMetricTracker
       (meter 'Optimizely-Performance')
[INFO] Registering CMS performance counter decorators
[INFO] Registered decorators: IContentLoader, IContentRepository, ISynchronizedObjectInstanceCache.
       IEventPublisher is V13-only and was not registered.
[INFO] Cache dependency cascade instrumentation installed on IMemoryCache.
[INFO] Optimizely CMS Performance Counters configured successfully
[INFO] Reading cache lock contention from
       'EPiServer.Framework.Cache.Internal.MemoryObjectInstanceCache.CacheLock'.
[INFO] Log write rate counters are active. Writes are counted at the host's default minimum
       level, which is what an unconfigured logging provider sees.
[INFO] Outbound response cacheability counters are active. Every response this process sends is
       classified from its Cache-Control and Expires headers as it goes out; the nine
       Optimizely.Runtime.Http counters report the mix once a minute.
[INFO] Optimizely CMS Performance Counters initialized
```

Seventy-five of those counters are this package's and twelve are SqlClient's, which is what the two
event sources are. The probes are quiet on a healthy start: the cache lock probe logs the one line
above naming where it found the lock, and the others log only when they cannot start or when a
sample crosses a threshold.

If the cache lock cannot be found, that line is replaced by one beginning
`Cache lock contention will not be reported`, giving the precise reason. Nothing else is affected —
see [Cache lock counters are missing](#cache-lock-counters-are-missing).

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
none of them knows what an `IContentLoader` is. This package is on the other side of that line: it
creates the seam and then publishes it to a meter OpenTelemetry can collect, which is why
`.AddMeter("Optimizely-Performance")` works where auto-instrumentation cannot.

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

Two honest caveats for V11. **Application Insights registration of this package's own counters is
not implemented there.** `TelemetryStartup.Configure` needs an `IServiceCollection` and V11
configures services through `IServiceConfigurationProvider`. Detection runs and is logged; the
counters are published to the EventSource and are readable with PerfView or your own
`EventListener`, but nothing subscribes Application Insights to them automatically. The SQL
connection pool counters are the exception, for the reason given
[above](#making-the-counters-visible). See the gaps below.

**And three of the four probes are V12/V13 only.** The thread pool probe runs everywhere. The GC
pause and contention probes need runtime APIs that do not exist on .NET Framework, and the cache
lock probe has nothing to find: V11 caches through `HttpRuntime.Cache`, which has no such lock. On
V11 the ground those three cover is the ground Windows performance counters already cover
adequately — which is the DotNetCounters package's job, not this one's.

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
// Invalidations that never left this node. On a load-balanced site every one of these is a
// window where the other instances are serving content this one has already discarded, so
// the share is the number to alert on rather than the rate - a busy site and a quiet one
// with the same bug read completely differently in absolute terms
customMetrics
| where name in (
    "Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond",
    "Optimizely.CMS.Cache.SynchronizedInvalidationsPerSecond")
| summarize
    localOnly = avgif(value, name endswith "LocalOnlyInvalidationsPerSecond"),
    synchronized = avgif(value, name endswith "SynchronizedInvalidationsPerSecond")
    by bin(timestamp, 5m)
| extend localOnlyShare = 100.0 * localOnly / (localOnly + synchronized)
| project timestamp, localOnlyShare
| render timechart
```

```kusto
// Restarts, and what they explain. Uptime dropping to zero at the moment a rate counter
// changes shape means the rate did not change - the process did
customMetrics
| where name in ("Optimizely.Runtime.Process.UptimeSeconds", "Optimizely.CMS.Cache.HitRate")
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

```kusto
// Cascade amplification. count is removals asked for, avg is how many entries each one
// actually discarded, max is the worst single cascade in the interval
customMetrics
| where name in (
    "Optimizely.CMS.Cache.RemovalFanOut",
    "Optimizely.CMS.Cache.RemoteRemovalFanOut",
    "Optimizely.CMS.Cache.InsertFanOut")
| summarize removals = sum(valueCount), meanFanOut = avg(value), worst = max(valueMax)
    by name, bin(timestamp, 5m)
| render timechart
```

```kusto
// GC pauses. valueMax is the number that matters - an average pause is meaningless when
// a single 900ms gen 2 is what the user actually felt
customMetrics
| where name startswith "Optimizely.Runtime.GC." and name endswith "PauseMs"
| summarize max(valueMax) by name, bin(timestamp, 1m)
| render timechart
```

```kusto
// The whole cascade on one chart: mass invalidation -> cache lock queue -> miss storm ->
// pool exhaustion -> requests waiting on threads. One bucket, so it reads as a sequence
customMetrics
| where name in (
    "Optimizely.CMS.Cache.RemovalFanOut",
    "Optimizely.CMS.Cache.LockWaitingWriters",
    "Optimizely.CMS.Cache.MissRate",
    "number-of-free-connections",
    "Optimizely.Runtime.ThreadPool.QueueDelayMs")
| summarize max(valueMax) by name, bin(timestamp, 1m)
| render timechart
```

```kusto
// Thread pool delay, where the max matters far more than the average: starvation is
// bursty and a one-minute mean hides it completely
customMetrics
| where name == "Optimizely.Runtime.ThreadPool.QueueDelayMs"
| summarize p50 = percentile(value, 50), p95 = percentile(value, 95), worst = max(valueMax)
    by bin(timestamp, 1m)
| render timechart
```

```kusto
// Log write rate, with the two severity series against the total. The interesting
// incidents are the ones where they diverge: a flat total with a climbing error rate is a
// fault the site is absorbing quietly, a climbing total with a flat error rate is a debug
// level someone left switched on
customMetrics
| where name startswith "Optimizely.Runtime.Logging."
| summarize avg(value) by name, bin(timestamp, 1m)
| render timechart
```

```kusto
// The cacheability mix as one stacked chart. The five shares are mutually exclusive and
// sum to a hundred, so this reads as a composition rather than five unrelated lines - and
// the band worth watching is NoDirective, which is the absence of a decision rather than
// a decision
customMetrics
| where name in (
    "Optimizely.Runtime.Http.PublicPercent",
    "Optimizely.Runtime.Http.PrivatePercent",
    "Optimizely.Runtime.Http.RevalidatePercent",
    "Optimizely.Runtime.Http.NoStorePercent",
    "Optimizely.Runtime.Http.NoDirectivePercent")
| summarize avg(value) by name, bin(timestamp, 5m)
| render areachart
```

```kusto
// What a release did to cacheability. A deployment that quietly drops a caching attribute
// moves these two and nothing else, and the origin just gets busier - so it is worth
// looking at deliberately rather than waiting for it to present as load
customMetrics
| where name in (
    "Optimizely.Runtime.Http.PublicPercent",
    "Optimizely.Runtime.Http.FreshnessSeconds")
| summarize avg(value) by name, bin(timestamp, 1h)
| render timechart
```

```kusto
// Shared-cache conflicts: responses marked public that also set a cookie. Nonzero here
// means cache headers that buy nothing. The log line names the route; this says how much
// of the site's traffic it is
customMetrics
| where name == "Optimizely.Runtime.Http.SharedCacheConflictPercent"
| summarize avg(value), max(valueMax) by bin(timestamp, 5m)
| render timechart
```

### Correlating a counter with the requests it affected

Everything above says *what* happened and *when*. Closing the loop to *who it happened to*
takes one more step, and the shape of that step is dictated by something worth stating
plainly rather than working around: **the counters carry no `operation_Id`.**

They cannot. An EventCounter is a process-level aggregate, and Application Insights'
`EventCounterCollectionModule` reads it on its own collection timer — not inside any
request, not on a request's thread, and not within its activity scope. The same is true of
the probes at the point of measurement: a probe samples from a background thread, so even
the moment of measurement has no request context to inherit. There is nothing to join on,
and a query that appeared to join on one would be joining on whatever ambient activity the
collection timer happened to pick up, which is worse than not joining at all.

What replaces it is a join on time *and* `cloud_RoleInstance`. The instance is the part
people leave out and the part that makes it work — on a multi-instance site a counter
spike is usually one instance, and averaging it across the others is exactly how a real
spike gets buried under healthy neighbours.

```kusto
// Step 1. Find when, and just as importantly where. Set the threshold from your own
// baseline; 100ms is a placeholder, not a recommendation
let counter = "Optimizely.Runtime.ThreadPool.QueueDelayMs";
let spikeThreshold = 100;
customMetrics
| where name == counter
| summarize worst = max(valueMax) by cloud_RoleInstance, bucket = bin(timestamp, 1m)
| where worst > spikeThreshold
| order by worst desc
```

```kusto
// Step 2. The requests that instance served inside those windows. A one-minute bucket
// because that is the counters' own publication interval - a finer bucket does not buy
// resolution the source data has
let counter = "Optimizely.Runtime.ThreadPool.QueueDelayMs";
let spikeThreshold = 100;
let window = 1m;
let spikes =
    customMetrics
    | where name == counter
    | summarize worst = max(valueMax) by cloud_RoleInstance, bucket = bin(timestamp, window)
    | where worst > spikeThreshold;
requests
| extend bucket = bin(timestamp, window)
| join kind=inner spikes on cloud_RoleInstance, bucket
| summarize
    requests = count(),
    failed = countif(success == false),
    p95 = percentile(duration, 95)
    by operation_Name, cloud_RoleInstance, bucket, worst
| order by p95 desc
```

That is a coincidence-in-a-window argument, not proof of causation, and it is worth
reading it as one. What makes it persuasive is the comparison rather than the number: run
the same query against quiet windows on the same instance, and an operation whose p95 only
moves inside the spike windows is a much stronger candidate than one that is slow
throughout.

The logging counters are the exception, and the one place the loop genuinely closes to a
request. Counters carry no `operation_Id`, but the `traces` those counters are counting
do — Application Insights stamps them from the ambient request. So the counter tells you
which minute went wrong, and `traces` tells you which operations produced the writes:

```kusto
// The write rate counter says WHEN the site got noisy. This says which operations were
// doing the writing, and at what severity
let window = 1m;
let noisy =
    customMetrics
    | where name == "Optimizely.Runtime.Logging.WritesPerSecond"
    | summarize rate = avg(value) by cloud_RoleInstance, bucket = bin(timestamp, window)
    | where rate > 50;   // from your own baseline
traces
| extend bucket = bin(timestamp, window)
| join kind=inner noisy on cloud_RoleInstance, bucket
| join kind=leftouter (requests | project operation_Id, operation_Name) on operation_Id
| summarize writes = count(), affectedOperations = dcount(operation_Id) by operation_Name, severityLevel
| order by writes desc
```

Writes with no `operation_Name` are not a defect in the query. They are the site logging
from somewhere that is not a request — startup, a scheduled job, a background thread — and
on a site whose write rate has just doubled, that bucket is often where the answer is.

### What is deployed, and when it changed

These read the `OptiCounters.*` events described in [What was deployed](#what-was-deployed). They
work on any host, including one where nothing the site writes survives a deployment: the comparison
is done here rather than in the process.

```kusto
// What every instance is running right now. Two fingerprints in this result during a rolling
// deployment is expected; two fingerprints an hour after one finished is a partial swap
customEvents
| where name == "OptiCounters.DeploymentManifest"
| where timestamp > ago(1h)
| extend
    role = tostring(customDimensions.RoleName),
    instance = tostring(customDimensions.InstanceId),
    fingerprint = tostring(customDimensions.Fingerprint),
    assemblies = toint(customDimensions.AssemblyCount)
| summarize arg_max(timestamp, fingerprint, assemblies) by role, instance
| order by role asc, instance asc
```

```kusto
// The same thing as an alert. Give the window a margin over how long a rollout takes on your
// site - during one this is supposed to fire, and an alert that cries wolf every release is an
// alert nobody reads
customEvents
| where name == "OptiCounters.DeploymentManifest"
| where timestamp > ago(30m)
| extend
    role = tostring(customDimensions.RoleName),
    instance = tostring(customDimensions.InstanceId),
    fingerprint = tostring(customDimensions.Fingerprint)
| summarize arg_max(timestamp, fingerprint) by role, instance
| summarize builds = dcount(fingerprint), instances = count(), running = make_set(fingerprint) by role
| where builds > 1
```

```kusto
// When each build arrived and how long it was live. This is the release timeline, reconstructed
// from what the instances were actually running rather than from what a pipeline reported
customEvents
| where name == "OptiCounters.DeploymentManifest"
| extend
    role = tostring(customDimensions.RoleName),
    instance = tostring(customDimensions.InstanceId),
    fingerprint = tostring(customDimensions.Fingerprint)
| summarize
    firstSeen = min(timestamp),
    lastSeen = max(timestamp),
    instances = dcount(instance)
    by role, fingerprint
| extend live = lastSeen - firstSeen
| order by firstSeen asc
```

```kusto
// Every DLL in one build. Take the fingerprint from any of the queries above
let build = "0123456789abcdef";
customEvents
| where name == "OptiCounters.AssemblyInventory"
| where tostring(customDimensions.Fingerprint) == build
| summarize by
    assembly = tostring(customDimensions.Assembly),
    version = tostring(customDimensions.Version),
    mvid = tostring(customDimensions.Mvid)
| order by assembly asc
```

```kusto
// What changed between two builds. "Rebuilt" is the row a version comparison cannot produce:
// same version number, different bits - a hotfix rebuilt from a branch, or a package restored
// from a different feed
let before = "0123456789abcdef";
let after  = "fedcba9876543210";
let inventory = (build:string) {
    customEvents
    | where name == "OptiCounters.AssemblyInventory"
    | where tostring(customDimensions.Fingerprint) == build
    | summarize by
        assembly = tostring(customDimensions.Assembly),
        version = tostring(customDimensions.Version),
        mvid = tostring(customDimensions.Mvid)
};
inventory(before)
| join kind=fullouter inventory(after) on assembly
| extend change = case(
    isempty(assembly),  "Added",
    isempty(assembly1), "Removed",
    mvid == mvid1,      "Unchanged",
    version == version1, "Rebuilt",
                        "Changed")
| where change != "Unchanged"
| project change, assembly = coalesce(assembly, assembly1), from = version, to = version1
| order by change asc, assembly asc
```

```kusto
// Where the state store is durable - a V11 site, or anywhere with a mounted share - the site
// reports the diff itself and this needs no fingerprints typed in
customEvents
| where name == "OptiCounters.AssemblyChanged"
| extend
    instance = tostring(customDimensions.InstanceId),
    change = tostring(customDimensions.Change),
    assembly = tostring(customDimensions.Assembly),
    from = tostring(customDimensions.PreviousVersion),
    to = tostring(customDimensions.Version),
    rebuiltOnly = tostring(customDimensions.SameVersionDifferentBinary) == "true"
| project timestamp, instance, change, assembly, from, to, rebuiltOnly
| order by timestamp desc
```

And the one the rest of it is for. Every request, dependency, exception and trace carries the
fingerprint of the build that served it, so the before-and-after is a `summarize` — no time ranges
guessed, no deployment timestamp to look up, and a rolling deployment compares correctly while it is
still half done:

```kusto
// Did the release do this. Two rows per operation means both builds served traffic in the
// window; if only one appears, widen the range until the previous build is in it
requests
| where timestamp > ago(24h)
| extend build = tostring(customDimensions.DeploymentFingerprint)
| where isnotempty(build)
| summarize
    requests = count(),
    failureRatePercent = round(100.0 * countif(success == false) / count(), 2),
    p95 = percentile(duration, 95)
    by operation_Name, build
| order by operation_Name asc, p95 desc
```

```kusto
// The same question asked of exceptions, which is usually the faster answer
exceptions
| where timestamp > ago(24h)
| extend build = tostring(customDimensions.DeploymentFingerprint)
| where isnotempty(build)
| summarize count() by build, type, bin(timestamp, 1h)
| render timechart
```

A caveat worth stating: an instance that started before this package was installed, or before the
first scan finished, sends telemetry with no `DeploymentFingerprint` at all. `isnotempty(build)`
above drops those rows rather than lumping them together as a build of their own, which they are
not.

---

## Troubleshooting

**The startup log shows nothing from either module.** The module never ran. Confirm the package is
in the site's `bin`, not merely referenced by a project that is not deployed.

**The site fails to start with a version mismatch.** The target framework and the CMS major
disagree — see the support matrix above. This is the intended failure, not a bug.

**Counters appear in `dotnet-counters` but not in Application Insights.** Collection is fine and
export is not. Check the connection string, and turn adaptive sampling off while you verify: a
counter that is being sampled away is indistinguishable from one that is not being collected.

**Nothing arrives in OpenTelemetry or Azure Monitor.** The meter has to be subscribed to
explicitly — `.WithMetrics(m => m.AddMeter("Optimizely-Performance"))`. Nothing is auto-wired on
that path, by design: an exporter's counter budget is the host's business. Check the startup log for
the line naming the meter; if it says meter publication is switched off, that is
`Optimizely:Instrumentation:Meter:Enabled`. On V11 there is no meter at all.

**A counter is missing entirely.** EventCounters that have never been written are not listed at
all. Drive the matching traffic first. `CartLineItemCount` and `CartTotal` in particular need a
populated cart to be saved or loaded.

**Startup log is clean but no counters move.** The decorators registered but are not on the path
your traffic takes. Resolve `IContentLoader` from the site's container and confirm the concrete
type is `InstrumentedContentLoader`.

### Cache lock counters are missing

The four `Optimizely.CMS.Cache.Lock*` counters read a private static field on Optimizely's
`MemoryObjectInstanceCache`. That field is not part of any public API, so these are expected to
lapse across some upgrades. When it happens the probe logs one `Information` line at startup
beginning `Cache lock contention will not be reported`, naming exactly what it could not find, and
then stops. Nothing else is affected and cache behaviour is unchanged.

Common reasons, in order of likelihood:

- **The site is V11.** There is no equivalent lock; V11 caches through `HttpRuntime.Cache`.
- **A CMS version whose cache no longer serialises on a single static `ReaderWriterLockSlim`.** The
  probe declines rather than reporting a number off some arbitrary other lock. It matches on field
  *type* rather than name, which is what let one implementation survive Optimizely moving the type
  from `EPiServer.Framework` to `EPiServer.Cache` and renaming the field between 12 and 13 — but
  shape matching only goes so far.
- **Several candidate fields and no recognisable name.** Same outcome, deliberately.

### Cascade counters are missing but the rate counters work

The nine `FanOut`/`Evictions*`/`Ttl` counters are measured one layer below the object cache, at
`IMemoryCache`. If that decoration failed, the startup log carries a warning beginning
`Could not decorate IMemoryCache`. Hit rate, invalidation rate and the lock counters are unaffected.
On V11 these counters are absent by design — there is no `IMemoryCache` beneath the object cache.

### Response cacheability counters are missing

`Optimizely.Runtime.Http.ResponsesPerSecond` is published every interval, zero included, so it is
the one to look for first. If even that is absent, nothing is measuring.

- **The site is on V11 in classic pipeline mode.** There is no managed response header collection
  there, so the module subscribes to nothing rather than throwing once per request. Integrated mode
  is required.
- **`Optimizely:Instrumentation:Http:Enabled` is false**, or the master switch is.
- **On V12 or V13, something replaced the request pipeline wholesale.** The middleware is added
  through an `IStartupFilter`, which a host that builds its own `IApplicationBuilder` outside the
  generic host can bypass.

If the rate counter moves but the five shares are absent, that is not a fault: the shares are
suppressed for an interval with no traffic in it, because a chart claiming nothing was cacheable
during the quiet hours is worse than a gap. The gap is unambiguous next to a response rate of zero.

### SQL pool counters read a constant zero on V11

`NumberOfActiveConnections`, `NumberOfFreeConnections` and the soft connect/disconnect rates need
the `ConnectionPoolPerformanceCounterDetail` switch. This package omits them when it is off rather
than charting zeroes, so seeing nothing at all means exactly that — add the switch and restart the
application pool. See [SQL connection pool counters on V11](#sql-connection-pool-counters-on-v11).

If *all* the SQL counters are missing on V11, the instance name is the likely cause. ADO.NET names
its performance counter instance after the entry assembly and process ID by a private algorithm;
this package reproduces it, and a mismatch produces a counter that reads as absent rather than
erroring. Compare the path in the startup log against what Performance Monitor shows under
`.NET Data Provider for SqlServer`.

### Every deployment reports as a baseline and no `AssemblyChanged` events arrive

Expected on a container host, and the manifest events say so: `StateStore` on every row reports
which directory won and whether what is written there is expected to outlive a deployment. On DXP
nothing does — the container is replaced on release — so there is no previous manifest to diff
against and `Transition` is always `Baseline`. The comparison is done in the query instead; see
[What is deployed, and when it changed](#what-is-deployed-and-when-it-changed). Setting `StatePath`
to a mounted share is the only way to get the in-process diff back, and it buys convenience rather
than information.

`FleetDivergence` has the same dependency and one more: the state directory has to be *shared*
between instances for one to see another. Where it is not, the divergence query above answers the
same question from the manifest events.

### Requests have no `DeploymentFingerprint` dimension

- **`Optimizely:Instrumentation:Deployment:StampTelemetry` is false**, or `Deployment:Enabled` is,
  or the master switch is.
- **The site has no Application Insights.** The events still go to the log; there is nothing to
  stamp.
- **The rows predate the first scan.** The dimension is attached at startup and filled in when the
  first scan completes a moment later, so the earliest requests of a process have no value. They are
  a handful of rows, and dropping them with `isnotempty(build)` is more honest than treating them as
  a build.
- **The site is on Application Insights SDK 3.x.** The initializer is attached by reflection to
  `TelemetryConfiguration.TelemetryInitializers`, which 3.x does not have — the same 3.x gap that
  affects counter collection. See
  [OpenTelemetry, Azure Monitor and Application Insights 3.x](#opentelemetry-azure-monitor-and-application-insights-3x).

---

## Current gaps

Stated plainly, because a monitoring package that overstates its coverage is worse than one that
does not exist:

- **Application Insights auto-registration on V11**, for this package's own counters. Described
  above. The SQL connection pool counters do register there.
- **The meter is V12 and V13 only, and is not auto-subscribed.** .NET Framework has no
  `System.Diagnostics.Metrics`, so V11 has the EventSource and nothing else. On V12 and V13 the
  meter is published but a collector still has to name it; there is no equivalent of the automatic
  Application Insights wiring, because adding metrics to somebody else's exporter without being
  asked is not this package's call.
- **The meter carries no tags and no units.** Deliberate, and explained above: name parity with the
  EventSource is worth more than either while both paths are live.
- **Counters cannot be disabled one at a time.** The settings described above switch features —
  a probe, the cascade instrumentation, the log write rate, or the whole package — rather than
  individual counters.
- **Three of the four probes are V12/V13 only.** GC pause and contention need .NET 6 or later;
  `IntervalPauseMs` needs .NET 8. The cache lock probe has nothing to find on V11.
- **Cacheability is measured for the site as a whole, not per route.** The counters carry no
  dimensions, so the mix is a single figure for everything the process sends; the only thing that
  names a route is the shared-cache conflict log line. Finding *which* pages are in the
  `NoDirective` band still means going and looking.
- **Responses the site never sends are not counted.** Anything served from the IIS kernel cache, a
  reverse proxy or a CDN edge never reaches the module or the middleware — which is the correct
  behaviour for a counter measuring what this process emits, but means the mix describes origin
  responses rather than what a browser ultimately received.
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
| [tests/](tests/) | 250 tests, run on net472, net6.0, net8.0, net9.0 and net10.0 — container registration, published counter names, that the emitted set matches the subscribed set, probe lifecycle and emission, cache-control classification across the whole decision tree, and graceful degradation of the cache lock reflection against every shape a future CMS could present |
| [examples/](examples/) | Per-version settings, and `verify-package-install`, which installs the built packages from a local feed and asserts they bind and detect the right major. Builds on all six target frameworks; runs on whichever runtimes are installed |
| [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md) | Eight-stage verification on a real site |
| [ARCHITECTURE.md](ARCHITECTURE.md), [TELEMETRY_ARCHITECTURE.md](TELEMETRY_ARCHITECTURE.md) | Design notes |

---

## Related packages

- [Optimizely.Performance.DotNetCounters](https://github.com/jeff-fischer-optimizely/Optimizely.Performance.DotNetCounters)
  — .NET runtime and ASP.NET counters. Required, and installed transitively.

## License

Apache-2.0
