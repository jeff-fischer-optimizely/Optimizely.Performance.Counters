# Architecture: Decorator Pattern for Performance Counters

## Overview

The OptiCounters project uses the **Decorator Pattern** to intercept Optimizely operations and instrument them with performance counters. This is achieved through three separate NuGet packages.

---

## Package Structure

### 1. **Optimizely.Performance.Counters.Core**

**Purpose**: Shared infrastructure used by both CMS and Commerce packages.

**Contents**:
- `IPerformanceCounter` interface
- `PerformanceCounterBase` abstract class
- `OptimizelyVersionDetector` (multi-strategy version detection)
- Common utilities

**Dependencies**:
- Microsoft.ApplicationInsights
- Microsoft.Extensions.Logging.Abstractions

**Consumers**: Referenced by CMS and Commerce packages

---

### 2. **Optimizely.Performance.Counters.CMS**

**Purpose**: Performance counters for Optimizely CMS operations.

**Instrumentation Strategy**: Decorator pattern on core CMS interfaces

#### Decorators Implemented

| Interface | Decorator | Metrics Tracked |
|-----------|-----------|----------------|
| `IContentLoader` | `InstrumentedContentLoader` | Load operations/sec, load time, item counts |
| `IContentRepository` | `InstrumentedContentRepository` | Save/publish/delete operations, timing |
| `ISynchronizedObjectInstanceCache` | `InstrumentedCache` (TODO) | Cache hit/miss rate, invalidations |
| `IEventPublisher` | `InstrumentedEventPublisher` (TODO) | Event rate, delivery time |

#### Key Metrics

```
Optimizely.CMS.Content.LoadTimeMs (by Operation)
Optimizely.CMS.Content.LoadOperations (by Operation, Success)
Optimizely.CMS.Content.ItemsLoaded (by Operation)
Optimizely.CMS.Content.SaveTimeMs (by Type)
Optimizely.CMS.Content.SaveOperations (by Type, Success)
Optimizely.CMS.Content.PublishTimeMs (by Type)
Optimizely.CMS.Content.PublishOperations (by Type, Success)
```

#### Registration

```csharp
// In CMSPerformanceCountersModule.ConfigureContainer()
context.Services.Intercept<IContentLoader>(
    (locator, defaultImplementation) => new InstrumentedContentLoader(
        defaultImplementation,
        locator.GetInstance<TelemetryClient>(),
        locator.GetInstance<ILogger<InstrumentedContentLoader>>()));
```

**How Intercept Works**:
1. Optimizely registers default `IContentLoader` implementation
2. Our module intercepts registration
3. Wraps default implementation with our decorator
4. All calls to `IContentLoader` now go through decorator first
5. Decorator tracks metrics, then delegates to inner implementation

---

### 3. **Optimizely.Performance.Counters.Commerce**

**Purpose**: Performance counters for Optimizely Commerce operations.

**Instrumentation Strategy**: Decorator pattern on Commerce interfaces

#### Decorators Implemented

| Interface | Decorator | Metrics Tracked |
|-----------|-----------|----------------|
| `IOrderRepository` | `InstrumentedOrderRepository` | Cart/order saves, loads, creates, deletes |
| `IPriceService` | `InstrumentedPriceService` (TODO) | Price lookups, calculation time |
| `IInventoryProcessor` | `InstrumentedInventoryProcessor` (TODO) | Inventory operations, adjustments |
| `IPromotionEngine` | `InstrumentedPromotionEngine` (TODO) | Promotion evaluations, execution time |

#### Key Metrics

```
Optimizely.Commerce.Orders.SaveTimeMs (by OrderType)
Optimizely.Commerce.Orders.SaveOperations (by OrderType, Success)
Optimizely.Commerce.Orders.LoadTimeMs (by OrderType)
Optimizely.Commerce.Orders.CartLineItemCount
Optimizely.Commerce.Orders.CartTotal (by Currency)
```

---

## Decorator Pattern Deep Dive

### Example: InstrumentedContentLoader

```csharp
public class InstrumentedContentLoader : IContentLoader
{
    private readonly IContentLoader _inner;  // The actual Optimizely implementation
    private readonly TelemetryClient _telemetryClient;
    private readonly ILogger _logger;

    // Implement IContentLoader interface
    public T Get<T>(ContentReference contentLink) where T : IContentData
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Delegate to actual Optimizely implementation
            var result = _inner.Get<T>(contentLink);
            sw.Stop();

            // Track metrics
            TrackLoadOperation("Get<T>", sw.Elapsed.TotalMilliseconds, success: true);
            return result;
        }
        catch
        {
            sw.Stop();
            TrackLoadOperation("Get<T>", sw.Elapsed.TotalMilliseconds, success: false);
            throw;  // Re-throw to maintain Optimizely behavior
        }
    }

    // Implement all other IContentLoader methods similarly...
}
```

### Why Decorators vs Event-Based?

**Decorators** (Current approach):
✅ Intercept every call (100% coverage)
✅ Measure exact operation timing
✅ Track parameters (item counts, types)
✅ No dependency on Optimizely events existing
✅ Works even if events are disabled

**Events** (Alternative approach):
❌ Only fires if Optimizely fires event
❌ Timing is approximate (start/end events)
❌ Limited to operations that have events
✅ Less invasive (no interception needed)

---

## Multi-Targeting Strategy

### Compilation Symbols

| Target Framework | CMS Version | Commerce Version | Symbols |
|-----------------|-------------|------------------|---------|
| `net472` | V11 (11.x) | 13.x | `CMS11`, `COMMERCE13` |
| `net6.0` | V12 (12.x) | 14.x | `CMS12`, `COMMERCE14` |
| `net8.0+` | V13 (13.x) | 15.x | `CMS13`, `COMMERCE15` |

### Version-Specific Code

```csharp
#if CMS11 || CMS12
    // Find-based search tracking
    context.Services.Intercept<EPiServer.Find.IClient>(
        (locator, inner) => new InstrumentedFindClient(inner, ...));
#endif

#if CMS13
    // Graph-based search tracking
    // Different implementation for V13's Graph API
#endif
```

---

## Registration Flow

### 1. Application Startup
```
Optimizely App starts
    ↓
EPiServer.Web.InitializationModule loads
    ↓
CMSPerformanceCountersModule.ConfigureContainer() called
```

### 2. Version Validation
```csharp
var detected = OptimizelyVersionDetector.DetectVersion();  // Runtime detection
var expected = OptimizelyVersionDetector.GetExpectedVersion();  // Compile-time

if (detected != expected)
    throw new InvalidOperationException("Version mismatch!");
```

### 3. Decorator Registration
```csharp
context.Services.Intercept<IContentLoader>(
    (locator, defaultImpl) => new InstrumentedContentLoader(defaultImpl, ...));
```

### 4. Runtime Behavior
```
User code: var content = _contentLoader.Get<PageData>(pageRef);
    ↓
InstrumentedContentLoader.Get<T>() called
    ↓
Metrics tracked (Stopwatch.Start)
    ↓
_inner.Get<T>() (actual Optimizely implementation)
    ↓
Metrics tracked (Stopwatch.Stop, TrackMetric)
    ↓
Result returned to user code
```

---

## Decorator Implementation Checklist

For each Optimizely interface you want to instrument:

### 1. Create Decorator Class

```csharp
public class InstrumentedXXX : IXXX
{
    private readonly IXXX _inner;
    private readonly TelemetryClient _telemetryClient;
    private readonly ILogger _logger;

    public InstrumentedXXX(IXXX inner, TelemetryClient tc, ILogger logger)
    {
        _inner = inner;
        _telemetryClient = tc;
        _logger = logger;
    }

    // Implement all interface methods...
}
```

### 2. Implement All Interface Methods

- Wrap with Stopwatch for timing
- Try/catch for success/failure tracking
- Delegate to `_inner` for actual operation
- Track metrics via TelemetryClient
- Re-throw exceptions (don't swallow)

### 3. Register in InitializationModule

```csharp
context.Services.Intercept<IXXX>(
    (locator, defaultImpl) => new InstrumentedXXX(
        defaultImpl,
        locator.GetInstance<TelemetryClient>(),
        locator.GetInstance<ILogger<InstrumentedXXX>>()));
```

### 4. Test

- Verify decorator is called (add logging)
- Verify metrics appear in Application Insights
- Verify Optimizely functionality still works
- Verify performance overhead is acceptable

---

## Remaining Decorators Needed

### CMS Package

| Priority | Interface | Purpose |
|----------|-----------|---------|
| **High** | `ISynchronizedObjectInstanceCache` | Cache hit/miss rate |
| **High** | `IEventPublisher` | Remote event tracking |
| **Medium** | `EPiServer.Find.IClient` (V11/V12) | Search query tracking |
| **Medium** | Graph client (V13) | Graph query tracking |
| **Medium** | Blob providers | Blob storage operations |
| **Low** | `DynamicDataStore` | DDS operation tracking |

### Commerce Package

| Priority | Interface | Purpose |
|----------|-----------|---------|
| **High** | `IPriceService` | Pricing lookups/calculations |
| **High** | `IInventoryProcessor` | Inventory operations |
| **High** | `IPromotionEngine` | Promotion evaluations |
| **Medium** | `ITaxCalculator` | Tax calculations |
| **Medium** | `IShippingCalculator` | Shipping calculations |
| **Medium** | `IPaymentProcessor` | Payment operations |

---

## Alternative Interception Strategies

### Strategy 1: Decorator Pattern (Current)
✅ Clean separation
✅ Type-safe
✅ Testable
❌ Must implement full interface

### Strategy 2: Castle DynamicProxy
✅ Less boilerplate (automatic proxying)
❌ Reflection overhead
❌ Additional dependency

### Strategy 3: Aspect-Oriented Programming (PostSharp)
✅ Attribute-based interception
❌ Requires PostSharp license
❌ Build-time weaving

### Strategy 4: IL Weaving (Fody)
✅ No runtime overhead
❌ Complex setup
❌ Harder to debug

**Decision**: Decorator pattern provides best balance of simplicity, performance, and maintainability.

---

## Performance Considerations

### Overhead Per Operation

- Stopwatch creation: ~100ns
- TelemetryClient.TrackMetric: ~1-10μs (buffered)
- Total overhead: <0.01% for typical operations

### Optimization Strategies

1. **Lazy metric tracking**: Only create Stopwatch if enabled
2. **Sampling**: Track every Nth operation for high-frequency calls
3. **Async metrics**: Fire-and-forget metric tracking
4. **Conditional compilation**: Completely remove overhead in Debug builds

```csharp
#if RELEASE
    TrackMetric("MyMetric", value);
#endif
```

---

## Testing Strategy

### Unit Tests
- Mock `_inner` implementation
- Verify metrics tracked correctly
- Verify exceptions propagate

### Integration Tests
- Deploy to test Optimizely instance
- Verify decorators registered
- Verify metrics in Application Insights
- Performance benchmarks

---

## Deployment

### Package Installation

```bash
# For CMS-only projects
dotnet add package Optimizely.Performance.Counters.CMS

# For Commerce projects (includes Core automatically)
dotnet add package Optimizely.Performance.Counters.Commerce

# For projects with both
dotnet add package Optimizely.Performance.Counters.CMS
dotnet add package Optimizely.Performance.Counters.Commerce
```

### What Gets Installed

1. Core assembly (shared infrastructure)
2. CMS or Commerce assembly (decorators)
3. Optimizely.Performance.DotNetCounters (transitive dependency)

### Initialization

**Automatic** via `IConfigurableModule` - no code changes required.

---

## Summary

✅ **3 separate packages**: Core, CMS, Commerce
✅ **Decorator pattern**: Intercepts Optimizely interfaces
✅ **Multi-targeting**: net472, net6.0, net8.0+
✅ **Version validation**: Hard failures on mismatch
✅ **Application Insights**: Direct metric tracking
✅ **Zero configuration**: Auto-registers via InitializationModule

**Next Steps**: Implement remaining decorators for cache, events, pricing, inventory, and promotions.
