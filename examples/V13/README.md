# Optimizely CMS 13 (.NET 10)

```
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce   # Commerce 15 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration in `Program.cs`. There is nothing to add beyond the package reference.

## What gets instrumented

Everything V12 instruments - `IContentLoader`, `IContentRepository`,
`ISynchronizedObjectInstanceCache`, `IMemoryCache`, and `IOrderRepository` with the Commerce
package - plus `IEventPublisher`, which is new in V13. This is the only major where the four
`Optimizely.CMS.Events.*` counters produce values.

All four probes run: cache lock, thread pool queue delay, GC pause and lock contention. The cache
lock probe needed no change for V13 even though Optimizely moved the cache type into a separate
`EPiServer.Cache` assembly and renamed the lock field, because it matches on field *type* rather
than name.

V13 also moved the `Intercept<T>` extension into `EPiServer.DependencyInjection`. That is handled
inside the package; it changes nothing for a consumer.

## Log write rate

The three `Optimizely.Runtime.Logging.*` counters come from neither a decorator nor a probe. The
package registers an `ILoggerProvider` named `OptimizelyLogWriteRate` while the container is being
configured, and because `EPiServer.Logging.LogManager` forwards to `Microsoft.Extensions.Logging`,
that provider sees every write every real sink sees. It is the one chokepoint all logging passes
through on this version.

It counts at the host's default minimum level, which is what an unconfigured provider sees. To count
below that, give the provider its own level in `appsettings.json`:

```json
"Logging": { "OptimizelyLogWriteRate": { "LogLevel": { "Default": "Debug" } } }
```

That widens what the counter sees without changing what any sink writes - the counting logger has no
output of its own. It also always reports `IsEnabled` as false, so registering it cannot make an
`if (logger.IsEnabled(...))` guard elsewhere in the site start building messages nothing will write,
and it never calls the formatter: rendering is most of what a log write costs, and doing it a second
time to count it would make the measurement the expensive part.

## Application Insights

If the site already references `Microsoft.ApplicationInsights.AspNetCore` (or
`Microsoft.ApplicationInsights.WorkerService`), the module detects it during container configuration
and subscribes all 62 counters to `EventCounterCollectionModule` itself, along with the twelve
`Microsoft.Data.SqlClient` connection pool counters. The SqlClient counter names are unchanged
between the SqlClient 3.x that CMS 12 resolves and the 6.1.x that CMS 13 requires, so one list
covers both. You do not call `ConfigureTelemetryModule`.

[appsettings.json](appsettings.json) is the connection string side of that. Confirm it took effect
from the startup log:

```
Registered 74 EventCounters with Application Insights, from 2 event sources
```

If Application Insights is not installed, the counters still go to the `Optimizely-Performance`
EventSource and the log says so, with the `dotnet-counters` command to read them.
