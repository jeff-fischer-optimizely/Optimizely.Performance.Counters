using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Wires the Optimizely-Performance EventSource up to whatever telemetry system the host is
    /// running, and explains in the log what it found.
    /// <para>
    /// The CMS and Commerce initialization modules both do this, identically, so the logic lives
    /// here rather than in each of them. Registration is idempotent, so it is safe when both
    /// packages are installed side by side.
    /// </para>
    /// </summary>
    public static class TelemetryStartup
    {
        /// <summary>
        /// Detects the available telemetry systems and subscribes Application Insights to our
        /// counters when it is present.
        /// </summary>
        /// <param name="services">
        /// The container being configured, or null on CMS 11. V11 configures services through
        /// <c>IServiceConfigurationProvider</c> rather than <see cref="IServiceCollection"/>, and
        /// the Application Insights registration needs the latter - so on V11 the detection still
        /// runs and is logged, but nothing is subscribed.
        /// </param>
        /// <param name="logger">Log sink; may be null during container configuration.</param>
        public static void Configure(IServiceCollection? services, ILogger? logger)
        {
            var telemetryInfo = ApplicationInsightsBridge.DetectTelemetrySystems();
            logger?.LogInformation("Telemetry Detection: {TelemetryInfo}", telemetryInfo);

            if (telemetryInfo.ApplicationInsightsAvailable)
            {
                if (services == null)
                {
                    logger?.LogInformation(
                        "Application Insights detected - V11 AI registration not yet implemented, use EventSource directly");
                }
                else
                {
                    ApplicationInsightsRegistration.RegisterEventCounters(services, logger);
                    logger?.LogInformation(
                        "Optimizely EventCounters registered with Application Insights EventCounterCollectionModule");
                }
            }

            if (telemetryInfo.DataDogAvailable)
            {
                logger?.LogInformation(
                    "DataDog detected - {EventSourceName} EventCounters will be auto-discovered",
                    CounterNames.EventSourceName);
            }

            if (!telemetryInfo.ApplicationInsightsAvailable && !telemetryInfo.DataDogAvailable)
            {
                logger?.LogInformation(
                    "No telemetry system detected. EventCounters published to '{EventSourceName}' EventSource. " +
                    "Use dotnet-counters to view: dotnet-counters monitor --process-id <pid> {EventSourceName}",
                    CounterNames.EventSourceName,
                    CounterNames.EventSourceName);
            }
        }
    }
}
