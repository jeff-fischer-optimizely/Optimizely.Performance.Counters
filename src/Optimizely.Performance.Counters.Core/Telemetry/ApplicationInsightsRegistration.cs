using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Registers our finite list of Optimizely EventCounters with Application Insights.
    /// Uses minimal reflection to avoid hard dependency on Application Insights.
    /// </summary>
    public static class ApplicationInsightsRegistration
    {
        private const string EventCounterCollectorAssembly = "Microsoft.ApplicationInsights.EventCounterCollector";

        private const string ModuleTypeName =
            "Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.EventCounterCollectionModule, " +
            EventCounterCollectorAssembly;

        private const string RequestTypeName =
            "Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.EventCounterCollectionRequest, " +
            EventCounterCollectorAssembly;

        /// <summary>
        /// Where <c>ConfigureTelemetryModule</c> lives, in the order to try. A worker service and
        /// an ASP.NET Core host each ship their own copy under a different type and assembly, and
        /// a given process has only one of them.
        /// </summary>
        private static readonly string[] ConfigureTelemetryModuleHosts =
        {
            "Microsoft.ApplicationInsights.WorkerService.TelemetryModulesExtensions, " +
            "Microsoft.ApplicationInsights.WorkerService",

            "Microsoft.Extensions.DependencyInjection.ApplicationInsightsExtensions, " +
            "Microsoft.ApplicationInsights.AspNetCore",
        };

        /// <summary>
        /// Registers all Optimizely-Performance EventCounters from our finite list
        /// with Application Insights EventCounterCollectionModule.
        /// Equivalent to:
        /// <code>
        /// services.ConfigureTelemetryModule&lt;EventCounterCollectionModule&gt;((module, _) => {
        ///     module.Counters.Add(new EventCounterCollectionRequest("Optimizely-Performance", "Optimizely.CMS.Content.LoadTimeMs"));
        ///     // ... for each counter in EventCounterRegistry
        /// });
        /// </code>
        /// </summary>
        public static void RegisterEventCounters(IServiceCollection services, ILogger? logger = null)
        {
            try
            {
                var moduleType = FindFirstType(ModuleTypeName);
                if (moduleType == null)
                {
                    logger?.LogDebug("Application Insights EventCounterCollectionModule not found - skipping registration");
                    return;
                }

                var requestType = FindFirstType(RequestTypeName);
                if (requestType == null)
                {
                    logger?.LogDebug("EventCounterCollectionRequest type not found");
                    return;
                }

                var extensionType = FindFirstType(ConfigureTelemetryModuleHosts);
                if (extensionType == null)
                {
                    logger?.LogWarning("ConfigureTelemetryModule extension method not found - Application Insights not configured");
                    return;
                }

                var configureMethod = FindConfigureTelemetryModule(extensionType, moduleType);
                if (configureMethod == null)
                {
                    logger?.LogWarning("Could not find ConfigureTelemetryModule method");
                    return;
                }

                // Create the configuration action that adds our finite list of counters
                var actionType = typeof(Action<,>).MakeGenericType(moduleType, typeof(object));
                Action<object, object> configAction = (module, _) =>
                {
                    AddCountersToModule(module, moduleType, requestType, logger);
                };

                var typedAction = Delegate.CreateDelegate(actionType, configAction.Target, configAction.Method);

                // Call: services.ConfigureTelemetryModule<EventCounterCollectionModule>(typedAction)
                configureMethod.Invoke(null, new object[] { services, typedAction });

                logger?.LogInformation(
                    "Registered {Count} Optimizely EventCounters with Application Insights",
                    EventCounterRegistry.GetAllCounterNames().Count());
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to register EventCounters with Application Insights");
            }
        }

        /// <summary>
        /// The first of the given assembly-qualified names that resolves, or null if none do.
        /// Application Insights is an optional dependency, so a miss is an ordinary outcome
        /// rather than an error.
        /// </summary>
        private static Type? FindFirstType(params string[] assemblyQualifiedNames) =>
            assemblyQualifiedNames
                .Select(name => Type.GetType(name, throwOnError: false))
                .FirstOrDefault(type => type != null);

        private static MethodInfo? FindConfigureTelemetryModule(Type extensionType, Type moduleType)
        {
            var configureMethod = extensionType.GetMethod(
                "ConfigureTelemetryModule",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(IServiceCollection), typeof(Action<,>).MakeGenericType(moduleType, typeof(object)) },
                null);

            if (configureMethod != null)
            {
                return configureMethod;
            }

            // Try generic version
            return extensionType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "ConfigureTelemetryModule" && method.IsGenericMethodDefinition)
                .Select(method => method.MakeGenericMethod(moduleType))
                .FirstOrDefault();
        }

        private static void AddCountersToModule(object module, Type moduleType, Type requestType, ILogger? logger)
        {
            var countersProperty = moduleType.GetProperty("Counters", BindingFlags.Public | BindingFlags.Instance);
            var counters = countersProperty?.GetValue(module);

            if (counters == null)
            {
                logger?.LogWarning("EventCounterCollectionModule.Counters is null");
                return;
            }

            var addMethod = counters.GetType().GetMethod("Add");
            if (addMethod == null)
            {
                logger?.LogWarning("Counters.Add method not found");
                return;
            }

            // Add each counter from our finite list
            foreach (var counterName in EventCounterRegistry.GetAllCounterNames())
            {
                try
                {
                    // new EventCounterCollectionRequest("Optimizely-Performance", counterName)
                    var request = Activator.CreateInstance(requestType, CounterNames.EventSourceName, counterName);
                    addMethod.Invoke(counters, new[] { request });
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to add counter {CounterName}", counterName);
                }
            }
        }
    }
}
