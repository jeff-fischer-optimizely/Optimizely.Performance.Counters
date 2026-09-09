# Smoke test

Confirming, on a real site, that the packages install, that the initialization module fires, that
counters reach `dotnet-counters` and Application Insights Live Metrics, and that a counter can be
followed back to the requests it affected.

Stage 1 needs nothing. Stages 2 to 8 need a licensed Optimizely site, and Stage 8 additionally needs
Application Insights to have been collecting for long enough to have a spike worth querying.

---

## Stage 1 - the packages install (no licence)

```
dotnet pack -c Release -o build/nupkg
dotnet run --project examples/verify-package-install -f net10.0
```

Expected:

```
Target framework      : net10.0
EventSource name      : Optimizely-Performance
Compiled for          : V13
Registered counters   : 71
EventSource state     : healthy
CMS module            : Optimizely.Performance.Counters.CMS loaded
Commerce module       : Optimizely.Performance.Counters.Commerce loaded
Detected at runtime   : V13

PASS - the packages install and bind on this target framework.
```

Repeat with `-f net472`, `net6.0`, `net7.0`, `net8.0`, `net9.0`. `Compiled for` should read V11 on
net472, V12 on net6.0 through net9.0, and V13 on net10.0, and should always match
`Detected at runtime`. The process exits non-zero on failure.

> **NU1608 on restore is expected.** Commerce names `EPiServer.CMS.Core` as a range and its sibling
> `EPiServer.CMS.AspNetCore` pins that range's floor exactly, so on the V12 band NuGet settles
> `AspNetCore` on 12.4.0 - which requires `CMS.Core (= 12.4.0)` - and then `Optimizely.Performance.
> DotNetCounters` lifts `CMS.Core` to its own 12.10.0 minimum, past the pin. Four lines, two
> distinct warnings reported once per restore pass.
>
> It does not appear on a real site, only on a project like this one that references nothing but
> these packages: a site references `CMS.AspNetCore` itself, which lifts it to match. Removing it
> here means lowering the CMS minimums in DotNetCounters, or raising the Commerce floor to a release
> whose own CMS floor is already above 12.10.0 - which would cost reach to silence a warning. See
> the note in `Directory.Build.props`.

---

## Testing at the ceiling

The packages are built against the **oldest** Optimizely each target framework supports, so that a
site which has drifted behind can still install them - see the note in `Directory.Build.props`. The
default test run therefore proves the floor and says nothing about current Optimizely, so run the
other end too:

```
dotnet test tests/Optimizely.Performance.Counters.Tests \
  -p:OptimizelyV11Version=11.21.5 \
  -p:OptimizelyV12Version=12.24.1 \
  -p:OptimizelyV13Version=13.1.1 \
  -p:CommerceV13Version=13.37.2 \
  -p:CommerceV14Version=14.45.5 \
  -p:CommerceV15Version=15.1.0
```

Both ends must be green before a release. 14.45.5 rather than 14.46.0 because 14.46 ships no net6.0
or net7.0 assembly and one property covers the whole V12 band. To cover 14.46 as well:

```
dotnet test tests/Optimizely.Performance.Counters.Tests \
  -p:TargetFrameworks=net8.0 -p:OptimizelyV12Version=12.24.1 -p:CommerceV14Version=14.46.0
```

and again with `net9.0`. `-p:TargetFrameworks=` rather than `-f`: `-f` filters which built target
framework gets run, but restore still walks every target framework of the referenced projects, so
the Commerce project's net6.0 target would try to resolve a Commerce release that has no net6.0
assembly and fail with NU1202 before anything is built.

A failure at the ceiling but not the floor means an Optimizely interface changed shape underneath a
decorator. A failure at the floor but not the ceiling means something in the source has started
using an API newer than the floor, and either the call or the floor has to move.

---

## Stage 2 - the module fires

Install into the site and set the two module loggers to `Information` - see the `appsettings.json`
or `web.config.snippet.xml` in the matching [examples](../examples/) folder. Start the site and read
the startup log.

Six lines matter, in this order:

1. **Version detection.** `Detected Version` must equal `Expected Version`. A mismatch throws out of
   `ConfigureContainer` and takes the site down at startup, so if the site is running at all this
   one passed.
2. **Telemetry detection.** Either
   `Registered 83 EventCounters with Application Insights, from 2 event sources`, or the "no
   telemetry system detected" line with the `dotnet-counters` command in it. Seventy-five of those are
   this package's and twelve are SqlClient's.

   On V11 expect two different lines instead: `Registered 8 SQL connection pool performance counters
   with Application Insights (detail counters off)` - or `12` and `enabled` if the
   `ConnectionPoolPerformanceCounterDetail` switch is set - followed by the line explaining that V11
   has no `IServiceCollection` and giving the EventSource name to read directly. That split is a
   known gap, not a failure.
3. **`Registered IMetricTracker: EventCounterMetricTracker`.**
4. **The decorator list.** V11 and V12 name three; V13 also names `IEventPublisher`.
5. **`Cache dependency cascade instrumentation installed on IMemoryCache.`** V12 and V13 only. A
   warning beginning `Could not decorate IMemoryCache` here means the nine cascade counters will
   read zero; everything else is unaffected and the site starts either way.
6. **`Reading cache lock contention from '...MemoryObjectInstanceCache.CacheLock'.`** V12 and V13
   only, and logged from `Initialize` rather than `ConfigureContainer`, so it comes after
   `configured successfully`. A line beginning `Cache lock contention will not be reported` instead
   means the four lock counters will be absent - expected on V11, and an ordinary outcome of an
   upgrade elsewhere.

If none of these appear, the module never ran and nothing downstream will work. Check that the
package is actually in the site's `bin`, not merely referenced.

The probes log nothing else on a healthy start. Their remaining output is rate-limited warnings
when a sample crosses a threshold, which is what you want to see under load in Stage 3 and not
before.

---

## Stage 3 - counters in `dotnet-counters` (V12 / V13)

`dotnet-counters` attaches to .NET Core only. On V11, use PerfView or your own `EventListener`
against the `Optimizely-Performance` source instead.

```
dotnet tool install --global dotnet-counters
dotnet-counters ps
dotnet-counters monitor --process-id <pid> Optimizely-Performance
```

Then drive some traffic - load a page, edit and publish content, add something to a cart.

Expected: the counters below appear and move. Counters are polled on an interval, so allow a few
seconds.

All seventy-five are listed from the moment the EventSource is constructed, whether or not any traffic
has reached them yet, so a counter sitting at zero means the decorator that owns it has not been
hit - not that anything is broken. They are created up front deliberately: an EventCounter is polled
through a group that arms its timer when the collector attaches, and the group does not exist until
the first counter does, so counters created lazily on first use were invisible to any collector that
attached before them. That is the normal order on a site, which is why `dotnet-counters` and
Application Insights used to show nothing at all here.

| Prefix | Counters |
| --- | --- |
| `Optimizely.CMS.Content.` | `LoadTimeMs`, `LoadOperations`, `SaveTimeMs`, `SaveOperations`, `PublishTimeMs`, `PublishOperations`, `DeleteTimeMs`, `DeleteOperations`, `MoveTimeMs`, `MoveOperations`, `ItemsLoaded` |
| `Optimizely.CMS.Cache.` | `HitRate`, `MissRate`, `InvalidationsPerSecond`, `Operations`, `SynchronizedInvalidationsPerSecond`, `LocalOnlyInvalidationsPerSecond`, `RemoteInvalidationsPerSecond` |
| `Optimizely.CMS.Cache.` *(cascade)* | `RemovalFanOut`, `RemoteRemovalFanOut`, `InsertFanOut`, `RemovalDurationMs`, `InsertTtlSeconds`, `EvictionsExpired`, `EvictionsCapacity`, `EvictionsReplaced`, `EvictionsTokenExpired` |
| `Optimizely.CMS.Cache.` *(lock probe)* | `LockWaitingWriters`, `LockWaitingReaders`, `LockCurrentReaders`, `LockWriteHeldPercent` |
| `Optimizely.CMS.Events.` | `EventsPerSecond`, `RemoteEventsPerSecond`, `RemoteEventFailuresPerSecond`, `AverageRemoteEventDeliveryTimeMs` *(V13 only)* |
| `Optimizely.Runtime.ThreadPool.` | `QueueDelayMs`, `BusyWorkerThreads`, `BusyIoThreads`, `StarvationSamples` |
| `Optimizely.Runtime.GC.` | `Gen0PauseMs`, `Gen1PauseMs`, `Gen2PauseMs`, `Gen2BackgroundPauseMs`, `PauseTimePercent`, `IntervalPauseMs`, `PauseDutyCyclePercent` |
| `Optimizely.Runtime.Contention.` | `ContentionsPerSecond`, `BurstContentions`, `BurstWaitP50Ms`, `BurstWaitP95Ms`, `BurstWaitMaxMs` |
| `Optimizely.Runtime.Logging.` | `WritesPerSecond`, `WarningsPerSecond`, `ErrorsPerSecond` |
| `Optimizely.Runtime.Http.` | `ResponsesPerSecond`, `PublicPercent`, `PrivatePercent`, `RevalidatePercent`, `NoStorePercent`, `NoDirectivePercent`, `FreshnessSeconds`, `ValidatorPercent`, `SharedCacheConflictPercent` |
| `Optimizely.Runtime.Process.` | `UptimeSeconds` |
| `Optimizely.Commerce.Orders.` | `SaveTimeMs`, `SaveOperations`, `LoadTimeMs`, `LoadOperations`, `CreateTimeMs`, `CreateOperations`, `DeleteTimeMs`, `DeleteOperations`, `CartLineItemCount`, `CartTotal`, `CartsLoaded` |

Seventy-five in total. `Optimizely.CMS.Events.*` needs V13; the `Optimizely.Commerce.Orders.*` counters
need the Commerce package.

The probe counters behave differently from the decorator counters and are worth checking separately,
because they move without any traffic at all:

- **`Optimizely.Runtime.ThreadPool.*`** should be populated within about five seconds of startup and
  keep moving on an idle site. `QueueDelayMs` under a millisecond is healthy. If these are flat zero
  rather than small, the probe did not start - look for a warning naming it in the startup log.
- **`Optimizely.Runtime.GC.*`** need a collection to have happened. On an idle site that can take a
  while; `GC.Collect()` from a diagnostic endpoint, or just driving traffic, is the quick way.
  `IntervalPauseMs` requires .NET 8 or later and stays zero below it.
- **`Optimizely.Runtime.Contention.ContentionsPerSecond`** moves on any site under load. The four
  `Burst*` counters only move when the rate crosses the trigger threshold, so on a healthy site they
  are expected to be zero. To see them, contend a lock deliberately.
- **`Optimizely.Runtime.Logging.*`** move on any site that logs. `WritesPerSecond` flat at zero on a
  site you know is writing to a log means the sink did not attach: on V12 and V13 look for the
  `OptimizelyLogWriteRate` provider being registered, and on V11 for a startup line saying log4net
  was not loaded. Note that V12 and V13 count at the host's default minimum level, so raising it -
  `"Logging": { "OptimizelyLogWriteRate": { "LogLevel": { "Default": "Debug" } } }` - widens what the
  counter sees without changing what any real sink writes.
- **`Optimizely.Runtime.Http.*`** move on any request at all, including the one that loaded the
  page you are looking at. `ResponsesPerSecond` is published every interval, zero included, so it
  is the one to check first; if it is flat zero on a site you are actively browsing, nothing is
  measuring. The five shares are *not* published for an interval with no traffic in it, so a gap
  in those next to a zero response rate is correct rather than broken. `FreshnessSeconds` only
  appears once some response has stated a lifetime.
  To exercise `SharedCacheConflictPercent` deliberately, serve one response that sets both
  `Cache-Control: public, max-age=600` and a cookie; the counter should move and one
  `Information` line naming the path should appear in the log.
- **`Optimizely.Runtime.Process.UptimeSeconds`** moves on every site, needs nothing, and is the
  one counter here that cannot legitimately be missing. If it is absent, the runtime probes did not
  start at all - check the startup log rather than anything to do with this counter. It should read
  roughly the age of the worker process; a value close to zero on a site that has been up for hours
  means the host declined to report the process start time and the fallback took over, which is
  worth knowing but not a fault.
- **`Optimizely.CMS.Cache.LocalOnlyInvalidationsPerSecond`** and its two siblings are published
  every interval, zero included, so all three should be present immediately. A flat zero on
  `LocalOnlyInvalidationsPerSecond` is the good reading and the common one - it means nothing in the
  site is calling `RemoveLocal` on shared content. To confirm the split works rather than merely
  reads zero, publish a page and watch `SynchronizedInvalidationsPerSecond` move; on a multi-node
  site `RemoteInvalidationsPerSecond` should move on the *other* instances at the same time.
- **`Optimizely.CMS.Cache.Lock*`** need the lock to have been found - see Stage 2, line 6. Publish
  content to make `LockWaitingWriters` move; on an idle site all four are legitimately zero.
- **The nine cascade counters** need an invalidation with dependents. Publishing a page that others
  reference is the reliable way; `RemovalFanOut` should then report a mean above 1.

Nothing at all here, with Stage 2 passing, means the decorators registered but are not on the path
the traffic took. Resolve `IContentLoader` from the site's container and check its concrete type is
`InstrumentedContentLoader`.

The twelve `Microsoft.Data.SqlClient` pool counters are not on this source. To see them:

```
dotnet-counters monitor --process-id <pid> Microsoft.Data.SqlClient.EventSource
```

---

## Stage 4 - counters in Application Insights Live Metrics (V12 / V13)

Not available on V11 for this package's own counters - see the gap noted in Stage 2. The SQL
connection pool counters *are* available on V11 and have their own stage below. Needs the 2.x
Application Insights SDK; see the note on 3.x under Known gaps.

The counters are registered with `EventCounterCollectionModule` by reflection, and everything up to
the point Azure gets involved is covered by `ApplicationInsightsRegistrationTests` - so if this stage
fails while those tests pass, the problem is the connection string or sampling rather than the
registration.

1. Azure portal, Application Insights resource, **Live Metrics**.
2. Restart the site and drive the same traffic.
3. Under **Sample telemetry**, the counters arrive as custom metrics named exactly as above.

Live Metrics is roughly a one-second stream; the metrics blade lags by two to three minutes, so
check Live Metrics first.

To query them once they have landed:

```kusto
customMetrics
| where name startswith "Optimizely."
| summarize avg(value), count() by name, bin(timestamp, 1m)
| order by name asc
```

Present in `dotnet-counters` but absent here means the collection side is fine and the export side
is not: confirm the connection string is set, and that adaptive sampling is off while verifying -
a sampled-away counter is indistinguishable from an uncollected one.

---

## Stage 5 - SQL connection pool counters (V11)

The one thing that does reach Application Insights automatically on V11, because there the pool
counters are Windows performance counters rather than EventCounters and are collected through a
`PerformanceCollectorModule` this package builds and initializes against
`TelemetryConfiguration.Active` - no `IServiceCollection` required.

1. Confirm the category exists on the machine at all. In PowerShell:

   ```powershell
   Get-Counter -ListSet '.NET Data Provider for SqlServer' | Select-Object -ExpandProperty Counter
   ```

   Fourteen paths should come back. If the category is absent, ADO.NET has never initialized its
   counters on this machine and nothing downstream can work.

2. Read the startup log line from Stage 2. It reports how many counters were registered and whether
   the detail counters are on: `8` and `off` without the switch, `12` and `enabled` with it.

3. Drive traffic that opens connections, then look in Application Insights under `customMetrics` for
   the reported names - `SQL Pooled Connections`, `SQL Hard Connects/Sec` and the rest. They are
   charted under those names rather than under the raw counter paths.

4. To get the four utilisation counters, add the switch and restart the application pool:

   ```xml
   <system.diagnostics>
     <switches>
       <add name="ConnectionPoolPerformanceCounterDetail" value="4" />
     </switches>
   </system.diagnostics>
   ```

   `4` is `TraceLevel.Verbose`, the only value ADO.NET accepts. Without it those four read a constant
   zero rather than failing, which is why this package omits them instead of charting a number it
   cannot vouch for.

Nothing here at all, with the category present, points at the instance name. ADO.NET names its
counter instance after the entry assembly and process ID by a private algorithm that this package
reproduces; a mismatch reads as an absent counter rather than an error. Compare the path in the log
against what Performance Monitor shows under the category.

`SqlClientWindowsCounterTests` checks the requested set against the live category on every net472
test run, so a drift in the counter names themselves fails the build rather than this stage.

---

## Stage 6 - response cacheability on V11

The one measurement in the package with no unit test behind it, so this stage is not optional on a
V11 release. `HttpResponse.Headers` throws outside a running integrated-mode pipeline and System.Web
offers nothing to stand in for one, so the only place the V11 half of this feature can be exercised
is a site.

1. **Confirm the module reached the pipeline.** It is added through `PreApplicationStartMethod`
   rather than a `web.config` entry, so there is nothing to check in configuration - check the
   pipeline itself. Enable failed request tracing for a request, or read `HttpContext.Current.
   ApplicationInstance.Modules` from a diagnostic page: `HttpCacheabilityModule` should be listed.
   If it is not, the registration was swallowed - which it is on purpose, because an exception there
   stops the application from starting - and the likely cause is `Microsoft.Web.Infrastructure`
   missing from `bin`.

2. **Confirm the application pool is in integrated mode.** Classic mode has no managed response
   header collection, and the module subscribes to nothing at all rather than throwing once per
   request. This is the most likely reason for a silent zero on an otherwise healthy V11 site.

3. **Read the counters.** `dotnet-counters` does not attach to .NET Framework, so use PerfView or an
   `EventListener` against `Optimizely-Performance`, as in Stage 3. Browse a few pages and confirm
   `Optimizely.Runtime.Http.ResponsesPerSecond` moves.

4. **Confirm `HttpCachePolicy` is being seen.** This is the V11-specific correctness check and the
   reason the module measures at `AddOnSendingHeaders` rather than at `EndRequest`: System.Web does
   not materialise `Cache-Control` from `Response.Cache` until it generates the headers. Serve a page
   that sets its caching through the policy object rather than the header -

   ```csharp
   Response.Cache.SetCacheability(HttpCacheability.Public);
   Response.Cache.SetMaxAge(TimeSpan.FromMinutes(10));
   ```

   - and confirm it lands in `PublicPercent` with a `FreshnessSeconds` of 600. If it lands in
   `NoDirectivePercent` instead, the measurement is running too early and every page on the site that
   configures caching this way is being miscounted.

---

## Stage 7 - both packages side by side

On a Commerce site, install both and repeat Stages 2 to 4. Specifically confirm:

- Both modules log their own registration, and the Commerce one does not re-register the CMS
  services. Telemetry registration is idempotent and safe to run twice: the second pass skips the
  counters the first already asked Application Insights for, so nothing is collected or billed
  twice. `ApplicationInsightsRegistrationTests` covers this without a site.
- `Optimizely.CMS.*` and `Optimizely.Commerce.*` counters both appear, from one EventSource.
- **The runtime probes run once, not twice.** Both modules call `RuntimeProbes.Start` from
  `Initialize`; whichever runs first wins and the other is a no-op. Two thread pool probes would not
  measure the pool twice as well, they would measure it slightly worse. The give-away if this ever
  broke is `Optimizely.Runtime.ThreadPool.QueueDelayMs` reporting roughly twice the sample count.
- **The cache lock probe runs only from the CMS module**, so a Commerce-only host logs nothing about
  it at all - not even the "will not be reported" line.
- **Each response is classified once, not twice.** Both modules register the middleware, through
  `TryAddEnumerable` against one implementation type, and both start the recorder, through the same
  process-wide monitor. The give-away if this ever broke is peculiar rather than obvious:
  `Optimizely.Runtime.Http.ResponsesPerSecond` would read double while the five shares, being
  shares, would still read correctly.

---

## Stage 8 - from a counter back to the requests (V12 / V13)

The stages above end at *the counter arrived*. This one ends at *the counter was useful*, which is
a different claim and the one an operator actually needs. It is worth running once on a site you
are about to rely on, because everything it depends on can be individually true and still not line
up.

The queries live in the **Correlating a counter with the requests it affected** section of
[README.md](../README.md#correlating-a-counter-with-the-requests-it-affected) rather than here, so
there is one copy to keep right. This stage checks the preconditions they need.

1. **Confirm the counters carry an instance.** The join is on time *and* `cloud_RoleInstance`,
   because on a multi-instance site a spike is usually one instance and averaging it across the
   others buries it.

   ```kusto
   customMetrics
   | where name startswith "Optimizely."
   | summarize instances = dcount(cloud_RoleInstance), any(cloud_RoleInstance)
   ```

   An empty or single literal instance name on a site you know is scaled out means the join will
   silently over-match, pulling in requests other instances served.

2. **Confirm requests and counters agree about the clock.** Pick a minute with traffic and check
   both tables report it:

   ```kusto
   union
       (customMetrics | where name startswith "Optimizely." | extend t = "counter"),
       (requests | extend t = "request")
   | summarize count() by t, cloud_RoleInstance, bin(timestamp, 1m)
   | order by timestamp desc
   ```

   Counter buckets and request buckets should interleave. Counters arriving in bursts every few
   minutes rather than every minute means the collection interval was changed, and a one-minute
   join window is then too narrow.

3. **Turn adaptive sampling off before drawing conclusions.** Sampled-away requests do not make the
   correlation wrong, they make it *understated* - the spike window looks quieter than it was, which
   is the direction that talks you out of a real finding.

4. **Run the two-step query from the README** against a window you have already provoked - the
   thread pool or cache lock exercises in Stage 3 will do. An operation whose p95 moves only inside
   the spike windows is the result you are looking for.

5. **Run it against the logging counters too**, which are the one family where the loop closes
   properly. Counters carry no `operation_Id`, but the `traces` they are counting do, so the join
   there names operations rather than inferring them from a time window. If that query returns rows
   and the time-window one does not, the correlation machinery is fine and the spike simply had no
   requests in it - which is itself an answer, and usually means a scheduled job.

**What this stage cannot establish.** Causation. A counter is a process-level aggregate collected on
its own timer, outside any request and outside any activity scope, so there is nothing to join on
but time and instance. Read a result as a strong candidate, and confirm it by comparing against
quiet windows on the same instance.

---

## Known gaps

- **Application Insights on V11, for this package's own counters.** `TelemetryStartup.Configure`
  needs an `IServiceCollection`, and V11 supplies `IServiceConfigurationProvider`. Detection runs and
  is logged; the counters are published to the EventSource but nothing subscribes to them. The SQL
  connection pool counters are collected on V11 - see Stage 5 - because that path goes through
  `TelemetryConfiguration.Active` rather than the container.
- **Application Insights 3.x.** Only the 2.x SDK is supported. 3.0 re-based the SDK on
  OpenTelemetry and removed `EventCounterCollectionModule`, `ConfigureTelemetryModule` and the
  telemetry-module concept the registration is built on, so on a site running 3.x nothing is
  subscribed and the "no telemetry system detected" path does not fire either - `TelemetryClient`
  still resolves, so detection reports Application Insights as present. Collecting EventCounters
  under 3.x means OpenTelemetry's own EventCounters instrumentation, which is a feature rather than
  a version bump.
- **Counters cannot be turned off one at a time.** Everything under `Optimizely:Instrumentation`
  is configurable - the settings template the package installs lists every key at its default -
  but the switches are per feature, not per counter: a probe, the
  cascade instrumentation or the log write rate goes off as a unit, and the master switch takes the
  whole package off. Turning off a single counter would mean a registry a site could edit, and a
  chart that is empty because somebody deprovisioned it looks exactly like one that is empty because
  it is broken.
- **Three of the four probes are V12/V13 only.** GC pause and contention need runtime APIs that
  .NET Framework does not have, and the cache lock probe has nothing to find on V11.
  `Optimizely.Runtime.GC.IntervalPauseMs` additionally needs .NET 8 or later.
- **`CartLineItemCount` and `CartTotal`** need a populated cart, so they only move once a real
  Commerce cart is saved or loaded.
- **The `Burst*` contention counters and the cache lock counters are zero on a healthy site.**
  That is correct behaviour, but it makes them hard to smoke test - see the notes in Stage 3 for
  how to provoke each one.
- **The V11 response cacheability module has no unit test.** `HttpResponse.Headers` throws outside a
  running integrated-mode pipeline, and there is no System.Web equivalent of `DefaultHttpContext` to
  hand it, so what the tests can reach is the registration contract and the classifier either side
  of it - not the header read between them. Stage 6 is the coverage.
- **The cacheability mix is not broken down by route.** Counters carry no dimensions, so the shares
  describe everything the process sends. Only the shared-cache conflict names a path, and only in
  the log.
