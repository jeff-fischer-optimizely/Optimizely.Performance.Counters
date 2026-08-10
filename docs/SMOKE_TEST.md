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

> **NU1608 on restore is expected.** `Optimizely.Performance.DotNetCounters` 1.0.0 declares CMS
> minimums of 11.21.5 / 12.24.1 / 13.1.1, which sit above the floor Commerce names, so NuGet lifts
> `EPiServer.CMS.Core` past the exact version its sibling `EPiServer.CMS.AspNet(Core)` pins to. It is
> harmless on a site already running a current CMS, because the site's own reference lifts
> `AspNet(Core)` to match. See the note in `Directory.Build.props`.

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
seconds; a counter no decorator has hit yet will not be listed at all.

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

Not available on V11 - see the gap noted in Stage 2.

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
  services. Telemetry registration is idempotent and safe to run twice.
- `Optimizely.CMS.*` and `Optimizely.Commerce.*` counters both appear, from one EventSource.

---

## Known gaps

- **Application Insights on V11.** `TelemetryStartup.Configure` needs an `IServiceCollection`, and
  V11 supplies `IServiceConfigurationProvider`. Detection runs and is logged; nothing is subscribed.
- **No configuration system.** Counters cannot be turned off individually, or at all, yet.
- **`CartLineItemCount` and `CartTotal`** need a populated cart, so they only move once a real
  Commerce cart is saved or loaded.
