using System;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Detects if Application Insights is available and configures EventCounter collection.
    /// This bridges our EventCounters to Application Insights without hard dependency.
    /// </summary>
    public static class ApplicationInsightsBridge
    {
        private const string EventSourceName = "Optimizely-Performance";

        /// <summary>
        /// Attempts to configure Application Insights to collect our EventCounters.
        /// Returns true if Application Insights is available and configured, false otherwise.
        /// </summary>
        public static bool TryConfigureApplicationInsights(ILogger? logger = null)
        {
            try
            {
                // Check if Application Insights assemblies are loaded
                var telemetryConfigType = Type.GetType(
                    "Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration, Microsoft.ApplicationInsights",
                    throwOnError: false);

                if (telemetryConfigType == null)
                {
                    logger?.LogInformation(
                        "Application Insights not detected - metrics will be available via EventCounters only");
                    return false;
                }

                // Get the active TelemetryConfiguration
                var activeProperty = telemetryConfigType.GetProperty("Active",
                    BindingFlags.Public | BindingFlags.Static);
                var activeConfig = activeProperty?.GetValue(null);

                if (activeConfig == null)
                {
                    logger?.LogWarning("Application Insights TelemetryConfiguration.Active is null");
                    return false;
                }

                // Try to configure EventCounter collection
                var configured = TryConfigureEventCounterModule(activeConfig, logger);

                if (!configured)
                {
                    // Fallback: Try configuring via TelemetryModules collection directly
                    configured = TryAddEventCounterToConfiguration(activeConfig, logger);
                }

                if (configured)
                {
                    logger?.LogInformation(
                        "Application Insights configured to collect EventSource: {EventSourceName}",
                        EventSourceName);
                }
                else
                {
                    logger?.LogWarning(
                        "Application Insights detected but automatic EventCounter configuration failed. " +
                        "Metrics will still be available if EventCounterCollectionModule is manually configured.");
                }

                return configured;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error configuring Application Insights");
                return false;
            }
        }

        private static bool TryConfigureEventCounterModule(object telemetryConfig, ILogger? logger)
        {
            try
            {
                // Get TelemetryModules collection
                var modulesProperty = telemetryConfig.GetType()
                    .GetProperty("TelemetryModules", BindingFlags.Public | BindingFlags.Instance);
                var modules = modulesProperty?.GetValue(telemetryConfig);

                if (modules == null)
                    return false;

                // Find EventCounterCollectionModule
                var moduleType = Type.GetType(
                    "Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.EventCounterCollectionModule, " +
                    "Microsoft.ApplicationInsights.EventCounterCollector",
                    throwOnError: false);

                if (moduleType == null)
                {
                    // Try older SDK location
                    moduleType = Type.GetType(
                        "Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.EventCounterCollectionModule, " +
                        "Microsoft.ApplicationInsights.PerfCounterCollector",
                        throwOnError: false);
                }

                if (moduleType == null)
                {
                    logger?.LogDebug("EventCounterCollectionModule type not found");
                    return false;
                }

                // Find existing module instance
                object? eventCounterModule = null;
                var enumerableType = modules.GetType().GetInterface("IEnumerable`1");
                if (enumerableType != null)
                {
                    var getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator");
                    var enumerator = getEnumeratorMethod?.Invoke(modules, null);
                    if (enumerator != null)
                    {
                        var moveNextMethod = enumerator.GetType().GetMethod("MoveNext");
                        var currentProperty = enumerator.GetType().GetProperty("Current");

                        while ((bool)(moveNextMethod?.Invoke(enumerator, null) ?? false))
                        {
                            var current = currentProperty?.GetValue(enumerator);
                            if (current?.GetType() == moduleType)
                            {
                                eventCounterModule = current;
                                break;
                            }
                        }
                    }
                }

                if (eventCounterModule == null)
                {
                    logger?.LogDebug("EventCounterCollectionModule instance not found in TelemetryModules");
                    return false;
                }

                // Add our EventSource to the Counters collection
                var countersProperty = moduleType.GetProperty("Counters",
                    BindingFlags.Public | BindingFlags.Instance);
                var counters = countersProperty?.GetValue(eventCounterModule);

                if (counters == null)
                {
                    logger?.LogDebug("EventCounterCollectionModule.Counters is null");
                    return false;
                }

                // Create EventCounterCollectionRequest
                var requestType = Type.GetType(
                    "Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.EventCounterCollectionRequest, " +
                    "Microsoft.ApplicationInsights.EventCounterCollector",
                    throwOnError: false);

                if (requestType == null)
                {
                    // Try older SDK location
                    requestType = Type.GetType(
                        "Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.EventCounterCollectionRequest, " +
                        "Microsoft.ApplicationInsights.PerfCounterCollector",
                        throwOnError: false);
                }

                if (requestType != null)
                {
                    var request = Activator.CreateInstance(requestType);
                    requestType.GetProperty("EventSourceName")?.SetValue(request, EventSourceName);

                    // Add to collection
                    var addMethod = counters.GetType().GetMethod("Add");
                    addMethod?.Invoke(counters, new[] { request });

                    logger?.LogDebug("Added {EventSourceName} to EventCounterCollectionModule.Counters", EventSourceName);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to configure EventCounterCollectionModule");
                return false;
            }
        }

        private static bool TryAddEventCounterToConfiguration(object telemetryConfig, ILogger? logger)
        {
            // Alternative approach: Add to configuration if module auto-discovers EventSources
            // Many versions of AI SDK auto-discover EventSources, so just having the EventSource
            // might be enough
            logger?.LogDebug("EventCounterCollectionModule configuration not accessible - relying on auto-discovery");
            return false; // Let auto-discovery handle it
        }

        /// <summary>
        /// Gets information about available telemetry systems.
        /// </summary>
        public static TelemetrySystemInfo DetectTelemetrySystems()
        {
            var info = new TelemetrySystemInfo();

            // Check Application Insights
            var aiType = Type.GetType(
                "Microsoft.ApplicationInsights.TelemetryClient, Microsoft.ApplicationInsights",
                throwOnError: false);
            info.ApplicationInsightsAvailable = aiType != null;

            if (info.ApplicationInsightsAvailable)
            {
                info.ApplicationInsightsVersion = aiType?.Assembly.GetName().Version?.ToString();
            }

            // Check DataDog (common assembly name)
            var ddType = Type.GetType(
                "Datadog.Trace.Tracer, Datadog.Trace",
                throwOnError: false);
            info.DataDogAvailable = ddType != null;

            if (info.DataDogAvailable)
            {
                info.DataDogVersion = ddType?.Assembly.GetName().Version?.ToString();
            }

            // EventCounters are always available in .NET Core 2.1+ and .NET Framework 4.7.2+
            info.EventCountersAvailable = true;

            return info;
        }
    }

    /// <summary>
    /// Information about detected telemetry systems.
    /// </summary>
    public class TelemetrySystemInfo
    {
        /// <summary>True when an Application Insights assembly is loaded in the process.</summary>
        public bool ApplicationInsightsAvailable { get; set; }

        /// <summary>Version of the loaded Application Insights assembly, or null if absent.</summary>
        public string? ApplicationInsightsVersion { get; set; }

        /// <summary>True when a DataDog tracer assembly is loaded in the process.</summary>
        public bool DataDogAvailable { get; set; }

        /// <summary>Version of the loaded DataDog assembly, or null if absent.</summary>
        public string? DataDogVersion { get; set; }

        /// <summary>True when the runtime supports .NET EventCounters.</summary>
        public bool EventCountersAvailable { get; set; }

        /// <summary>
        /// Renders the detected systems as a single line suitable for a log entry.
        /// </summary>
        /// <returns>A human-readable summary of the detected telemetry systems.</returns>
        public override string ToString()
        {
            var systems = new System.Collections.Generic.List<string>();

            if (ApplicationInsightsAvailable)
                systems.Add($"Application Insights {ApplicationInsightsVersion}");

            if (DataDogAvailable)
                systems.Add($"DataDog {DataDogVersion}");

            if (EventCountersAvailable)
                systems.Add("EventCounters");

            return systems.Count > 0
                ? $"Telemetry Systems: {string.Join(", ", systems)}"
                : "No telemetry systems detected";
        }
    }
}
