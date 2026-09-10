using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Stamps the deployment fingerprint onto every telemetry item the host sends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public only because <see cref="DispatchProxy"/> requires it - the generated proxy derives
    /// from this type into a dynamic assembly, which cannot subclass an internal one. The same
    /// constraint, for the same reason, as <c>Log4NetWriteRateAppenderProxy</c>.
    /// </para>
    /// </remarks>
    public class DeploymentFingerprintProxy : DispatchProxy
    {
        /// <summary>
        /// The dimension added to every item. Named on the events as well, so one value joins
        /// <c>requests</c>, <c>dependencies</c>, <c>exceptions</c> and
        /// <c>OptiCounters.DeploymentManifest</c>.
        /// </summary>
        public const string PropertyName = "DeploymentFingerprint";

        /// <summary>
        /// The fingerprint to stamp, or null to stamp nothing.
        /// </summary>
        /// <remarks>
        /// Settable because the initializer is attached before the fingerprint is known. The scan
        /// runs on a background thread after initialization - it reads several hundred files - and
        /// attaching only once it finished would leave every request served during startup
        /// unstamped, which is exactly the window a deployment regression shows up in.
        /// </remarks>
        public string? Fingerprint { get; set; }

        /// <summary>
        /// Reads <c>Properties</c> off an item, when the item supports properties at all.
        /// </summary>
        internal Func<object, IDictionary<string, string>?>? ReadProperties { get; set; }

        /// <summary>
        /// Dispatches <c>ITelemetryInitializer.Initialize</c>.
        /// </summary>
        /// <param name="targetMethod">The interface method Application Insights called.</param>
        /// <param name="args">Its arguments; the single telemetry item.</param>
        /// <returns>Null. The interface has one void member.</returns>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "Initialize" || args == null || args.Length == 0)
            {
                return null;
            }

            var fingerprint = Fingerprint;
            var telemetry = args[0];

            if (fingerprint == null || telemetry == null || ReadProperties == null)
            {
                return null;
            }

            try
            {
                var properties = ReadProperties(telemetry);

                // Not an overwrite. A host that has already put this dimension on an item meant
                // something by it, and this runs on every item the process sends - including ones
                // from other initializers that ran first.
                if (properties != null && !properties.ContainsKey(PropertyName))
                {
                    properties[PropertyName] = fingerprint;
                }
            }
            catch (Exception)
            {
                // This is on the path of every telemetry item the site produces. An exception here
                // would surface inside Application Insights' own send loop, and the cost of losing
                // one stamp is that one row cannot be attributed.
            }

            return null;
        }
    }

    /// <summary>
    /// Attaches <see cref="DeploymentFingerprintProxy"/> to the host's telemetry configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attached to <c>TelemetryConfiguration.TelemetryInitializers</c> rather than registered in the
    /// container. Initializers are read per item, so a list mutated after startup takes effect
    /// immediately - whereas a container registration would have to happen during
    /// <c>ConfigureContainer</c>, before the fingerprint exists, and V11 has no
    /// <c>IServiceCollection</c> to register into at all. One route covers all three majors.
    /// </para>
    /// <para>
    /// <strong>What this deliberately does not do is set <c>Component.Version</c>.</strong> The
    /// obvious design was to fill in <c>application_Version</c> when the host had left it empty.
    /// Measured against the DXP environments this is built for, the host has not left it empty: it
    /// carries the site's own release - it moved from <c>8.0.29</c> to <c>8.0.30</c> across a real
    /// deployment - and both the in-process SDK and the App Service codeless agent agree on it. A
    /// "fill it if it is blank" rule would therefore mean the column holds a release number on DXP
    /// and a fingerprint on a self-hosted site, which is worse than either: one column, two
    /// meanings, and no way for a query to tell which it is looking at. So the host keeps that
    /// column and this adds its own. Joining the two needs nothing further: the manifest event goes
    /// through the host's own <c>TelemetryClient</c>, so the SDK stamps <c>application_Version</c>
    /// onto it exactly as it does onto a request, and one row carries both values.
    /// </para>
    /// </remarks>
    public static class DeploymentFingerprintInitializer
    {
        private const string InitializerTypeName =
            "Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer, Microsoft.ApplicationInsights";

        private const string ConfigurationTypeName =
            "Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration, Microsoft.ApplicationInsights";

        // DataContracts, not Channel, where ITelemetry itself lives. The two interfaces are
        // implemented by the same classes and are always named together, which is exactly why this
        // was wrong once: a mistyped name here does not fail, it returns null, and the stamp simply
        // never appears. DeploymentFingerprintInitializerTests is what noticed.
        private const string SupportPropertiesTypeName =
            "Microsoft.ApplicationInsights.DataContracts.ISupportProperties, Microsoft.ApplicationInsights";

        /// <summary>
        /// Attaches the initializer, if Application Insights is present.
        /// </summary>
        /// <param name="serviceProvider">The built container on V12 and V13; null on V11.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>
        /// The proxy, whose <see cref="DeploymentFingerprintProxy.Fingerprint"/> the caller sets
        /// once a scan has completed, or null if nothing could be attached.
        /// </returns>
        public static DeploymentFingerprintProxy? TryAttach(
            IServiceProvider? serviceProvider, ILogger? logger)
        {
            try
            {
                var initializerType = Type.GetType(InitializerTypeName, throwOnError: false);
                var supportProperties = Type.GetType(SupportPropertiesTypeName, throwOnError: false);

                if (initializerType == null || supportProperties == null)
                {
                    return null;
                }

                var propertiesProperty = supportProperties.GetProperty(
                    "Properties", BindingFlags.Public | BindingFlags.Instance);

                if (propertiesProperty == null)
                {
                    return null;
                }

                var initializers = Initializers(serviceProvider, initializerType);

                if (initializers == null)
                {
                    logger?.LogDebug(
                        "No Application Insights TelemetryConfiguration was reachable, so the " +
                        "deployment fingerprint will not be stamped onto other telemetry.");

                    return null;
                }

                var initializer = DispatchProxyFactory.Create(
                    initializerType, typeof(DeploymentFingerprintProxy));

                var proxy = (DeploymentFingerprintProxy)initializer;

                proxy.ReadProperties = telemetry =>
                    supportProperties.IsInstanceOfType(telemetry)
                        ? propertiesProperty.GetValue(telemetry) as IDictionary<string, string>
                        : null;

                if (!initializers.Invoke("Add", initializer))
                {
                    logger?.LogDebug(
                        "The Application Insights initializer list would not accept another " +
                        "entry, so the deployment fingerprint will not be stamped onto other " +
                        "telemetry.");

                    return null;
                }

                logger?.LogInformation(
                    "The deployment fingerprint will be stamped onto telemetry as '{PropertyName}'. " +
                    "application_Version is left to the host.",
                    DeploymentFingerprintProxy.PropertyName);

                return proxy;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not attach the deployment fingerprint initializer.");
                return null;
            }
        }

        /// <summary>
        /// Removes a previously attached initializer.
        /// </summary>
        /// <param name="initializer">What <see cref="TryAttach"/> returned.</param>
        /// <param name="serviceProvider">The same provider it was attached with.</param>
        /// <remarks>
        /// Best effort. At shutdown the telemetry configuration may already have been disposed, and
        /// the caller's guarantee is that it clears the fingerprint first - an initializer with
        /// nothing to stamp does nothing whether it is still in the list or not.
        /// </remarks>
        public static void Detach(object initializer, IServiceProvider? serviceProvider)
        {
            try
            {
                var initializerType = Type.GetType(InitializerTypeName, throwOnError: false);

                if (initializerType != null)
                {
                    Initializers(serviceProvider, initializerType)?.Invoke("Remove", initializer);
                }
            }
            catch (Exception)
            {
                // See the remarks.
            }
        }

        /// <remarks>
        /// The container's configuration first, because on V12 and V13 that is the instance the
        /// host's <c>TelemetryClient</c> was built from; the static <c>Active</c> is V11's answer,
        /// and on a .NET host is frequently a second, unused configuration.
        /// </remarks>
        private static InitializerList? Initializers(
            IServiceProvider? serviceProvider, Type initializerType)
        {
            var configurationType = Type.GetType(ConfigurationTypeName, throwOnError: false);

            if (configurationType == null)
            {
                return null;
            }

            object? configuration = null;

            try
            {
                configuration = serviceProvider?.GetService(configurationType);
            }
            catch (Exception)
            {
                // Resolution failure is not fatal; the static below may still answer.
            }

            configuration ??= configurationType
                .GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);

            if (configuration == null)
            {
                return null;
            }

            var list = configurationType
                .GetProperty("TelemetryInitializers", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(configuration);

            return list == null ? null : new InitializerList(list, initializerType);
        }

        /// <summary>
        /// The host's initializer list, reached through <c>ICollection&lt;ITelemetryInitializer&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Not the non-generic <see cref="IList"/>, which would be the shorter way to write this and
        /// does not work: what the property returns is a <c>SnapshottingList&lt;T&gt;</c>, which
        /// implements the generic collection interfaces only. Casting to <see cref="IList"/> yields
        /// null against every version of the SDK, and null here is indistinguishable from a host
        /// with no Application Insights - so the stamp would simply never appear, quietly.
        /// </remarks>
        private sealed class InitializerList
        {
            private readonly object _list;
            private readonly Type _initializerType;

            internal InitializerList(object list, Type initializerType)
            {
                _list = list;
                _initializerType = initializerType;
            }

            internal bool Invoke(string methodName, object initializer)
            {
                var collectionType = typeof(ICollection<>).MakeGenericType(_initializerType);

                if (!collectionType.IsInstanceOfType(_list))
                {
                    return false;
                }

                var method = collectionType.GetMethod(methodName, new[] { _initializerType });

                if (method == null)
                {
                    return false;
                }

                method.Invoke(_list, new[] { initializer });

                return true;
            }
        }
    }
}
