# Examples

Everything here is about consuming the published packages, not about building them.

| Folder | What it is | Needs a licence? |
| --- | --- | --- |
| [verify-package-install/](verify-package-install/) | A console app that installs the packages on all six target frameworks and checks they bind | No |
| [V11/](V11/) | What to add to a CMS 11 site (`web.config`, .NET Framework 4.7.2) | Yes |
| [V12/](V12/) | What to add to a CMS 12 site (`appsettings.json`, .NET 6-9) | Yes |
| [V13/](V13/) | What to add to a CMS 13 site (`appsettings.json`, .NET 10) | Yes |

The end-to-end runbook - getting counters to show up in `dotnet-counters` and in Application
Insights Live Metrics on a real site - is [docs/SMOKE_TEST.md](../docs/SMOKE_TEST.md).

## Installing

There is nothing to write. Both packages ship an `[InitializableModule]` that Optimizely discovers
on its own, and all the registration happens in `ConfigureContainer` before any module initializes.

```
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce   # only if the site is a Commerce site
```

`Optimizely.Performance.Counters.Core` and `Optimizely.Performance.DotNetCounters` come along
transitively; the two never ship apart.

## Running against a local build

[NuGet.config](NuGet.config) in this folder adds `../build/nupkg` as a source, so a locally packed
1.0.0 wins over the feed:

```
dotnet pack -c Release -o build/nupkg          # from the repo root
dotnet run --project examples/verify-package-install -f net10.0
```

Swap `-f` for `net472`, `net6.0`, `net7.0`, `net8.0` or `net9.0` to check another target framework.
It exits non-zero on failure, so it works as a build step.

## What the verifier proves, and what it does not

It proves the package graph resolves, that the right `lib/` folder is chosen, that the EPiServer
assemblies bind at runtime, that the target framework maps to the expected Optimizely major, and
that the EventSource constructs without faulting - the last one because an `EventSource` reports
configuration failures through `ConstructionException` rather than throwing, so a broken one looks
healthy to every caller and silently publishes nothing.

It does not start Optimizely. Nothing is intercepted, no counter is written, and no telemetry is
exported. That needs a licensed site, which is what the runbook covers.
