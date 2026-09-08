# Smoke test

Confirming, on a real site, that the packages install, that the initialization module fires, and
that counters reach `dotnet-counters` and Application Insights Live Metrics.

Stage 1 needs nothing. Stages 2 to 5 need a licensed Optimizely site.

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
Registered counters   : 30
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

Four lines matter, in this order:

1. **Version detection.** `Detected Version` must equal `Expected Version`. A mismatch throws out of
   `ConfigureContainer` and takes the site down at startup, so if the site is running at all this
   one passed.
2. **Telemetry detection.** Either `Registered 30 Optimizely EventCounters with Application Insights`,
   or the "no telemetry system detected" line with the `dotnet-counters` command in it. On V11,
   expect `V11 AI registration not yet implemented` instead - that is a known gap.
3. **`Registered IMetricTracker: EventCounterMetricTracker`.**
4. **The decorator list.** V11 and V12 name three; V13 also names `IEventPublisher`.

If none of these appear, the module never ran and nothing downstream will work. Check that the
package is actually in the site's `bin`, not merely referenced.

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

All thirty are listed from the moment the EventSource is constructed, whether or not any traffic has
reached them yet, so a counter sitting at zero means the decorator that owns it has not been hit -
not that anything is broken. They are created up front deliberately: an EventCounter is polled
through a group that arms its timer when the collector attaches, and the group does not exist until
the first counter does, so counters created lazily on first use were invisible to any collector that
attached before them. That is the normal order on a site, which is why `dotnet-counters` and
Application Insights used to show nothing at all here.

| Prefix | Counters |
| --- | --- |
| `Optimizely.CMS.Content.` | `LoadTimeMs`, `LoadOperations`, `SaveTimeMs`, `SaveOperations`, `PublishTimeMs`, `PublishOperations`, `DeleteTimeMs`, `DeleteOperations`, `MoveTimeMs`, `MoveOperations`, `ItemsLoaded` |
| `Optimizely.CMS.Cache.` | `HitRate`, `MissRate`, `InvalidationsPerSecond`, `Operations` |
| `Optimizely.CMS.Events.` | `EventsPerSecond`, `RemoteEventsPerSecond`, `RemoteEventFailuresPerSecond`, `AverageRemoteEventDeliveryTimeMs` *(V13 only)* |
| `Optimizely.Commerce.Orders.` | `SaveTimeMs`, `SaveOperations`, `LoadTimeMs`, `LoadOperations`, `CreateTimeMs`, `CreateOperations`, `DeleteTimeMs`, `DeleteOperations`, `CartLineItemCount`, `CartTotal`, `CartsLoaded` |

Thirty in total. `Optimizely.CMS.Events.*` needs V13; the `Optimizely.Commerce.Orders.*` counters
need the Commerce package.

Nothing at all here, with Stage 2 passing, means the decorators registered but are not on the path
the traffic took. Resolve `IContentLoader` from the site's container and check its concrete type is
`InstrumentedContentLoader`.

---

## Stage 4 - counters in Application Insights Live Metrics (V12 / V13)

Not available on V11 - see the gap noted in Stage 2. Needs the 2.x Application Insights SDK; see the
note on 3.x under Known gaps.

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

## Stage 5 - both packages side by side

On a Commerce site, install both and repeat Stages 2 to 4. Specifically confirm:

- Both modules log their own registration, and the Commerce one does not re-register the CMS
  services. Telemetry registration is idempotent and safe to run twice: the second pass skips the
  counters the first already asked Application Insights for, so nothing is collected or billed
  twice. `ApplicationInsightsRegistrationTests` covers this without a site.
- `Optimizely.CMS.*` and `Optimizely.Commerce.*` counters both appear, from one EventSource.

---

## Known gaps

- **Application Insights on V11.** `TelemetryStartup.Configure` needs an `IServiceCollection`, and
  V11 supplies `IServiceConfigurationProvider`. Detection runs and is logged; nothing is subscribed.
- **Application Insights 3.x.** Only the 2.x SDK is supported. 3.0 re-based the SDK on
  OpenTelemetry and removed `EventCounterCollectionModule`, `ConfigureTelemetryModule` and the
  telemetry-module concept the registration is built on, so on a site running 3.x nothing is
  subscribed and the "no telemetry system detected" path does not fire either - `TelemetryClient`
  still resolves, so detection reports Application Insights as present. Collecting EventCounters
  under 3.x means OpenTelemetry's own EventCounters instrumentation, which is a feature rather than
  a version bump.
- **No configuration system.** Counters cannot be turned off individually, or at all, yet.
- **`CartLineItemCount` and `CartTotal`** need a populated cart, so they only move once a real
  Commerce cart is saved or loaded.
