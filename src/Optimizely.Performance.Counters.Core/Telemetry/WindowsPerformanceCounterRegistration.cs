#if NET472
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Registers the ADO.NET connection pool counters with Application Insights on .NET Framework,
    /// where they are Windows performance counters rather than EventCounters.
    /// <para>
    /// The counterpart to <see cref="ApplicationInsightsRegistration"/>, which cannot serve V11: it
    /// needs an <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/> to hang a
    /// module configurator on, and V11 configures services through <c>IServiceConfigurationProvider</c>
    /// instead. There is no equivalent seam, so this builds the collector itself and initializes it
    /// against <c>TelemetryConfiguration.Active</c> - the .NET Framework SDK's process-wide
    /// singleton, which is how a V11 site's Application Insights is configured in the first place.
    /// </para>
    /// <para>
    /// Only the pool counters. The CLR, IIS and process counters a V11 site wants are the business
    /// of Optimizely.Performance.DotNetCounters, which collects them through this same module and is
    /// already a dependency of this package. Anything registered here that is also registered there
    /// would be collected and billed twice, which is why this list stops where it does.
    /// </para>
    /// <para>
    /// Bound entirely by reflection, for the same reason the rest of this namespace is: Application
    /// Insights is optional, and a site without it has to be able to load Core without the AI
    /// assemblies present. Every step fails by logging and returning rather than by throwing.
    /// </para>
    /// </summary>
    public static class WindowsPerformanceCounterRegistration
    {
        private const string PerfCounterCollectorNamespace =
            "Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.";

        /// <summary>
        /// Where the collector lives, in the order to try.
        /// </summary>
        /// <remarks>
        /// <c>Microsoft.AI.PerfCounterCollector</c> first. The NuGet package is called
        /// Microsoft.ApplicationInsights.PerfCounterCollector and the assembly inside it is not -
        /// the same mismatch that stopped the EventCounter registration from ever running, described
        /// at length on <see cref="ApplicationInsightsRegistration"/>. The package name is tried
        /// second anyway, because being wrong about which one an old SDK used costs nothing here.
        /// </remarks>
        private static readonly string[] ModuleTypeNames =
        {
            PerfCounterCollectorNamespace + "PerformanceCollectorModule, Microsoft.AI.PerfCounterCollector",
            PerfCounterCollectorNamespace + "PerformanceCollectorModule, Microsoft.ApplicationInsights.PerfCounterCollector",
        };

        private static readonly string[] RequestTypeNames =
        {
            PerfCounterCollectorNamespace + "PerformanceCounterCollectionRequest, Microsoft.AI.PerfCounterCollector",
            PerfCounterCollectorNamespace + "PerformanceCounterCollectionRequest, Microsoft.ApplicationInsights.PerfCounterCollector",
        };

        private const string TelemetryConfigurationTypeName =
            "Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration, Microsoft.ApplicationInsights";

        private static readonly object Gate = new object();

        private static bool _registered;

        /// <summary>
        /// Builds a performance counter collector for the pool counters and starts it against the
        /// active telemetry configuration.
        /// </summary>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns><c>true</c> when a collector is running, including when one already was.</returns>
        /// <remarks>
        /// Idempotent, and it has to be: the CMS and Commerce packages both configure telemetry and
        /// are routinely installed side by side. Unlike the EventCounter path there is no shared
        /// module to read the existing requests back off - each call would build its own collector -
        /// so the guard is a static rather than a duplicate check.
        /// </remarks>
        public static bool Register(ILogger? logger = null)
        {
            lock (Gate)
            {
                if (_registered)
                {
                    return true;
                }

                try
                {
                    var moduleType = FindFirstType(ModuleTypeNames);

                    if (moduleType == null)
                    {
                        // Warning, not Debug, on the same reasoning as the EventCounter registration:
                        // getting here means Application Insights was detected, so the counters were
                        // expected to go somewhere and are not.
                        logger?.LogWarning(
                            "Application Insights is present but its PerformanceCollectorModule was not found, " +
                            "so the SQL connection pool counters will not be collected. Install the " +
                            "Microsoft.ApplicationInsights.PerfCounterCollector package to enable them.");
                        return false;
                    }

                    var module = Activator.CreateInstance(moduleType);

                    if (module == null)
                    {
                        logger?.LogWarning("PerformanceCollectorModule could not be constructed");
                        return false;
                    }

                    var added = AddCounters(module, logger);

                    if (!InitializeModule(module, moduleType, logger))
                    {
                        return false;
                    }

                    _registered = true;

                    logger?.LogInformation(
                        "Registered {Count} SQL connection pool performance counters with Application Insights " +
                        "(detail counters {DetailState}).",
                        added,
                        SqlClientCounters.IsDetailEnabled() ? "enabled" : "off");

                    return true;
                }
                catch (Exception ex)
                {
                    logger?.LogError(
                        ex,
                        "Failed to register SQL connection pool performance counters with Application Insights");
                    return false;
                }
            }
        }

        /// <summary>
        /// Adds the pool counters to a performance counter collector the caller already has.
        /// </summary>
        /// <param name="performanceCollectorModule">
        /// An Application Insights <c>PerformanceCollectorModule</c>. Typed as
        /// <see cref="object"/> because naming it would put a hard dependency on Application
        /// Insights into an assembly that must load without it.
        /// </param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>How many counters were added.</returns>
        /// <remarks>
        /// Public so a site that builds its own collector can add these to it rather than ending up
        /// with two, and so the reflection above can be exercised against a real module - which is
        /// the only thing that can tell whether it still binds.
        /// </remarks>
        public static int AddCounters(object performanceCollectorModule, ILogger? logger = null)
        {
            if (performanceCollectorModule == null)
            {
                throw new ArgumentNullException(nameof(performanceCollectorModule));
            }

            var requestType = FindFirstType(RequestTypeNames);

            if (requestType == null)
            {
                logger?.LogWarning("PerformanceCounterCollectionRequest type not found");
                return 0;
            }

            var counters = performanceCollectorModule
                .GetType()
                .GetProperty("Counters", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(performanceCollectorModule);

            if (counters == null)
            {
                logger?.LogWarning("PerformanceCollectorModule.Counters is null");
                return 0;
            }

            var addMethod = counters.GetType().GetMethod("Add");

            if (addMethod == null)
            {
                logger?.LogWarning("Counters.Add method not found");
                return 0;
            }

            // Resolved once. The instance name cannot change while the process lives, and building
            // it involves reflection over the entry assembly.
            var instanceName = SqlClientCounters.ResolveInstanceName();
            var alreadyRequested = AlreadyRequested(counters, requestType);
            var added = 0;

            foreach (var counter in SqlClientCounters.GetWindowsCounters())
            {
                var path = counter.CounterPath.Replace(SqlClientCounters.InstanceNameToken, instanceName);

                if (!alreadyRequested.Add(path))
                {
                    continue;
                }

                try
                {
                    // new PerformanceCounterCollectionRequest(performanceCounter, reportAs)
                    var request = Activator.CreateInstance(requestType, path, counter.ReportAs);
                    addMethod.Invoke(counters, new[] { request });
                    added++;
                }
                catch (Exception ex)
                {
                    // Not every counter is available on every host, and one that is missing must not
                    // take the rest with it.
                    logger?.LogDebug(ex, "Failed to add performance counter {CounterPath}", path);
                }
            }

            return added;
        }

        /// <summary>
        /// Starts the collector against the process-wide telemetry configuration.
        /// </summary>
        private static bool InitializeModule(object module, Type moduleType, ILogger? logger)
        {
            var configurationType = FindFirstType(TelemetryConfigurationTypeName);

            var active = configurationType
                ?.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);

            if (active == null)
            {
                logger?.LogWarning(
                    "Application Insights TelemetryConfiguration.Active is not available, so the SQL " +
                    "connection pool counters were prepared but never started.");
                return false;
            }

            var initialize = moduleType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    method.Name == "Initialize" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType.IsInstanceOfType(active));

            if (initialize == null)
            {
                logger?.LogWarning("PerformanceCollectorModule.Initialize(TelemetryConfiguration) was not found");
                return false;
            }

            initialize.Invoke(module, new[] { active });
            return true;
        }

        /// <summary>
        /// The counter paths the collector has been asked for already, so a caller's own list is not
        /// duplicated.
        /// </summary>
        private static HashSet<string> AlreadyRequested(object counters, Type requestType)
        {
            var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var pathProperty = requestType.GetProperty(
                "PerformanceCounter", BindingFlags.Public | BindingFlags.Instance);

            if (pathProperty == null || counters is not IEnumerable requests)
            {
                return requested;
            }

            foreach (var request in requests)
            {
                if (request != null && pathProperty.GetValue(request) is string path)
                {
                    requested.Add(path);
                }
            }

            return requested;
        }

        /// <summary>
        /// The first of the given assembly-qualified names that resolves, or null if none do.
        /// Application Insights is an optional dependency, so a miss is an ordinary outcome rather
        /// than an error.
        /// </summary>
        private static Type? FindFirstType(params string[] assemblyQualifiedNames) =>
            assemblyQualifiedNames
                .Select(name => Type.GetType(name, throwOnError: false))
                .FirstOrDefault(type => type != null);
    }
}
#endif
