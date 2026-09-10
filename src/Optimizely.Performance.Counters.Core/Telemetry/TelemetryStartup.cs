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
        /// the EventCounter registration needs the latter - so on V11 the detection still runs and
        /// is logged, but this package's own counters are not subscribed. The SQL connection pool
        /// counters are, because on .NET Framework they are Windows performance counters and reach
        /// Application Insights through a collector built here rather than through the container.
        /// </param>
        /// <param name="logger">Log sink; may be null during container configuration.</param>
        /// <param name="meterEnabled">
        /// Whether the counters are also being published to a meter, so the log can say so. Purely
        /// informational - the meter is created by <c>MeterMetricTracker</c>, not here, and that
        /// type does not exist on .NET Framework.
        /// </param>
        public static void Configure(
            IServiceCollection? services, ILogger? logger, bool meterEnabled = true)
        {
            var telemetryInfo = ApplicationInsightsBridge.DetectTelemetrySystems();
            logger?.LogInformation("Telemetry Detection: {TelemetryInfo}", telemetryInfo);

            if (telemetryInfo.ApplicationInsightsAvailable)
            {
                if (services == null)
                {
#if NET472
                    // V11. The EventCounter registration needs an IServiceCollection and there is
                    // none, so this package's own counters still have to be read off the EventSource
                    // directly. The SQL connection pool counters are the exception: on .NET Framework
                    // they are Windows performance counters, which are collected through a module
                    // built here rather than through the container.
                    WindowsPerformanceCounterRegistration.Register(logger);

                    // dotnet-counters is deliberately not suggested here, unlike everywhere else
                    // this message appears: it attaches over EventPipe, which is a .NET Core
                    // construct, so on .NET Framework the advice would not work.
                    logger?.LogInformation(
                        "Application Insights detected - V11 has no IServiceCollection, so the " +
                        "{EventSourceName} counters are not registered with it. Read them off the " +
                        "EventSource directly with PerfView or your own EventListener, enabling " +
                        "the source named {EventSourceName}.",
                        CounterNames.EventSourceName,
                        CounterNames.EventSourceName);
#else
                    logger?.LogInformation(
                        "Application Insights detected - V11 AI registration not yet implemented, use EventSource directly");
#endif
                }
                else
                {
                    // Deliberately no success line here. RegisterEventCounters reports its own
                    // outcome, and it reports failure by logging rather than by throwing - so an
                    // unconditional "registered" line from out here is printed just as cheerfully
                    // when nothing was registered at all. That is what the log said for as long as
                    // the registration was broken, which is part of why nobody noticed.
                    ApplicationInsightsRegistration.RegisterEventCounters(services, logger);
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

            ReportMeter(logger, meterEnabled);
        }

        /// <remarks>
        /// Said unconditionally, whatever detection found, because the meter is the path that works
        /// where detection finds nothing. Application Insights SDK 3.x, <c>UseAzureMonitor()</c> and
        /// plain OpenTelemetry all leave <see cref="ApplicationInsightsBridge"/> reporting no
        /// EventCounter collector - correctly, there is none - and an operator reading that line has
        /// no way to know their counters are still reachable unless something tells them how.
        /// </remarks>
        private static void ReportMeter(ILogger? logger, bool meterEnabled)
        {
#if NET6_0_OR_GREATER
            if (meterEnabled)
            {
                logger?.LogInformation(
                    "The same counters are also published to the '{MeterName}' meter. Collect them " +
                    "from OpenTelemetry, Azure Monitor or Application Insights SDK 3.x with " +
                    ".WithMetrics(m => m.AddMeter(\"{MeterName}\")), which is the only path that " +
                    "works once EventCounter collection is gone.",
                    CounterNames.MeterName,
                    CounterNames.MeterName);
            }
            else
            {
                logger?.LogInformation(
                    "Meter publication is switched off by configuration " +
                    "('{SectionName}:Meter:Enabled' is false), so the counters leave this process " +
                    "over the '{EventSourceName}' EventSource only.",
                    Configuration.InstrumentationOptions.SectionName,
                    CounterNames.EventSourceName);
            }
#else
            // .NET Framework has no System.Diagnostics.Metrics, so there is nothing to report and
            // nothing the operator could do about it. The setting is ignored in silence for the same
            // reason ModuleSupport ignores it: one configuration file is meant to serve all three
            // versions.
            _ = logger;
            _ = meterEnabled;
#endif
        }
    }
}
