using System;
using System.Collections;
using System.Collections.Generic;
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
        /// <summary>
        /// The assembly holding the EventCounter collector, which is <c>Microsoft.AI.*</c> and not
        /// <c>Microsoft.ApplicationInsights.*</c>.
        /// </summary>
        /// <remarks>
        /// The NuGet package is called Microsoft.ApplicationInsights.EventCounterCollector; the
        /// assembly inside it is not, and <see cref="Type.GetType(string, bool)"/> wants the
        /// assembly. Naming the package here instead is why none of this ever ran: the lookup
        /// returned null on every host, registration logged "not found - skipping" at Debug and
        /// returned, and <see cref="TelemetryStartup"/> then announced success anyway. Several of
        /// the AI collectors are packaged this way - DependencyCollector, PerfCounterCollector,
        /// ServerTelemetryChannel and WindowsServer all ship as Microsoft.AI.* - so the mismatch is
        /// the rule rather than an exception to check for once.
        /// </remarks>
        private const string EventCounterCollectorAssembly = "Microsoft.AI.EventCounterCollector";

        private const string EventCounterCollectorNamespace =
            "Microsoft.ApplicationInsights.Extensibility.EventCounterCollector.";

        private const string ModuleTypeName =
            EventCounterCollectorNamespace + "EventCounterCollectionModule, " + EventCounterCollectorAssembly;

        private const string RequestTypeName =
            EventCounterCollectorNamespace + "EventCounterCollectionRequest, " + EventCounterCollectorAssembly;

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
                    // Warning, not Debug. Getting here means Application Insights was detected -
                    // that is the only reason this is called - but the piece that collects
                    // EventCounters is not there, so the counters go nowhere. The 3.x SDK is the
                    // likely reason: it re-based on OpenTelemetry and removed this module, while
                    // still providing the TelemetryClient that detection looks for. Left at Debug,
                    // an operator following docs/SMOKE_TEST.md sees neither the "registered" line
                    // nor the "no telemetry system detected" line, and nothing explains why.
                    logger?.LogWarning(
                        "Application Insights is present but its EventCounterCollectionModule was not found, " +
                        "so the {EventSourceName} counters will not be collected. This is expected on the 3.x " +
                        "SDK, which replaced telemetry modules with OpenTelemetry. Use dotnet-counters to view " +
                        "them meanwhile: dotnet-counters monitor --process-id <pid> {EventSourceName}",
                        CounterNames.EventSourceName,
                        CounterNames.EventSourceName);
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

                // The delegate shape comes from the method that was found, rather than being assumed.
                // Assuming it is the other half of why this never ran: the shape was hard-coded as
                // Action<TModule, object>, which matches neither overload - the two-argument one
                // takes ApplicationInsightsServiceOptions, not object - so the invoke below threw
                // ArgumentException and was swallowed by the catch.
                var configureAction = CreateConfigureAction(
                    configureMethod.GetParameters()[1].ParameterType, moduleType, requestType, logger);

                if (configureAction == null)
                {
                    return;
                }

                // Call: services.ConfigureTelemetryModule<EventCounterCollectionModule>(configureAction)
                configureMethod.Invoke(null, new object[] { services, configureAction });

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

        /// <summary>
        /// The <c>ConfigureTelemetryModule</c> overload to call, closed over the module type.
        /// </summary>
        /// <remarks>
        /// There is more than one overload - <c>Action&lt;TModule&gt;</c> and
        /// <c>Action&lt;TModule, ApplicationInsightsServiceOptions&gt;</c> - and reflection returns
        /// them in no defined order, so one is chosen rather than taken as whichever came back
        /// first. The two-argument form, even though nothing here reads the options: the
        /// one-argument form carries <c>[Obsolete]</c> telling callers to use it instead, and an
        /// obsolete overload is the one that eventually disappears. Ordering rather than filtering
        /// so that an SDK old enough to have only the one-argument form still binds.
        /// </remarks>
        private static MethodInfo? FindConfigureTelemetryModule(Type extensionType, Type moduleType) =>
            extensionType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method =>
                    method.Name == "ConfigureTelemetryModule" &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 1 &&
                    method.GetParameters().Length == 2 &&
                    method.GetParameters()[0].ParameterType == typeof(IServiceCollection))
                .Select(method => method.MakeGenericMethod(moduleType))
                .Where(method => typeof(Delegate).IsAssignableFrom(method.GetParameters()[1].ParameterType))
                .OrderByDescending(method => method.GetParameters()[1].ParameterType.GetGenericArguments().Length)
                .FirstOrDefault();

        /// <summary>
        /// Builds the callback <c>ConfigureTelemetryModule</c> asked for, whatever shape that is.
        /// </summary>
        /// <remarks>
        /// Only the arity varies, because both overloads hand over the module and neither needs
        /// anything read back off the options. <c>CreateDelegate</c> relaxes reference-typed
        /// parameters, so a target taking <see cref="object"/> binds to a delegate taking the
        /// concrete module type without either being named at compile time.
        /// </remarks>
        private static Delegate? CreateConfigureAction(
            Type delegateType, Type moduleType, Type requestType, ILogger? logger)
        {
            Action<object> withoutOptions = module =>
                AddCountersToModule(module, moduleType, requestType, logger);

            Action<object, object> withOptions = (module, _) =>
                AddCountersToModule(module, moduleType, requestType, logger);

            switch (delegateType.GetGenericArguments().Length)
            {
                case 1:
                    return Delegate.CreateDelegate(delegateType, withoutOptions.Target, withoutOptions.Method);

                case 2:
                    return Delegate.CreateDelegate(delegateType, withOptions.Target, withOptions.Method);

                default:
                    logger?.LogWarning(
                        "ConfigureTelemetryModule expects {DelegateType}, which is not a shape this knows how to supply",
                        delegateType);
                    return null;
            }
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

            // What the module has been asked for already. The CMS and Commerce packages both
            // register telemetry and are routinely installed side by side, so this runs twice on a
            // Commerce site - against the same module, because Application Insights builds one and
            // hands it to every configurator in turn. Adding the same counter twice makes the
            // module collect and send it twice: double the telemetry volume, double the bill, and
            // aggregations that quietly read high.
            var requested = AlreadyRequested(counters, requestType);

            foreach (var counterName in EventCounterRegistry.GetAllCounterNames())
            {
                if (!requested.Add(counterName))
                {
                    continue;
                }

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

        /// <summary>
        /// The counter names already requested from our own EventSource, so a second registration
        /// adds nothing.
        /// </summary>
        /// <remarks>
        /// Read off the module rather than tracked in a static, so it stays right when the host
        /// builds more than one module, and when the site has configured some of these counters
        /// itself. Only our own EventSource is considered; what else the host collects is its
        /// business.
        /// </remarks>
        private static HashSet<string> AlreadyRequested(object counters, Type requestType)
        {
            var requested = new HashSet<string>(StringComparer.Ordinal);

            var sourceProperty = requestType.GetProperty("EventSourceName", BindingFlags.Public | BindingFlags.Instance);
            var nameProperty = requestType.GetProperty("EventCounterName", BindingFlags.Public | BindingFlags.Instance);

            if (sourceProperty == null || nameProperty == null || counters is not IEnumerable requests)
            {
                return requested;
            }

            foreach (var request in requests)
            {
                if (request != null &&
                    sourceProperty.GetValue(request) as string == CounterNames.EventSourceName &&
                    nameProperty.GetValue(request) is string counterName)
                {
                    requested.Add(counterName);
                }
            }

            return requested;
        }
    }
}
