# Optimizely CMS 11 (.NET Framework 4.7.2)

```
Install-Package Optimizely.Performance.Counters.CMS
Install-Package Optimizely.Performance.Counters.Commerce   # Commerce 13 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration on your side. There is no configuration file for this package and nothing to add to
`web.config` for the counters themselves.

## What gets instrumented

`IContentLoader`, `IContentRepository` and `ISynchronizedObjectInstanceCache`, plus `IOrderRepository`
if the Commerce package is installed. `IEventPublisher` does not exist on V11 - V11 raises events
through the static `Event` class, which has no seam to decorate - so the four
`Optimizely.CMS.Events.*` counters stay at zero here.

## Reading the counters

V11 runs on the .NET Framework, so `dotnet-counters` cannot attach. Both routes below work instead.

**PerfView / a custom EventListener.** The counters go to an `EventSource` named
`Optimizely-Performance`, and on .NET Framework each metric is written as a plain event rather than
through the `EventCounter` polling machinery. Any listener that enables that source sees them
immediately, with the counter name as the first payload argument.

**Application Insights.** [web.config.snippet.xml](web.config.snippet.xml) has the instrumentation
key wiring. Note the limitation below before relying on it.

## Application Insights is not wired up on V11

`TelemetryStartup.Configure` takes an `IServiceCollection`, and V11 configures services through
`IServiceConfigurationProvider` instead. On V11 the telemetry detection still runs and is logged, but
nothing is subscribed to `EventCounterCollectionModule` - so Application Insights will not collect
these counters on a V11 site yet.

Watch the startup log to confirm which path you are on. V11 logs:

```
Application Insights detected - V11 AI registration not yet implemented, use EventSource directly
```
