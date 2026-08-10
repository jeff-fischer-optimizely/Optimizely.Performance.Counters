# Optimizely Performance Counters

Custom performance counters for Optimizely CMS and Commerce, providing deep introspection into internal operations across V11, V12, and V13.

## ⚠️ CRITICAL: Version Compatibility

This package uses **strict framework-to-version mapping** to ensure compile-time type safety and correct counter implementations.

### Supported Configurations

| Your Optimizely Version | Required .NET Version | Package Build Used | Counter Backend |
|------------------------|----------------------|-------------------|-----------------|
| **V11** (CMS 11.x) | .NET Framework 4.7.2 | `net472` | Find/SQL |
| **V12** (CMS 12.x) | **.NET 6 ONLY** | `net6.0` | Find/SQL |
| **V13** (CMS 13.x) | .NET 8, 9, or 10 | `net8.0`+ | Graph/SQL |

### ❌ Unsupported Configurations

**V12 on .NET 8+**: While Optimizely V12 *can* technically run on .NET 8, **this package does NOT support it**. The `net8.0` build is compiled against V13 APIs (including Graph). If you're running V12, you must use .NET 6.

**V13 on .NET 6**: V13 requires .NET 8 minimum when using this package to ensure compile-time safety and access to V13-specific features.

### Why These Restrictions?

- **V13 uses Optimizely Graph** - fundamentally different architecture from V12's Find/SQL backend
- **Compile-time type safety** - ensures you use the correct counter implementations for your version
- **Prevents runtime errors** - API mismatches are caught at compile time, not in production

### Migration Guidance

**Upgrading from V12 to V13?**
1. Upgrade your project to .NET 8 or later
2. Upgrade Optimizely CMS to V13
3. Reinstall this package - it will automatically use the V13 build

**Running V12 on .NET 8?**
- **Recommended**: Stay on .NET 6 until you're ready to upgrade to V13
- **Alternative**: Don't use this performance counter package until after upgrading to V13
- **Not Recommended**: Fork and maintain your own build

---

## 📦 Installation

```bash
dotnet add package Optimizely.Performance.Counters
```

### Requirements

- **Optimizely CMS** V11, V12, or V13
- **Optimizely.Performance.DotNetCounters** (automatically installed as dependency)
- **Application Insights** configured in your project

---

## 🎯 Features

### CMS Counters

**Content Operations:**
- `Optimizely.CMS.Content.LoadsPerSecond` - Content load rate
- `Optimizely.CMS.Content.AverageLoadTimeMs` - Average load latency
- `Optimizely.CMS.Content.SavesPerSecond` - Content save rate
- `Optimizely.CMS.Content.AverageSaveTimeMs` - Average save latency
- `Optimizely.CMS.Content.PublishesPerSecond` - Publishing rate
- `Optimizely.CMS.Content.AveragePublishTimeMs` - Average publish duration

**Cache System:**
- `Optimizely.CMS.Cache.ObjectCacheHitRate` - Cache hit percentage
- `Optimizely.CMS.Cache.CachedObjectCount` - Current cached objects
- `Optimizely.CMS.Cache.InvalidationsPerSecond` - Cache invalidation rate
- `Optimizely.CMS.Cache.RemoteSyncOperationsPerSecond` - Multi-server sync rate

**Search (V11/V12 - Find):**
- `Optimizely.CMS.Search.QueriesPerSecond` - Search query rate
- `Optimizely.CMS.Search.AverageQueryTimeMs` - Average query latency
- `Optimizely.CMS.Search.IndexingOperationsPerSecond` - Indexing rate

**Search (V13 - Graph):**
- `Optimizely.CMS.Graph.QueriesPerSecond` - GraphQL query rate
- `Optimizely.CMS.Graph.AverageQueryTimeMs` - Average GraphQL latency
- `Optimizely.CMS.Graph.SyncOperationsPerSecond` - Content sync to Graph

**Events:**
- `Optimizely.CMS.Events.ContentEventsPerSecond` - Content event rate
- `Optimizely.CMS.Events.RemoteEventsPerSecond` - Remote event rate
- `Optimizely.CMS.Events.RemoteEventFailuresPerSecond` - Failed remote events

### Commerce Counters

**Orders:**
- `Optimizely.Commerce.Orders.CartOperationsPerSecond` - Cart operation rate
- `Optimizely.Commerce.Orders.AverageCartCalculationTimeMs` - Cart calc latency
- `Optimizely.Commerce.Orders.OrderCreatesPerSecond` - Order creation rate
- `Optimizely.Commerce.Orders.CheckoutsPerSecond` - Checkout rate
- `Optimizely.Commerce.Orders.AverageCheckoutTimeMs` - Average checkout duration

**Pricing:**
- `Optimizely.Commerce.Pricing.RequestsPerSecond` - Price lookup rate
- `Optimizely.Commerce.Pricing.AverageLookupTimeMs` - Price lookup latency

**Inventory:**
- `Optimizely.Commerce.Inventory.RequestsPerSecond` - Inventory lookup rate
- `Optimizely.Commerce.Inventory.ReservationsPerSecond` - Stock reservation rate
- `Optimizely.Commerce.Inventory.AdjustmentsPerSecond` - Inventory adjustment rate

**Promotions:**
- `Optimizely.Commerce.Promotions.EvaluationsPerSecond` - Promotion evaluation rate
- `Optimizely.Commerce.Promotions.AverageExecutionTimeMs` - Promotion engine latency
- `Optimizely.Commerce.Promotions.ActivePromotionsCount` - Active promotions (gauge)

**Payments:**
- `Optimizely.Commerce.Payments.OperationsPerSecond` - Payment operation rate
- `Optimizely.Commerce.Payments.AverageProcessingTimeMs` - Payment processing latency
- `Optimizely.Commerce.Payments.SuccessRate` - Successful payments percentage

### Infrastructure Counters

**Database:**
- `Optimizely.Infrastructure.Database.QueriesPerSecond` - Database query rate
- `Optimizely.Infrastructure.Database.AverageQueryTimeMs` - Average query latency
- `Optimizely.Infrastructure.Database.ActiveConnections` - Active DB connections
- `Optimizely.Infrastructure.Database.ConnectionTimeouts` - Connection timeouts/sec

**Blob Storage:**
- `Optimizely.Infrastructure.Blobs.ReadsPerSecond` - Blob read rate
- `Optimizely.Infrastructure.Blobs.WritesPerSecond` - Blob write rate
- `Optimizely.Infrastructure.Blobs.AverageReadTimeMs` - Blob read latency

**Dynamic Data Store (DDS):**
- `Optimizely.Infrastructure.DDS.QueriesPerSecond` - DDS query rate
- `Optimizely.Infrastructure.DDS.SavesPerSecond` - DDS save rate
- `Optimizely.Infrastructure.DDS.AverageQueryTimeMs` - DDS query latency

**Service Bus:**
- `Optimizely.Infrastructure.ServiceBus.MessagesSentPerSecond` - Message send rate
- `Optimizely.Infrastructure.ServiceBus.MessagesReceivedPerSecond` - Message receive rate
- `Optimizely.Infrastructure.ServiceBus.FailedDeliveriesPerSecond` - Failed deliveries

**Scheduled Jobs:**
- `Optimizely.Infrastructure.ScheduledJobs.ExecutionsPerHour` - Job execution rate
- `Optimizely.Infrastructure.ScheduledJobs.SuccessRate` - Job success percentage
- `Optimizely.Infrastructure.ScheduledJobs.AverageDurationMs` - Average job duration

---

## 🚀 Usage

### Automatic Initialization

The package initializes automatically on application startup via Optimizely's `IConfigurableModule` system. No code changes required.

### Runtime Validation

On startup, the module:
1. Validates `Optimizely.Performance.DotNetCounters` is installed
2. Detects your Optimizely CMS version
3. Validates the detected version matches the compiled version
4. Registers appropriate counters
5. **Throws an exception** if configuration is invalid

**Example startup log:**

```
[INFO] Version Detection:
Detected Version: V12
Expected Version: V12
CMS.Core: 12.20.0
Framework: 12.20.0
Commerce.Core: 14.15.3
Has ASP.NET Core: True
Target Framework: .NET 6

[INFO] Version validation successful: Detected V12, Expected V12
[INFO] Registering performance counters for Optimizely V12
[INFO] Initialized counter: Optimizely.CMS.Content.LoadsPerSecond
[INFO] Initialized counter: Optimizely.CMS.Content.AverageLoadTimeMs
...
```

**Example error (misconfiguration):**

```
[ERROR] CRITICAL: Package compiled for Optimizely V12 but detected V13.
This is an unsupported configuration.

Supported configurations:
  - V11 requires .NET Framework 4.7.2
  - V12 requires .NET 6
  - V13 requires .NET 8+

If you are running V12 on .NET 8, this package does NOT support that configuration.
Please upgrade to V13 or downgrade to .NET 6.

See README.md for version compatibility details.
```

### Viewing Metrics in Application Insights

1. Navigate to your Application Insights resource in Azure Portal
2. Go to **Metrics**
3. Select metric namespace **"Custom"**
4. Select specific counters by name (e.g., `Optimizely.CMS.Content.LoadsPerSecond`)
5. Add filters and aggregations as needed

### Kusto Queries

**View all Optimizely counters:**

```kusto
customMetrics
| where name startswith "Optimizely."
| project timestamp, name, value
| order by timestamp desc
```

**Track content load performance over time:**

```kusto
customMetrics
| where name == "Optimizely.CMS.Content.LoadsPerSecond"
| summarize avg(value), max(value), min(value) by bin(timestamp, 5m)
| render timechart
```

**Monitor order processing latency:**

```kusto
customMetrics
| where name == "Optimizely.Commerce.Orders.AverageCheckoutTimeMs"
| summarize percentiles(value, 50, 95, 99) by bin(timestamp, 15m)
| render timechart
```

**Detect database performance issues:**

```kusto
customMetrics
| where name in (
    "Optimizely.Infrastructure.Database.AverageQueryTimeMs",
    "Optimizely.Infrastructure.Database.ConnectionTimeouts"
)
| summarize avg(value) by name, bin(timestamp, 5m)
| render timechart
```

**Commerce health dashboard:**

```kusto
let timeRange = 1h;
customMetrics
| where timestamp > ago(timeRange)
| where name in (
    "Optimizely.Commerce.Orders.CheckoutsPerSecond",
    "Optimizely.Commerce.Payments.SuccessRate",
    "Optimizely.Commerce.Promotions.AverageExecutionTimeMs",
    "Optimizely.Commerce.Inventory.AdjustmentsPerSecond"
)
| summarize avg(value) by name
```

---

## 🔧 Configuration

### Enabling/Disabling Counters

Currently, all counters are enabled by default. Configuration options coming in future release.

### Custom Counters

To add your own custom counters:

1. Implement `IPerformanceCounter` or extend `PerformanceCounterBase`
2. Register in DI container
3. Call `Initialize()` during startup

**Example:**

```csharp
public class MyCustomCounter : PerformanceCounterBase
{
    public MyCustomCounter(
        TelemetryClient telemetryClient,
        ILogger<MyCustomCounter> logger)
        : base(telemetryClient, logger)
    {
    }

    public override string Name => "Optimizely.Custom.MyMetric";
    public override string Category => "Custom";
    public override string Subsystem => "MyFeature";
    public override CounterType Type => CounterType.Rate;

    public override void Initialize()
    {
        base.Initialize();
        // Hook into events, start timers, etc.
    }
}
```

---

## 🐛 Troubleshooting

### "Unable to detect Optimizely CMS version"

**Cause**: EPiServer assemblies not loaded or incorrect package version.

**Solution**:
- Ensure `EPiServer.CMS.Core` is installed
- Verify package version matches your CMS version (11.x, 12.x, or 13.x)
- Check that Optimizely initialization has completed

### "Package compiled for V12 but detected V13"

**Cause**: Wrong NuGet package build selected (usually .NET version mismatch).

**Solution**:
- V12 requires .NET 6 - upgrade or downgrade .NET version
- V13 requires .NET 8+ - upgrade or downgrade CMS version
- Do NOT run V12 on .NET 8 with this package

### "Optimizely.Performance.DotNetCounters package is required but not found"

**Cause**: Missing dependency.

**Solution**:
```bash
dotnet add package Optimizely.Performance.DotNetCounters
```

### Counters not appearing in Application Insights

1. Verify Application Insights connection string is configured
2. Check startup logs for initialization errors
3. Ensure `TelemetryClient` is registered in DI
4. Allow 2-5 minutes for metrics to appear in portal

---

## 📊 Performance Impact

- Counter collection runs on background threads
- Default reporting interval: 60 seconds
- Negligible CPU/memory overhead (<1% in most scenarios)
- Failed counter reads handled gracefully without exceptions

---

## 🔐 License

Apache-2.0

---

## 🤝 Support

For issues and feature requests, please use the GitHub issue tracker.

---

## 📚 Related Packages

- [Optimizely.Performance.DotNetCounters](../OptiDotNetCounters) - .NET runtime and ASP.NET counters (required)
- [Optimizely.Performance.ServiceBus](../OptiServiceBusInterceptor) - Service Bus message prioritization and telemetry

---

## 🗺️ Counter Reference

For a complete list of all available counters organized by category, see [COUNTER_REFERENCE.md](docs/COUNTER_REFERENCE.md).

For counter tier classifications (Critical/Important/Operational), see the initial proposal in project documentation.
