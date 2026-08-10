# Optimizely Performance Counters - Project Summary

## Overview

This project provides custom performance counters for Optimizely CMS and Commerce across all three major versions (V11, V12, V13). It delivers deep introspection into internal operations including content management, caching, orders, pricing, inventory, and infrastructure.

## Key Design Decisions

### 1. Strict Framework-to-Version Mapping

**Decision**: Use compile-time version targeting rather than runtime detection for counter implementations.

**Rationale**:
- V13 uses Optimizely Graph (fundamentally different from V12's Find/SQL)
- Cannot abstract over architectural differences at runtime
- Compile-time safety prevents API mismatches
- Clear documentation prevents misconfiguration

**Implementation**:
```xml
<TargetFrameworks>net472;net6.0;net8.0;net9.0;net10.0</TargetFrameworks>

<!-- net472 = V11 only -->
<!-- net6.0 = V12 only -->
<!-- net8.0+ = V13 only -->
```

### 2. Required Dependency on OptiDotNetCounters

**Decision**: Make Optimizely.Performance.DotNetCounters a **required transitive dependency**.

**Rationale**:
- User wants both packages installed together
- DotNetCounters provides .NET runtime metrics (GC, threads, HTTP)
- OptiCounters provides Optimizely-specific metrics (content, orders, cache)
- Together they provide complete visibility

**Implementation**:
```xml
<ItemGroup>
  <PackageReference Include="Optimizely.Performance.DotNetCounters" Version="1.0.0" />
  <!-- Note: NO PrivateAssets - this is transitive -->
</ItemGroup>
```

### 3. Multi-Strategy Version Detection

**Decision**: Use multiple detection strategies with fallbacks.

**Strategies** (in order):
1. EPiServer.CMS.Core assembly version (11.x, 12.x, 13.x)
2. EPiServer.Framework assembly version
3. EPiServer.Commerce.Core version (13=V11, 14=V12, 15=V13)
4. ASP.NET Core presence (indicates V12+)
5. Compilation symbol as last resort

**Rationale**:
- Different projects might have different assembly combinations
- Provides reliable detection in all scenarios
- Logs detailed info for troubleshooting

### 4. Runtime Validation with Hard Failures

**Decision**: Throw exceptions on version mismatch rather than silent degradation.

**Rationale**:
- Version mismatches cause incorrect counter implementations
- Silent failures lead to confusing metrics
- Better to fail fast and loudly with clear error messages
- Forces users to fix configuration rather than ship with bad telemetry

**Implementation**:
```csharp
if (detected != expected)
{
    throw new InvalidOperationException(
        $"Package compiled for {expected} but detected {detected}. " +
        "See README.md for version compatibility.");
}
```

## Project Structure

```
OptiCounters/
├── src/
│   └── Optimizely.Performance.Counters/
│       ├── VersionDetection/
│       │   ├── OptimizelyVersion.cs
│       │   └── OptimizelyVersionDetector.cs
│       ├── Infrastructure/
│       │   ├── IPerformanceCounter.cs
│       │   └── PerformanceCounterBase.cs
│       ├── CMS/
│       │   ├── Content/
│       │   │   ├── ContentLoadsPerSecondCounter.cs
│       │   │   └── ContentAverageLoadTimeCounter.cs
│       │   ├── Cache/ (TODO)
│       │   ├── Events/ (TODO)
│       │   └── Search/ (TODO - version-specific)
│       ├── Commerce/
│       │   ├── Orders/ (TODO)
│       │   ├── Pricing/ (TODO)
│       │   ├── Inventory/ (TODO)
│       │   ├── Promotions/ (TODO)
│       │   └── Payments/ (TODO)
│       ├── Infrastructure/
│       │   ├── Database/ (TODO)
│       │   ├── Blob/ (TODO)
│       │   ├── DDS/ (TODO)
│       │   └── ServiceBus/ (TODO)
│       ├── Initialization/
│       │   └── PerformanceCountersInitializationModule.cs
│       └── Optimizely.Performance.Counters.csproj
├── examples/
│   ├── V11/ (TODO)
│   ├── V12/ (TODO)
│   └── V13/ (TODO)
├── docs/ (TODO)
├── build/ (TODO)
├── README.md
├── NuGet.config
├── .gitignore
└── Optimizely.Performance.Counters.sln
```

## Counter Categories

### Tier 1 - Critical (47 counters)
Must-have counters providing immediate operational value:
- Content load/save/publish operations and latency
- Cache hit rates and invalidations
- Order processing and checkout performance
- Pricing lookups
- Promotion engine execution time
- Database query performance
- Payment processing

### Tier 2 - Important (59 counters)
Significant value for operations and troubleshooting:
- Search query performance
- Inventory operations
- Catalog queries
- Publishing events
- Blob storage operations
- DDS operations
- Scheduled jobs

### Tier 3 - Operational (15 counters)
Nice-to-have visibility into background operations:
- Job execution details
- Cache evictions
- Storage metrics

**Total**: 121 counters

## Implementation Status

### ✅ Completed
- [x] Project structure and solution
- [x] Multi-targeted csproj with strict version mapping
- [x] Version detection system (multi-strategy)
- [x] Counter infrastructure (interfaces, base classes)
- [x] Initialization module with runtime validation
- [x] README with version compatibility documentation
- [x] Example CMS content counters (2 implemented as templates)

### 🚧 In Progress
- [ ] Complete CMS counters (Content, Cache, Events, Search)
- [ ] Implement Commerce counters (Orders, Pricing, Inventory, Promotions, Payments)
- [ ] Implement Infrastructure counters (Database, Blob, DDS, ServiceBus)

### 📋 TODO
- [ ] Configuration system (enable/disable counters)
- [ ] Example configuration files (V11/V12/V13)
- [ ] Counter reference documentation
- [ ] Build/packaging scripts
- [ ] Unit tests
- [ ] Integration tests

## Version Compatibility Matrix

| .NET Version | CMS Version | Commerce Version | Package Target | Supported |
|--------------|-------------|------------------|----------------|-----------|
| net472 | V11 (11.x) | 13.x | net472 | ✅ Yes |
| net6.0 | V12 (12.x) | 14.x | net6.0 | ✅ Yes |
| net6.0 | V13 (13.x) | 15.x | net6.0 | ❌ No - Use .NET 8+ |
| net8.0 | V12 (12.x) | 14.x | net8.0 | ❌ No - Use .NET 6 |
| net8.0 | V13 (13.x) | 15.x | net8.0 | ✅ Yes |
| net9.0 | V13 (13.x) | 15.x | net9.0 | ✅ Yes |
| net10.0 | V13 (13.x) | 15.x | net10.0 | ✅ Yes |

## Key Learnings & Patterns

### 1. Event-Based Counter Pattern

For operation rate counters, hook into Optimizely events:

```csharp
public class ContentLoadsPerSecondCounter : PerformanceCounterBase
{
    private readonly IContentEvents _contentEvents;
    private long _loadCount;
    private Timer _reportingTimer;

    public override void Initialize()
    {
        _contentEvents.LoadedContent += OnContentLoaded;
        _reportingTimer = new Timer(ReportMetrics, null, 60000, 60000);
    }

    private void OnContentLoaded(object sender, ContentEventArgs e)
    {
        Interlocked.Increment(ref _loadCount);
    }

    private void ReportMetrics(object state)
    {
        var count = Interlocked.Exchange(ref _loadCount, 0);
        var rate = count / 60.0; // per second
        TrackMetric(Name, rate);
    }
}
```

### 2. Latency Measurement Pattern

For operation latency, track start/end timing:

```csharp
public class ContentAverageLoadTimeCounter : PerformanceCounterBase
{
    private readonly ConcurrentBag<double> _latencies = new();

    public override void Initialize()
    {
        _contentEvents.LoadingContent += OnContentLoading;
        _contentEvents.LoadedContent += OnContentLoaded;
    }

    private void OnContentLoading(object sender, ContentEventArgs e)
    {
        var stopwatch = Stopwatch.StartNew();
        _loadTimings.TryAdd(e.ContentLink, stopwatch);
    }

    private void OnContentLoaded(object sender, ContentEventArgs e)
    {
        if (_loadTimings.TryRemove(e.ContentLink, out var sw))
        {
            sw.Stop();
            _latencies.Add(sw.Elapsed.TotalMilliseconds);
        }
    }
}
```

### 3. Version-Specific Features

Use compilation symbols for version-specific code:

```csharp
#if CMS11
    // V11-specific Find counter implementation
    private void RegisterFindCounters(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton<IPerformanceCounter, FindSearchQueriesCounter>();
    }
#elif CMS13
    // V13-specific Graph counter implementation
    private void RegisterGraphCounters(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton<IPerformanceCounter, GraphQueriesCounter>();
    }
#endif
```

## Next Steps for Implementation

1. **Complete Tier 1 counters** (47 critical counters):
   - CMS: Content operations, Cache, Events
   - Commerce: Orders, Pricing, Inventory, Promotions, Payments
   - Infrastructure: Database

2. **Add Tier 2 counters** (59 important counters):
   - Search (version-specific)
   - Catalog operations
   - Blob storage
   - DDS
   - ServiceBus

3. **Configuration system**:
   - Enable/disable counters via config
   - Reporting interval customization
   - Counter filtering

4. **Documentation**:
   - Counter reference guide
   - Kusto query cookbook
   - Troubleshooting guide

5. **Testing**:
   - Unit tests for version detection
   - Integration tests with mock Optimizely services
   - Build verification

6. **Packaging**:
   - NuGet package creation
   - Version tagging strategy
   - Release notes

## Dependencies

### Runtime Dependencies
- Optimizely.Performance.DotNetCounters 1.0.0 (required, transitive)
- Microsoft.ApplicationInsights 2.22.0
- Microsoft.Extensions.Logging.Abstractions 6.0.0
- EPiServer.CMS.Core (version-specific, PrivateAssets)
- EPiServer.Framework (version-specific, PrivateAssets)
- EPiServer.Commerce.Core (version-specific, PrivateAssets)

### Development Dependencies
- .NET SDK 9.0+ (for multi-targeting)
- Optimizely NuGet feed access

## Build Commands

```bash
# Restore packages
dotnet restore

# Build all targets
dotnet build

# Build specific target
dotnet build -f net472
dotnet build -f net6.0
dotnet build -f net8.0

# Pack NuGet package
dotnet pack -c Release

# Run tests (when implemented)
dotnet test
```

## Related Projects

- **OptiDotNetCounters**: .NET runtime and ASP.NET performance counters
- **OptiServiceBusInterceptor**: Service Bus message prioritization and telemetry

## Support & Contribution

- Version detection issues: Check assembly versions in startup logs
- Missing counters: File GitHub issue with details
- Version mismatch errors: Verify .NET and CMS version alignment
- Custom counters: Extend IPerformanceCounter or PerformanceCounterBase

## License

Apache-2.0
