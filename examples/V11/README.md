# Optimizely CMS 11 (.NET Framework 4.7.2)

```
Install-Package Optimizely.Performance.Counters.CMS
Install-Package Optimizely.Performance.Counters.Commerce   # Commerce 13 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration on your side, and it runs correctly with nothing added to `web.config`. Everything it
does can be changed or switched off from `appSettings` if you need to - see [Settings](#settings)
below.

## What gets instrumented

`IContentLoader`, `IContentRepository` and `ISynchronizedObjectInstanceCache`, plus `IOrderRepository`
if the Commerce package is installed. `IEventPublisher` does not exist on V11 - V11 raises events
through the static `Event` class, which has no seam to decorate - so the four
`Optimizely.CMS.Events.*` counters stay at zero here.

`IMemoryCache` is not decorated on V11, so the nine cache cascade counters
(`Optimizely.CMS.Cache.RemovalFanOut` and its neighbours) are absent. V11 caches through
`HttpRuntime.Cache`, which has no memory cache beneath the object cache to count entries at.

One of the four probes runs here: the thread pool queue delay probe, which publishes the four
`Optimizely.Runtime.ThreadPool.*` counters. The GC pause and contention probes need runtime APIs
that .NET Framework does not have, and the cache lock probe has nothing to find for the same reason
the cascade counters are absent - so the four `Optimizely.CMS.Cache.Lock*` counters stay at zero
too. On V11 the ground those three cover is the ground Windows performance counters already cover,
which is `Optimizely.Performance.DotNetCounters`' job.

## Log write rate

The three `Optimizely.Runtime.Logging.*` counters - `WritesPerSecond`, `WarningsPerSecond` and
`ErrorsPerSecond` - work here exactly as they do on V12 and V13. After the three paragraphs above
that is worth saying explicitly: none of V11's missing seams apply to them, because logging is a
property of the host process rather than of Optimizely.

V11 logs through log4net, so the hook is an appender on the root logger of every configured log4net
repository. log4net resolves the appenders for an event by walking from its logger up the parent
chain, and it does that walk per event - which is why an appender added during initialization still
sees writes from loggers that already existed. Both configurations a V11 site actually ships with
are covered, a standalone `EPiServerLog.config` and an inline `<log4net>` section in `web.config`,
because the sink enumerates every repository rather than assuming the default one.

Nothing in this package references log4net at compile time, and it must not: `EPiServer.Framework`
has no log4net dependency of its own - `EPiServer.Logging.Log4Net` is a separate opt-in package - so
a V11 site is free to log through something else entirely. If log4net is not loaded the counters
stay empty and the startup log says which of the two situations you are in:

```
log4net is not loaded, so log write rate counters were not attached. On CMS 11 that usually means
the site logs through something other than EPiServer.Logging.Log4Net.
```

A flat zero and an absent series mean different things here. Zero is a reading - the counters
attached and the site logged nothing that minute. Absent means nothing attached, which is the
message above.

Two kinds of write it does not count, both worth knowing before you read the numbers. A logger
configured with `additivity="false"` stops the walk before it reaches root, so its events are not
seen - that is rare, and it is a decision the site made to keep a noisy subsystem out of the main
log. And log4net discards writes below the level it is configured to accept before any appender
sees them, so the counter measures what the site logs, not what it could have logged.

Severity comes from log4net's numeric level rather than the level name - `WARN` is 60000 and
`ERROR` is 70000 - so `FATAL` counts as an error, and a custom level a site declares between the
built-in ones lands on the correct side of both.

## Reading the counters

V11 runs on the .NET Framework, so `dotnet-counters` cannot attach. Both routes below work instead.

**PerfView / a custom EventListener.** The counters go to an `EventSource` named
`Optimizely-Performance`, and on .NET Framework each metric is written as a plain event rather than
through the `EventCounter` polling machinery. Any listener that enables that source sees them
immediately, with the counter name as the first payload argument.

**Application Insights.** [web.config.snippet.xml](web.config.snippet.xml) has the instrumentation
key wiring. Note the limitation below before relying on it.

## Application Insights is not wired up on V11, with one exception

`TelemetryStartup.Configure` takes an `IServiceCollection`, and V11 configures services through
`IServiceConfigurationProvider` instead. On V11 the telemetry detection still runs and is logged, but
nothing is subscribed to `EventCounterCollectionModule` - so Application Insights will not collect
this package's own counters on a V11 site yet.

Watch the startup log to confirm which path you are on. V11 logs:

```
Registered 8 SQL connection pool performance counters with Application Insights (detail counters off)
Application Insights detected - V11 has no IServiceCollection, so the Optimizely-Performance
counters are not registered with it. Read them off the EventSource directly with PerfView or your
own EventListener, enabling the source named Optimizely-Performance.
```

## SQL connection pool counters

The exception, and the one part of this package Application Insights does collect on V11. The pool
counters are Windows performance counters here rather than EventCounters, so they go through a
`PerformanceCollectorModule` this package builds and initializes against
`TelemetryConfiguration.Active` - which needs no `IServiceCollection`, which is why this path works
where the other does not.

Eight are collected with no configuration. Four more - active connections, free connections, and the
soft connect and disconnect rates, which are the ones that answer *how full is the pool* - are only
published by ADO.NET when a trace switch says so. [web.config.snippet.xml](web.config.snippet.xml)
has it commented in place:

```xml
<system.diagnostics>
  <switches>
    <add name="ConnectionPoolPerformanceCounterDetail" value="4" />
  </switches>
</system.diagnostics>
```

`4` is `TraceLevel.Verbose`, the only value ADO.NET accepts here. Restart the application pool
afterwards; the counters are created during provider initialization, not on demand. Without the
switch those four read a constant zero rather than failing, which on a chart is indistinguishable
from an idle pool - so this package omits them while it is off, and the startup log tells you which
of the two states you are in.

## Settings

Nothing has to be set. Every value has a default, and the defaults are what the package uses as
installed.

When you do want to change something, it comes from `appSettings`, under the same
`Optimizely:Instrumentation:*` key paths a V12 or V13 site writes into `appsettings.json` - one set
of names across all three majors, so what you learn here still applies after an upgrade. The
complete set, every value at its default, is written to
`App_Data\Optimizely.Performance.Counters\Optimizely.Instrumentation.template.config` when the
package is installed. Copy the `<add>` elements you want to change into `web.config` and leave the
rest out.

The one worth knowing before you need it switches the whole package off - no decorators, no probes,
no counters - without a redeployment:

```xml
<add key="Optimizely:Instrumentation:Enabled" value="false" />
```

A misspelled key is not silently ignored. Unrecognised settings under `Optimizely:Instrumentation`
are listed in the startup log, because a typo and a correctly configured counter on a healthy site
otherwise look identical from the outside.
