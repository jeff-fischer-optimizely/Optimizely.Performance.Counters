# Correct Application Insights Integration

## The Right Way

Application Insights **already has an EventListener** (`EventCounterCollectionModule`) that collects EventCounters. We don't need to create our own EventListener to collect *our own* counters.

> Read "our own" strictly. The rule is about what a listener is **pointed at**, not about the
> existence of an `EventListener` in the product. Collecting the counters this package publishes
> is the host's job and we do not do it. Listening to *someone else's* EventSource to gather
> source data is a different thing and is allowed:
> [`ContentionProbe.ContentionBurstListener`](src/Optimizely.Performance.Counters.Core/Diagnostics/ContentionProbe.cs)
> subscribes to the runtime's contention keyword during a short capture burst, which is the only
> way to get wait *durations* rather than a contention count. It is deliberate and must stay.

We just need to **register our EventCounters** with Application Insights so it knows to collect them.

---

## How It Works

### 1. We Publish EventCounters (Already Done)

Our decorators track metrics via `IMetricTracker` → `EventCounterMetricTracker` → `OptimizelyPerformanceEventSource`.

This publishes EventCounters to the `"Optimizely-Performance"` EventSource.

### 2. Application Insights Collects Them (Now Implemented)

On startup, we call:

```csharp
ApplicationInsightsRegistration.RegisterEventCounters(context.Services, logger);
```

This does:

```csharp
builder.Services.ConfigureTelemetryModule<EventCounterCollectionModule>(
    (module, _) =>
    {
        // Register ALL our counters
        module.Counters.Add(
            new EventCounterCollectionRequest(
                "Optimizely-Performance",
                "Optimizely.CMS.Content.LoadTimeMs"));

        module.Counters.Add(
            new EventCounterCollectionRequest(
                "Optimizely-Performance",
                "Optimizely.CMS.Content.SaveTimeMs"));

        module.Counters.Add(
            new EventCounterCollectionRequest(
                "Optimizely-Performance",
                "Optimizely.Commerce.Orders.SaveTimeMs"));

        // ... etc for all counters in EventCounterRegistry
    });
```

### 3. Application Insights Collects and Sends to Azure

Application Insights' existing `EventCounterCollectionModule` automatically:
- Listens to `"Optimizely-Performance"` EventSource
- Collects all registered counters
- Sends to Application Insights backend
- Shows up in Azure Portal under Custom Metrics

---

## Architecture

```
Optimizely App
    ↓
InstrumentedContentLoader.Get()
    ↓
_metricTracker.TrackMetric("Optimizely.CMS.Content.LoadTimeMs", 15.3)
    ↓
EventCounterMetricTracker
    ↓
OptimizelyPerformanceEventSource.TrackMetric()
    ↓
EventCounter.WriteMetric(15.3)
    ↓
.NET EventCounter Infrastructure
    ↓
Application Insights EventCounterCollectionModule (EventListener)
    ← Registered via ConfigureTelemetryModule
    ↓
Collects "Optimizely.CMS.Content.LoadTimeMs" = 15.3
    ↓
Sends to Application Insights
    ↓
Azure Portal → Custom Metrics
```

---

## Key Files

### 1. EventCounterRegistry.cs

**Purpose**: Lists all counter names we publish

```csharp
public static class EventCounterRegistry
{
    public const string EventSourceName = "Optimizely-Performance";

    public static IEnumerable<string> GetAllCounterNames()
    {
        yield return "Optimizely.CMS.Content.LoadTimeMs";
        yield return "Optimizely.CMS.Content.SaveTimeMs";
        yield return "Optimizely.Commerce.Orders.SaveTimeMs";
        // ... etc
    }
}
```

### 2. ApplicationInsightsRegistration.cs

**Purpose**: Registers our counters with Application Insights

```csharp
public static void RegisterEventCounters(IServiceCollection services, ILogger? logger)
{
    // Uses reflection to call:
    // services.ConfigureTelemetryModule<EventCounterCollectionModule>((module, _) => {
    //     module.Counters.Add(new EventCounterCollectionRequest("Optimizely-Performance", "..."));
    // });
}
```

Uses reflection because we don't have a hard dependency on Application Insights.

### 3. CMSPerformanceCountersModule.cs

**Purpose**: Calls registration on startup

```csharp
private void DetectTelemetrySystems(ServiceConfigurationContext context)
{
    var telemetryInfo = ApplicationInsightsBridge.DetectTelemetrySystems();

    if (telemetryInfo.ApplicationInsightsAvailable)
    {
        ApplicationInsightsRegistration.RegisterEventCounters(context.Services, _logger);
    }
}
```

---

## What We DON'T Do

❌ **Collect our own counters with an EventListener** - Application Insights already has one. Listening to the runtime's own EventSources for source data is a separate thing and is allowed; see the note at the top.  
❌ **Collect metrics ourselves** - Application Insights does this  
❌ **Store metrics in a dictionary** - Not needed  
❌ **Call DotNetCounters.Initialize()** - Not related to our counters  

---

## What We DO

✅ **Publish EventCounters** via `OptimizelyPerformanceEventSource`  
✅ **Register them** with Application Insights' `EventCounterCollectionModule`  
✅ **Let Application Insights** collect and send them  

---

## Integration with Optimizely.Performance.DotNetCounters

`Optimizely.Performance.DotNetCounters` does the **same thing** for System.Runtime counters:

```csharp
// What DotNetCounters SHOULD do (but currently doesn't):
builder.Services.ConfigureTelemetryModule<EventCounterCollectionModule>(
    (module, _) =>
    {
        module.Counters.Add(new EventCounterCollectionRequest("System.Runtime", "cpu-usage"));
        module.Counters.Add(new EventCounterCollectionRequest("System.Runtime", "gc-heap-size"));
        // etc.
    });
```

**Our package** does this for `"Optimizely-Performance"` counters.

**Ideally**, both packages should register their counters with Application Insights the same way.

---

## User Experience

### Installation

```bash
dotnet add package Optimizely.Performance.Counters.CMS
```

### Startup Logs

```
[INFO] Telemetry Detection: Application Insights 2.22.0
[INFO] Optimizely EventCounters registered with Application Insights EventCounterCollectionModule
[INFO] Registered IMetricTracker: EventCounterMetricTracker
[INFO] Registered decorators: IContentLoader, IContentRepository
```

### Application Insights (Azure Portal)

Navigate to: **Application Insights → Metrics → Custom Metrics**

You'll see:
- `Optimizely.CMS.Content.LoadTimeMs`
- `Optimizely.CMS.Content.SaveTimeMs`
- `Optimizely.Commerce.Orders.SaveTimeMs`
- etc.

### Kusto Query

```kusto
customMetrics
| where name startswith "Optimizely."
| summarize avg(value) by name, bin(timestamp, 5m)
| render timechart
```

---

## Summary

✅ **No custom collector for our own counters** - use Application Insights' built-in one  
✅ **Register counters** via `ConfigureTelemetryModule<EventCounterCollectionModule>`  
✅ **Uses reflection** to avoid hard dependency  
✅ **Same pattern** that DotNetCounters should use  
✅ **Simple and correct**  

This is the **right way** to integrate with Application Insights!
