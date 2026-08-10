# Unified EventCounter Collection Architecture

## Problem Statement

**Before**: Two separate collection systems:
1. `Optimizely.Performance.DotNetCounters` collects **System.Runtime** counters
2. Our `Optimizely-Performance` EventSource publishes custom counters
3. They don't know about each other - Application Insights sees them as separate

**Goal**: Merge both into a single unified collection so Application Insights receives:
- ✅ .NET Runtime metrics (cpu-usage, gc-heap-size, threadpool-thread-count, etc.)
- ✅ Optimizely custom metrics (content loads, cart operations, etc.)
- ✅ All in one place, all tagged consistently

---

## Solution: Unified EventListener

### Architecture

```
┌─────────────────────────────────────────────┐
│  EventSources                                │
│                                              │
│  ┌──────────────────┐  ┌─────────────────┐ │
│  │  System.Runtime  │  │ Optimizely-     │ │
│  │                  │  │ Performance     │ │
│  │ - cpu-usage      │  │ - LoadTimeMs    │ │
│  │ - gc-heap-size   │  │ - SaveTimeMs    │ │
│  │ - threadpool-... │  │ - CartTotal     │ │
│  └──────────────────┘  └─────────────────┘ │
└──────────┬─────────────────────┬────────────┘
           │                     │
           └─────────┬───────────┘
                     ▼
     ┌───────────────────────────────────┐
     │  UnifiedEventCounterListener      │
     │  (EventListener)                  │
     │                                   │
     │  OnEventSourceCreated():          │
     │    if (System.Runtime)            │
     │      EnableEvents(...)            │
     │    if (Optimizely-Performance)    │
     │      EnableEvents(...)            │
     │                                   │
     │  OnEventWritten():                │
     │    Extract metric name & value    │
     │    Store in _metrics dictionary   │
     └───────────────┬───────────────────┘
                     │
                     ▼
     ┌───────────────────────────────────┐
     │  ConcurrentDictionary<string,     │
     │  double> _metrics                 │
     │                                   │
     │  ["cpu-usage"] = 12.5             │
     │  ["gc-heap-size"] = 524288        │
     │  ["LoadTimeMs[Operation=Get]"] =  │
     │    15.3                           │
     │  ["CartTotal[Currency=USD]"] =    │
     │    127.50                         │
     └───────────────┬───────────────────┘
                     │
                     ▼
     ┌───────────────────────────────────┐
     │  Application Insights             │
     │  (or DataDog, or dotnet-counters) │
     └───────────────────────────────────┘
```

---

## Implementation

### 1. UnifiedEventCounterListener

**File**: `UnifiedEventCounterListener.cs`

**Purpose**: Single EventListener that enables and collects from BOTH EventSources

**Key Methods**:

```csharp
protected override void OnEventSourceCreated(EventSource eventSource)
{
    // Enable System.Runtime counters (same as DotNetCounters)
    if (eventSource.Name == "System.Runtime" || 
        eventSource.Name.StartsWith(".NETRuntime"))
    {
        EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All,
            new Dictionary<string, string> { { "EventCounterIntervalSec", "1" } });
    }

    // Enable our custom Optimizely-Performance counters
    else if (eventSource.Name == "Optimizely-Performance")
    {
        EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All,
            new Dictionary<string, string> { { "EventCounterIntervalSec", "1" } });
    }
}

protected override void OnEventWritten(EventWrittenEventArgs eventData)
{
    // Extract metric name and value
    // Store in shared _metrics dictionary
    _metrics[name] = value;
}
```

### 2. UnifiedPerformanceCounters

**File**: `DotNetCountersBridge.cs` (renamed to UnifiedPerformanceCounters)

**Purpose**: Public API for initializing unified collection

```csharp
public static class UnifiedPerformanceCounters
{
    private static readonly ConcurrentDictionary<string, double> _metrics = new();
    
    public static void Initialize(ILogger? logger = null)
    {
        // Create single EventListener for both System.Runtime and Optimizely-Performance
        UnifiedEventCounterListener.Initialize(_metrics, logger);
    }

    public static IReadOnlyDictionary<string, double> GetAllMetrics() => _metrics;
}
```

### 3. Initialization

**File**: `CMSPerformanceCountersModule.cs`

```csharp
public void ConfigureContainer(ServiceConfigurationContext context)
{
    // Initialize unified collection (replaces PerformanceCountersBridge.Initialize())
    UnifiedPerformanceCounters.Initialize(_logger);
    
    // Rest of initialization...
}
```

---

## Comparison: Before vs After

### Before (Separate Collection)

```
DotNetCounters Package:
    PerformanceCountersBridge.Initialize()
        ↓
    DotNetEventCounters.Initialize()
        ↓
    EventListener for System.Runtime only
        ↓
    Collects: cpu-usage, gc-heap-size, etc.

Our Package:
    OptimizelyPerformanceEventSource publishes metrics
        ↓
    Application Insights discovers EventSource
        ↓
    Collects: LoadTimeMs, SaveTimeMs, etc.

Result: Two separate collections
```

### After (Unified Collection)

```
Our Package:
    UnifiedPerformanceCounters.Initialize()
        ↓
    UnifiedEventCounterListener enables:
        - System.Runtime EventSource
        - Optimizely-Performance EventSource
        ↓
    Single _metrics dictionary contains BOTH:
        - cpu-usage, gc-heap-size (from System.Runtime)
        - LoadTimeMs, SaveTimeMs (from Optimizely-Performance)
        ↓
    Application Insights collects all metrics together

Result: Unified collection
```

---

## Benefits

### ✅ Single Source of Truth
- All metrics (runtime + custom) in one dictionary
- Consistent collection interval (1 second)
- Same EventListener infrastructure

### ✅ Better Application Insights Integration
- Both metric sets tagged/grouped together
- Can correlate runtime metrics with custom metrics
- Single configuration point

### ✅ Replaces DotNetCounters EventListener
- No need to initialize DotNetCounters separately
- Our listener does everything DotNetCounters did, PLUS our custom counters
- Less complexity

### ✅ Easier Debugging
- `UnifiedPerformanceCounters.GetAllMetrics()` shows everything
- dotnet-counters sees both sets
- Single EventListener to troubleshoot

---

## Metrics Collected

### From System.Runtime

```
cpu-usage
working-set
gc-heap-size
gen-0-gc-count
gen-1-gc-count
gen-2-gc-count
time-in-gc
loh-size
alloc-rate
threadpool-thread-count
threadpool-queue-length
exception-count
monitor-lock-contention-count
active-timer-count
```

### From Optimizely-Performance

```
Optimizely.CMS.Content.LoadTimeMs[Operation=Get]
Optimizely.CMS.Content.LoadTimeMs[Operation=GetChildren]
Optimizely.CMS.Content.LoadOperations[Operation=Get,Success=true]
Optimizely.CMS.Content.SaveTimeMs[Type=Publish]
Optimizely.Commerce.Orders.SaveTimeMs[OrderType=Cart]
Optimizely.Commerce.Orders.CartLineItemCount
Optimizely.Commerce.Orders.CartTotal[Currency=USD]
... (all our custom metrics)
```

**All available via**:
- `UnifiedPerformanceCounters.GetAllMetrics()`
- Application Insights customMetrics table
- dotnet-counters CLI
- DataDog (if installed)

---

## Startup Logs

```
[INFO] Unified performance counter collection initialized - collecting System.Runtime and Optimizely-Performance EventSources
[DEBUG] Enabled EventSource: System.Runtime
[INFO] Enabled EventSource: Optimizely-Performance
[INFO] Telemetry Detection: Application Insights 2.22.0, EventCounters
[INFO] Application Insights integration enabled - metrics will be collected automatically
```

---

## Testing Locally

### Using dotnet-counters

```bash
# List available EventSources
dotnet-counters list --process-id <pid>

# Monitor both System.Runtime and Optimizely-Performance
dotnet-counters monitor --process-id <pid> System.Runtime Optimizely-Performance

# Output shows BOTH sets of metrics together
[System.Runtime]
    cpu-usage (%)                                         12.5
    gc-heap-size (MB)                                     45.2
[Optimizely-Performance]
    Optimizely.CMS.Content.LoadTimeMs[Operation=Get]      15.3
    Optimizely.Commerce.Orders.CartTotal[Currency=USD]    127.50
```

### Using Code

```csharp
// Get all metrics programmatically
var allMetrics = UnifiedPerformanceCounters.GetAllMetrics();

foreach (var (name, value) in allMetrics)
{
    Console.WriteLine($"{name}: {value}");
}
```

---

## Relationship to Optimizely.Performance.DotNetCounters

### Before This Change

- **DotNetCounters** was a separate package we had a dependency on
- We called `PerformanceCountersBridge.Initialize()` to initialize it
- It collected System.Runtime counters
- We published separate Optimizely-Performance counters
- They didn't integrate

### After This Change

- We **still have a dependency** on DotNetCounters (for API compatibility)
- But we **DON'T call** `PerformanceCountersBridge.Initialize()`
- Instead we call `UnifiedPerformanceCounters.Initialize()`
- Our UnifiedEventListener **replaces** DotNetCounters' EventListener
- We collect **both** System.Runtime and Optimizely-Performance together

### Should We Still Depend on DotNetCounters?

**Option A (Current)**: Keep dependency for API compatibility
- User expects both packages to work together
- Future: Could contribute our UnifiedEventListener back to DotNetCounters

**Option B**: Remove dependency entirely
- We provide everything DotNetCounters did, plus more
- Simpler dependency tree
- But user might wonder why they need two separate packages

**Recommendation**: **Keep the dependency** (Option A)
- Shows clear relationship between packages
- User installs both and they work together seamlessly
- Future path to merge improvements back to DotNetCounters

---

## Summary

✅ **Unified EventListener** collects System.Runtime + Optimizely-Performance  
✅ **Single dictionary** contains all metrics  
✅ **Application Insights** sees everything together  
✅ **Replaces** DotNetCounters EventListener with our enhanced version  
✅ **Still depends on** DotNetCounters package for compatibility  
✅ **No reflection** - direct method calls  

**Result**: Both .NET runtime counters AND Optimizely custom counters flow to Application Insights in a unified collection.
