using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Emits the deployment events through Application Insights, by reflection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection because Core references no Application Insights package and must not start: the
    /// SDK's assembly version moves with its package version, so a compile-time reference would
    /// bind to one identity and leave every site on a different one needing a redirect. The rest of
    /// this package reaches Application Insights the same way - see
    /// <c>ApplicationInsightsRegistration</c>, whose remarks also record what happens when the
    /// reflected names are wrong and the failure is swallowed.
    /// </para>
    /// <para>
    /// A custom event, not a metric or a counter. The payload is a set of strings - names, versions,
    /// GUIDs - and an EventCounter carries a number; the standing rule that this package publishes
    /// counters and lets the host collect them is about measurements, and does not reach a
    /// deployment record that has no numeric value to aggregate.
    /// </para>
    /// </remarks>
    public sealed class ApplicationInsightsEventSink : IDeploymentEventSink
    {
        private const string TelemetryClientTypeName =
            "Microsoft.ApplicationInsights.TelemetryClient, Microsoft.ApplicationInsights";

        private const string TelemetryConfigurationTypeName =
            "Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration, Microsoft.ApplicationInsights";

        private readonly object _client;
        private readonly MethodInfo _trackEvent;
        private readonly ILogger? _logger;

        private ApplicationInsightsEventSink(object client, MethodInfo trackEvent, ILogger? logger)
        {
            _client = client;
            _trackEvent = trackEvent;
            _logger = logger;
        }

        /// <summary>
        /// Builds a sink if Application Insights is present and a client can be got hold of.
        /// </summary>
        /// <param name="serviceProvider">
        /// The built container on V12 and V13, where the host registers its own configured
        /// <c>TelemetryClient</c>. Null on V11, which has no such registration.
        /// </param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The sink, or null when Application Insights is not usable here.</returns>
        public static ApplicationInsightsEventSink? TryCreate(
            IServiceProvider? serviceProvider, ILogger? logger)
        {
            try
            {
                var clientType = Type.GetType(TelemetryClientTypeName, throwOnError: false);

                if (clientType == null)
                {
                    return null;
                }

                // TrackEvent(string, IDictionary<string,string>, IDictionary<string,double>). The
                // three-argument overload rather than the EventTelemetry one, because building an
                // EventTelemetry means reflecting over a second type and its Properties dictionary
                // for no gain - the metrics argument is passed null.
                var trackEvent = clientType.GetMethod(
                    "TrackEvent",
                    new[]
                    {
                        typeof(string),
                        typeof(IDictionary<string, string>),
                        typeof(IDictionary<string, double>),
                    });

                if (trackEvent == null)
                {
                    logger?.LogDebug(
                        "Application Insights is present but TelemetryClient.TrackEvent does not have " +
                        "the expected shape, so deployment events will go to the log instead.");

                    return null;
                }

                var client = Resolve(serviceProvider, clientType) ?? FromActiveConfiguration(clientType);

                if (client == null)
                {
                    logger?.LogDebug(
                        "Application Insights is present but no TelemetryClient could be obtained, so " +
                        "deployment events will go to the log instead.");

                    return null;
                }

                return new ApplicationInsightsEventSink(client, trackEvent, logger);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not build an Application Insights deployment event sink.");
                return null;
            }
        }

        /// <inheritdoc />
        public void Track(string eventName, IDictionary<string, string> properties)
        {
            try
            {
                _trackEvent.Invoke(_client, new object?[] { eventName, properties, null });
            }
            catch (Exception ex)
            {
                // Once per event at Debug. A telemetry pipeline that has been shut down - during a
                // recycle, say - throws here, and a heartbeat that keeps running through it must not
                // fill the log.
                _logger?.LogDebug(ex, "Could not emit the deployment event {EventName}.", eventName);
            }
        }

        /// <remarks>
        /// The host's own client, so the events carry the connection string, the role name and the
        /// sampling configuration the site has already set up. Constructing one instead would send
        /// them somewhere else, or nowhere.
        /// </remarks>
        private static object? Resolve(IServiceProvider? serviceProvider, Type clientType)
        {
            try
            {
                return serviceProvider?.GetService(clientType);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <remarks>
        /// V11's route. There is no container to resolve from, and
        /// <c>TelemetryConfiguration.Active</c> is what a .NET Framework site configures - the same
        /// static <c>ApplicationInsightsBridge</c> reads to detect Application Insights at all. It
        /// is obsolete in the 2.x SDK and still the only answer on that version.
        /// </remarks>
        private static object? FromActiveConfiguration(Type clientType)
        {
            try
            {
                var configurationType = Type.GetType(TelemetryConfigurationTypeName, throwOnError: false);

                var active = configurationType?
                    .GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?
                    .GetValue(null);

                return active == null ? null : Activator.CreateInstance(clientType, active);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
