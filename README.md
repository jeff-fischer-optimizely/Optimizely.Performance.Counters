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

Alongside the decorators it runs a small set of **probes** — components that measure a condition
nothing publishes, rather than reading a counter somebody else maintains. Thread pool queue
*delay* instead of queue *length*, garbage collection *pause durations* instead of a percentage,
lock wait *distributions* instead of a contention count, and the depth of the queue on Optimizely's
own cache lock. It also subscribes your APM to the `Microsoft.Data.SqlClient` connection pool
counters, because a cache problem in Optimizely becomes a connection pool problem about thirty
seconds later and you want both series on one chart.

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
      "Logging": { "Enabled": true }
    }
  }
}
```

Three things worth knowing before you need them.

**The master switch is there for an incident.** `Optimizely:Instrumentation:Enabled` set to `false`
decorates nothing, starts no probe and registers no counter — each module logs one line and returns.
Ruling this package out as the cause of something should be a setting, not a deployment.

**The switches are per feature, not per counter.** A probe, the cascade instrumentation or the log
write rate goes off as a unit. That is deliberate: a chart that is empty because somebody
deprovisioned one counter looks exactly like a chart that is empty because the counter is broken.

**A misspelled key is not silently ignored.** Anything unrecognised under `Optimizely:Instrumentation`
is listed in the startup log, because a typo and a correctly configured counter on a healthy site are
otherwise indistinguishable from the outside.

The binding is done by hand rather than through `Microsoft.Extensions.Configuration.Binder`. The
binder cannot be used on net472 — a V11 site has no `IConfiguration` to bind from — and it ignores
unrecognised keys, which is the one behaviour a settings file most needs to be told about.

---

## What is instrumented

Sixty-two counters, from six decorated services, four probes and the host's logging pipeline. This
is the whole list — the package instruments a finite, hand-maintained set of seams rather than
crawling for things to wrap.

### From the decorators

| Prefix | Counters |
| --- | --- |
| `Optimizely.CMS.Content.` | `LoadTimeMs`, `LoadOperations`, `SaveTimeMs`, `SaveOperations`, `PublishTimeMs`, `PublishOperations`, `DeleteTimeMs`, `DeleteOperations`, `MoveTimeMs`, `MoveOperations`, `ItemsLoaded` |
| `Optimizely.CMS.Cache.` | `HitRate`, `MissRate`, `InvalidationsPerSecond`, `Operations` |
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
  ThreadPoolQueueDelayProbe                     EventCounterMetricTracker
  GcPauseProbe                      (V12/V13)              |
  ContentionProbe                   (V12/V13)              v
                             OptimizelyPerformanceEventSource ("Optimizely-Performance")
                                                           |
                    +--------------------------------------+--------------------+
                    v                                      v                    v
     AI EventCounterCollectionModule                    DataDog          dotnet-counters
                    ^
                    |
     Microsoft.Data.SqlClient.EventSource  (subscribed, not published, by this package)
```

An `[InitializableModule] : IConfigurableModule` in each package runs during container
construction, before any module initializes. It logs the detected and expected Optimizely version
and throws if they disagree, detects the telemetry systems present, registers
`EventCounterMetricTracker` as `IMetricTracker`, and wraps the services above using
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
no collector is attached, `EventSource.IsEnabled()` short-circuits and the cost is a branch. The
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
[INFO] Registered 74 EventCounters with Application Insights, from 2 event sources
[INFO] Registered IMetricTracker: EventCounterMetricTracker
[INFO] Registering CMS performance counter decorators
[INFO] Registered decorators: IContentLoader, IContentRepository, ISynchronizedObjectInstanceCache.
       IEventPublisher is V13-only and was not registered.
[INFO] Cache dependency cascade instrumentation installed on IMemoryCache.
[INFO] Optimizely CMS Performance Counters configured successfully
[INFO] Reading cache lock contention from
       'EPiServer.Framework.Cache.Internal.MemoryObjectInstanceCache.CacheLock'.
[INFO] Optimizely CMS Performance Counters initialized
```

Sixty-two of those counters are this package's and twelve are SqlClient's, which is what the two
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

---

## Current gaps

Stated plainly, because a monitoring package that overstates its coverage is worse than one that
does not exist:

- **Application Insights auto-registration on V11**, for this package's own counters. Described
  above. The SQL connection pool counters do register there.
- **Counters cannot be disabled one at a time.** The settings described above switch features —
  a probe, the cascade instrumentation, the log write rate, or the whole package — rather than
  individual counters.
- **Three of the four probes are V12/V13 only.** GC pause and contention need .NET 6 or later;
  `IntervalPauseMs` needs .NET 8. The cache lock probe has nothing to find on V11.
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
| [tests/](tests/) | 153 tests, run on net472, net6.0, net8.0, net9.0 and net10.0 — container registration, published counter names, that the emitted set matches the subscribed set, probe lifecycle and emission, and graceful degradation of the cache lock reflection against every shape a future CMS could present |
| [examples/](examples/) | Per-version settings, and `verify-package-install`, which installs the built packages from a local feed and asserts they bind and detect the right major. Builds on all six target frameworks; runs on whichever runtimes are installed |
| [docs/SMOKE_TEST.md](docs/SMOKE_TEST.md) | Five-stage verification on a real site |
| [ARCHITECTURE.md](ARCHITECTURE.md), [TELEMETRY_ARCHITECTURE.md](TELEMETRY_ARCHITECTURE.md) | Design notes |

---

## Related packages

- [Optimizely.Performance.DotNetCounters](https://github.com/jeff-fischer-optimizely/Optimizely.Performance.DotNetCounters)
  — .NET runtime and ASP.NET counters. Required, and installed transitively.

## License

Apache-2.0
