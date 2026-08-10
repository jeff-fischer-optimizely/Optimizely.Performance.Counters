# Implementation Roadmap

## Current Status: Foundation Complete ✅

The core infrastructure is in place and ready for counter implementations.

### ✅ Completed (Phase 1)

1. **Project Structure**
   - Multi-targeted csproj (net472, net6.0, net8.0, net9.0, net10.0)
   - Solution file
   - NuGet.config with Optimizely feed
   - .gitignore

2. **Version Detection System**
   - `OptimizelyVersion` enum
   - `OptimizelyVersionDetector` with multi-strategy detection
   - Compilation symbols (CMS11, CMS12, CMS13)

3. **Counter Infrastructure**
   - `IPerformanceCounter` interface
   - `PerformanceCounterBase` abstract class
   - `CounterType` enum (Rate, Latency, Gauge, Percentage)

4. **Initialization & Validation**
   - `PerformanceCountersInitializationModule`
   - Runtime version validation with hard failures
   - Dependency validation (OptiDotNetCounters required)
   - Counter registration and initialization

5. **Documentation**
   - README.md with version compatibility matrix
   - PROJECT_SUMMARY.md with design decisions
   - Clear error messages for misconfigurations

6. **Example Implementations**
   - `ContentLoadsPerSecondCounter` (rate counter example)
   - `ContentAverageLoadTimeCounter` (latency counter example)

---

## 🚧 Phase 2: Tier 1 Counters (Critical - 47 counters)

These are the highest priority counters providing immediate operational value.

### CMS Content Operations (6 counters)

```
✅ Optimizely.CMS.Content.LoadsPerSecond
✅ Optimizely.CMS.Content.AverageLoadTimeMs
⬜ Optimizely.CMS.Content.SavesPerSecond
⬜ Optimizely.CMS.Content.AverageSaveTimeMs
⬜ Optimizely.CMS.Content.PublishesPerSecond
⬜ Optimizely.CMS.Content.AveragePublishTimeMs
```

**Implementation Guide:**
- Hook into `IContentEvents` (Saving, Saved, Publishing, Published)
- Use `Stopwatch` for timing measurements
- Report every 60 seconds

### CMS Cache System (6 counters)

```
⬜ Optimizely.CMS.Cache.ObjectCacheHitRate
⬜ Optimizely.CMS.Cache.ObjectCacheMissRate
⬜ Optimizely.CMS.Cache.CachedObjectCount
⬜ Optimizely.CMS.Cache.CacheMemoryUsageMB
⬜ Optimizely.CMS.Cache.InvalidationsPerSecond
⬜ Optimizely.CMS.Cache.RemoteSyncOperationsPerSecond
```

**Implementation Guide:**
- Decorate `ISynchronizedObjectInstanceCache` to track Get() hits/misses
- Hook into `EPiServer.Events` for remote cache sync
- Use reflection to get cache size if not exposed

### CMS Events (4 counters)

```
⬜ Optimizely.CMS.Events.ContentEventsPerSecond
⬜ Optimizely.CMS.Events.RemoteEventsPerSecond
⬜ Optimizely.CMS.Events.AverageEventDeliveryTimeMs
⬜ Optimizely.CMS.Events.RemoteEventFailuresPerSecond
```

**Implementation Guide:**
- Hook into `IContentEvents` for all event types
- Monitor `IEventPublisher` for remote events
- Track Azure Service Bus metrics if available

### Commerce Orders (6 counters)

```
⬜ Optimizely.Commerce.Orders.CartOperationsPerSecond
⬜ Optimizely.Commerce.Orders.AverageCartCalculationTimeMs
⬜ Optimizely.Commerce.Orders.OrderCreatesPerSecond
⬜ Optimizely.Commerce.Orders.AverageOrderProcessingTimeMs
⬜ Optimizely.Commerce.Orders.CheckoutsPerSecond
⬜ Optimizely.Commerce.Orders.AverageCheckoutTimeMs
```

**Implementation Guide:**
- Decorate `IOrderRepository` to intercept operations
- Hook into `IOrderGroupCalculator.GetTotals()` for timing
- Track `ProcessPayments()`, `ApplyDiscounts()` extension methods

### Commerce Pricing (4 counters)

```
⬜ Optimizely.Commerce.Pricing.RequestsPerSecond
⬜ Optimizely.Commerce.Pricing.AverageLookupTimeMs
⬜ Optimizely.Commerce.Pricing.CalculationsPerSecond
⬜ Optimizely.Commerce.Pricing.AverageCalculationTimeMs
```

**Implementation Guide:**
- Decorate `IPriceService.GetPrices()`
- Track `IPlacedPriceProcessor` operations

### Commerce Inventory (5 counters)

```
⬜ Optimizely.Commerce.Inventory.RequestsPerSecond
⬜ Optimizely.Commerce.Inventory.AverageRequestTimeMs
⬜ Optimizely.Commerce.Inventory.ReservationsPerSecond
⬜ Optimizely.Commerce.Inventory.AdjustmentsPerSecond
⬜ Optimizely.Commerce.Inventory.ConcurrentOperations
```

**Implementation Guide:**
- Decorate `IInventoryProcessor`, `InventoryLoader`
- Use `Interlocked` for concurrent operation counter
- **Critical**: This is highest concurrency risk area

### Commerce Promotions (4 counters)

```
⬜ Optimizely.Commerce.Promotions.EvaluationsPerSecond
⬜ Optimizely.Commerce.Promotions.AverageExecutionTimeMs
⬜ Optimizely.Commerce.Promotions.DiscountCalculationsPerSecond
⬜ Optimizely.Commerce.Promotions.ActivePromotionsCount
```

**Implementation Guide:**
- Hook into `IPromotionEngine` evaluation
- Track `ApplyDiscounts()` extension method
- Query active promotions count periodically

### Commerce Payments (4 counters)

```
⬜ Optimizely.Commerce.Payments.OperationsPerSecond
⬜ Optimizely.Commerce.Payments.AverageProcessingTimeMs
⬜ Optimizely.Commerce.Payments.SuccessRate
⬜ Optimizely.Commerce.Payments.FailureRate
```

**Implementation Guide:**
- Hook into `ProcessPayments()` extension method
- Track payment provider operations
- Calculate success/failure rates

### Infrastructure Database (8 counters)

```
⬜ Optimizely.Infrastructure.Database.QueriesPerSecond
⬜ Optimizely.Infrastructure.Database.AverageQueryTimeMs
⬜ Optimizely.Infrastructure.Database.P95QueryTimeMs
⬜ Optimizely.Infrastructure.Database.ActiveConnections
⬜ Optimizely.Infrastructure.Database.AvailableConnections
⬜ Optimizely.Infrastructure.Database.ConnectionTimeouts
⬜ Optimizely.Infrastructure.Database.ExceptionsPerSecond
⬜ Optimizely.Infrastructure.Database.DeadlocksPerSecond
```

**Implementation Guide:**
- Use ADO.NET command interceptor (net472)
- Use EF Core interceptors (net6.0+)
- Track connection pool via performance counters
- **Most critical bottleneck** in Optimizely

---

## 📋 Phase 3: Tier 2 Counters (Important - 59 counters)

### CMS Search - Find (V11/V12) (6 counters)

```
⬜ Optimizely.CMS.Search.QueriesPerSecond
⬜ Optimizely.CMS.Search.AverageQueryTimeMs
⬜ Optimizely.CMS.Search.IndexingOperationsPerSecond
⬜ Optimizely.CMS.Search.IndexingQueueDepth
⬜ Optimizely.CMS.Search.QueryErrorsPerSecond
⬜ Optimizely.CMS.Search.IndexingErrorsPerSecond
```

**Conditional Compilation:**
```csharp
#if CMS11 || CMS12
// Find implementation
#endif
```

### CMS Search - Graph (V13) (4 counters)

```
⬜ Optimizely.CMS.Graph.QueriesPerSecond
⬜ Optimizely.CMS.Graph.AverageQueryTimeMs
⬜ Optimizely.CMS.Graph.SyncOperationsPerSecond
⬜ Optimizely.CMS.Graph.SyncErrorsPerSecond
```

**Conditional Compilation:**
```csharp
#if CMS13
// Graph implementation
#endif
```

### CMS Content Delivery API (5 counters)

```
⬜ Optimizely.CMS.API.RequestsPerSecond
⬜ Optimizely.CMS.API.AverageResponseTimeMs
⬜ Optimizely.CMS.API.P95ResponseTimeMs
⬜ Optimizely.CMS.API.ErrorRate
⬜ Optimizely.CMS.API.CacheHitRate
```

**Implementation Guide:**
- ASP.NET middleware for `/api/episerver/*` routes
- Track response codes for error rate

### Commerce Catalog (5 counters)

```
⬜ Optimizely.Commerce.Catalog.ProductLoadsPerSecond
⬜ Optimizely.Commerce.Catalog.AverageLoadTimeMs
⬜ Optimizely.Commerce.Catalog.CatalogQueriesPerSecond
⬜ Optimizely.Commerce.Catalog.CacheHitRate
⬜ Optimizely.Commerce.Catalog.ActiveSKUCount
```

### Commerce Tax & Shipping (4 counters)

```
⬜ Optimizely.Commerce.Tax.CalculationsPerSecond
⬜ Optimizely.Commerce.Tax.AverageCalculationTimeMs
⬜ Optimizely.Commerce.Shipping.CalculationsPerSecond
⬜ Optimizely.Commerce.Shipping.AverageCalculationTimeMs
```

### Infrastructure Blob Storage (4 counters)

```
⬜ Optimizely.Infrastructure.Blobs.ReadsPerSecond
⬜ Optimizely.Infrastructure.Blobs.WritesPerSecond
⬜ Optimizely.Infrastructure.Blobs.AverageReadTimeMs
⬜ Optimizely.Infrastructure.Blobs.AverageWriteTimeMs
```

**Implementation Guide:**
- Decorate `BlobProvider` abstract methods
- Works with file and Azure Blob Storage providers

### Infrastructure DDS (4 counters)

```
⬜ Optimizely.Infrastructure.DDS.QueriesPerSecond
⬜ Optimizely.Infrastructure.DDS.SavesPerSecond
⬜ Optimizely.Infrastructure.DDS.AverageQueryTimeMs
⬜ Optimizely.Infrastructure.DDS.AverageSaveTimeMs
```

### Infrastructure Service Bus (7 counters)

```
⬜ Optimizely.Infrastructure.ServiceBus.MessagesSentPerSecond
⬜ Optimizely.Infrastructure.ServiceBus.MessagesReceivedPerSecond
⬜ Optimizely.Infrastructure.ServiceBus.AverageDeliveryTimeMs
⬜ Optimizely.Infrastructure.ServiceBus.FailedDeliveriesPerSecond
⬜ Optimizely.Infrastructure.ServiceBus.PendingMessages
⬜ Optimizely.Infrastructure.ServiceBus.DeadLetterQueueDepth
⬜ Optimizely.Infrastructure.ServiceBus.AverageProcessingTimeMs
```

### Infrastructure Scheduled Jobs (4 counters)

```
⬜ Optimizely.Infrastructure.ScheduledJobs.ExecutionsPerHour
⬜ Optimizely.Infrastructure.ScheduledJobs.SuccessRate
⬜ Optimizely.Infrastructure.ScheduledJobs.FailureRate
⬜ Optimizely.Infrastructure.ScheduledJobs.AverageDurationMs
```

---

## 📊 Phase 4: Tier 3 Counters (Operational - 15 counters)

Lower priority operational visibility counters. Implement after Tier 1 & 2.

---

## 🔧 Phase 5: Configuration System

```
⬜ Configuration model (enable/disable counters)
⬜ appsettings.json support (V12/V13)
⬜ web.config support (V11)
⬜ Counter filtering by category/subsystem
⬜ Reporting interval customization
⬜ Example configuration files
```

---

## 📚 Phase 6: Documentation & Examples

```
⬜ COUNTER_REFERENCE.md (all counters listed)
⬜ KUSTO_QUERIES.md (cookbook of useful queries)
⬜ TROUBLESHOOTING.md (common issues)
⬜ Example V11 project integration
⬜ Example V12 project integration
⬜ Example V13 project integration
```

---

## 🧪 Phase 7: Testing

```
⬜ Unit tests for version detection
⬜ Unit tests for counter base classes
⬜ Integration tests with mock Optimizely services
⬜ Build verification across all targets
⬜ Package installation verification
```

---

## 📦 Phase 8: Packaging & Release

```
⬜ NuGet package metadata
⬜ Package icon
⬜ Release notes
⬜ Version tagging strategy
⬜ CI/CD pipeline (if applicable)
⬜ Package publishing
```

---

## Estimated Effort

| Phase | Counters | Estimated Time |
|-------|----------|----------------|
| Phase 1 (Complete) | Foundation | ✅ Done |
| Phase 2 | 47 Tier 1 | 2-3 days |
| Phase 3 | 59 Tier 2 | 3-4 days |
| Phase 4 | 15 Tier 3 | 1 day |
| Phase 5 | Configuration | 1 day |
| Phase 6 | Documentation | 1-2 days |
| Phase 7 | Testing | 2-3 days |
| Phase 8 | Packaging | 0.5 day |
| **Total** | **121 counters** | **10-15 days** |

---

## Implementation Tips

### Counter Implementation Pattern

1. Create counter class extending `PerformanceCounterBase`
2. Inject dependencies (events, services, telemetry, logger)
3. Override properties (Name, Category, Subsystem, Type)
4. Implement `Initialize()` to hook into events/services
5. Use `TrackMetric()` to report values
6. Implement `Dispose()` to unhook events
7. Register in `PerformanceCountersInitializationModule`

### Testing Strategy

1. Build and verify all 5 targets compile
2. Test version detection logic with mock assemblies
3. Validate runtime checks throw on mismatch
4. Verify counters report to Application Insights
5. Check performance overhead is acceptable

### Deployment Strategy

1. Create alpha package for internal testing
2. Test on V11, V12, V13 projects
3. Validate metrics appear in Application Insights
4. Create beta package for partner testing
5. Release stable 1.0.0 version

---

## Next Immediate Steps

1. **Implement remaining Tier 1 CMS counters** (Content saves/publishes)
2. **Implement CMS Cache counters** (high value, relatively easy)
3. **Implement Commerce Order counters** (critical for e-commerce)
4. **Implement Database counters** (most common bottleneck)
5. **Test end-to-end** with a sample V12 project

Once Tier 1 is complete, the package provides significant value and can be released as 1.0.0-beta.
