# Optimizely CMS 12 (.NET 6 - .NET 9)

```
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce   # Commerce 14 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration in `Program.cs`. There is nothing to add beyond the package reference.

## What gets instrumented

`IContentLoader`, `IContentRepository` and `ISynchronizedObjectInstanceCache`, plus `IOrderRepository`
if the Commerce package is installed. `IEventPublisher` is a V13 addition, so the four
`Optimizely.CMS.Events.*` counters stay at zero here.

## Application Insights

If the site already references `Microsoft.ApplicationInsights.AspNetCore` (or
`Microsoft.ApplicationInsights.WorkerService`), the module detects it during container configuration
and subscribes all 30 counters to `EventCounterCollectionModule` itself. You do not call
`ConfigureTelemetryModule`.

[appsettings.json](appsettings.json) is the connection string side of that. Confirm it took effect
from the startup log:

```
Registered 30 Optimizely EventCounters with Application Insights
```

If Application Insights is not installed, the counters still go to the `Optimizely-Performance`
EventSource and the log says so, with the `dotnet-counters` command to read them.

## .NET 6 and .NET 7

Both runtimes are out of Microsoft support. They are still in the matrix, but net8.0 or net9.0 is
the better target for a site being set up now.

Nothing about the Commerce package differs across the V12 band: all four target frameworks build
against Commerce 14.5.0, the oldest release with a `net6.0` assembly, and that is a minimum rather
than a pin. A net8.0 site running Commerce 14.46 installs the same package.
