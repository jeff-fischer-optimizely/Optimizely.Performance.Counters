using EPiServer.Commerce.Order;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
#if CMS13
// V13 moved the Intercept extension out of Microsoft.Extensions.DependencyInjection into
// EPiServer.DependencyInjection.ServiceCollectionExtensions.
using EPiServer.DependencyInjection;
#endif
using Microsoft.Extensions.Logging;
#if !CMS11
using Microsoft.Extensions.DependencyInjection;
#endif
using Optimizely.Performance.Counters.Commerce.Decorators;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Shared;
using Optimizely.Performance.Counters.VersionDetection;
using static Optimizely.Performance.Counters.Shared.ModuleSupport;

namespace Optimizely.Performance.Counters.Commerce.Initialization
{
    /// <summary>
    /// Initialization module for Optimizely Commerce Performance Counters.
    /// Wraps the Commerce services that carry the counters in instrumented decorators.
    /// </summary>
    // No [ModuleDependency]: all the work happens in ConfigureContainer, which the framework
    // runs while building the container - before any module initializes.
    [InitializableModule]
    public class CommercePerformanceCountersModule : IConfigurableModule
    {
        private ILogger<CommercePerformanceCountersModule>? _logger;

        // Held separately from _logger so Initialize knows what to flush. ConfigureContainer runs
        // before the container can be resolved from, so everything it logs is buffered here and
        // replayed through the host's own logger once one exists.
        private DeferredLogger<CommercePerformanceCountersModule>? _deferredLogger;

        /// <summary>
        /// Registers the metric tracker and wraps the instrumented Commerce services. Runs while
        /// the container is being built, before any module initializes.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        public void ConfigureContainer(ServiceConfigurationContext context)
        {
            _logger = _deferredLogger = ResolveLogger<CommercePerformanceCountersModule>(context);

            _logger?.LogInformation(
                "Commerce Version Detection:\n{VersionInfo}",
                OptimizelyVersionDetector.GetDetailedVersionInfo());

            VersionCompatibility.Validate(
                OptimizelyVersionDetector.DetectVersion(),
                OptimizelyVersionDetector.GetExpectedVersion(),
                packageName: "Commerce",
                requiredAssembly: "EPiServer.Commerce.Core",
                _logger);

            ConfigureTelemetry(context, _logger);
            RegisterMetricTracker(context);
            _logger?.LogInformation("Registered IMetricTracker: EventCounterMetricTracker");

            RegisterDecorators(context);

            _logger?.LogInformation("Optimizely Commerce Performance Counters configured successfully");
        }

        /// <summary>
        /// Replays what <see cref="ConfigureContainer"/> logged, now that a container exists to
        /// resolve the host's own logger from. No registration happens here.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        public void Initialize(InitializationEngine context)
        {
            _logger = FlushLogger(_deferredLogger, context) ?? _logger;
            _deferredLogger = null;

            _logger?.LogInformation("Optimizely Commerce Performance Counters initialized");
        }

        /// <summary>
        /// No-op beyond logging. The decorators are owned by the container and torn down with it.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        public void Uninitialize(InitializationEngine context) =>
            _logger?.LogInformation("Optimizely Commerce Performance Counters uninitialized");

        private void RegisterDecorators(ServiceConfigurationContext context)
        {
            _logger?.LogInformation("Registering Commerce performance counter decorators");

            // The lambda's first argument is IServiceLocator on V11 and IServiceProvider on
            // V12/V13, so it is left implicitly typed and every resolution goes through Resolve.
            context.Services.Intercept<IOrderRepository>(
                (services, defaultImplementation) => new InstrumentedOrderRepository(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedOrderRepository>>(services)));

            _logger?.LogInformation("Registered decorators: IOrderRepository");

            // TODO: Add more decorators
            // - IPriceService / IPriceDetailService (pricing lookups)
            // - IInventoryService (stock checks)
            // - IPromotionEngine (promotion evaluation)
            // - IPaymentProcessor (payment gateway round trips)
        }
    }
}
