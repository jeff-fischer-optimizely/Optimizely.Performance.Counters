# Optimizely CMS 12 (.NET 6 - .NET 9)

```
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce   # Commerce 14 sites only
```

`CMSPerformanceCountersModule` is an `[InitializableModule]`, so Optimizely finds it without any
registration in `Program.cs`. There is nothing to add beyond the package reference.

## What gets instrumented

`IContentLoader`, `IContentRepository`, `ISynchronizedObjectInstanceCache` and `IMemoryCache`, plus
`IOrderRepository` if the Commerce package is installed. `IEventPublisher` is a V13 addition, so the
four `Optimizely.CMS.Events.*` counters stay at zero here.

All four probes run: cache lock, thread pool queue delay, GC pause and lock contention. They need no
configuration and start themselves. `Optimizely.Runtime.GC.IntervalPauseMs` requires .NET 8 or later
and stays at zero on .NET 6 and .NET 7.

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
and subscribes all 75 counters to `EventCounterCollectionModule` itself, along with the twelve
`Microsoft.Data.SqlClient` connection pool counters. You do not call `ConfigureTelemetryModule`.

[appsettings.json](appsettings.json) is the connection string side of that. Confirm it took effect
from the startup log:

```
Registered 74 EventCounters with Application Insights, from 2 event sources
```

If Application Insights is not installed, the counters still go to the `Optimizely-Performance`
EventSource and the log says so, with the `dotnet-counters` command to read them.

## .NET 6 and .NET 7

Both runtimes are out of Microsoft support. They are still in the matrix, but net8.0 or net9.0 is
the better target for a site being set up now.

Nothing about the Commerce package differs across the V12 band: all four target frameworks build
against Commerce 14.5.0, the oldest release with a `net6.0` assembly, and that is a minimum rather
than a pin. A net8.0 site running Commerce 14.46 installs the same package.
