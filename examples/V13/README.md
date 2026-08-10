# Optimizely CMS 13 (.NET 10)

```
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce   # Commerce 15 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration in `Program.cs`. There is nothing to add beyond the package reference.

## What gets instrumented

Everything V12 instruments - `IContentLoader`, `IContentRepository`,
`ISynchronizedObjectInstanceCache`, and `IOrderRepository` with the Commerce package - plus
`IEventPublisher`, which is new in V13. This is the only major where the four
`Optimizely.CMS.Events.*` counters produce values.

V13 also moved the `Intercept<T>` extension into `EPiServer.DependencyInjection`. That is handled
inside the package; it changes nothing for a consumer.

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
