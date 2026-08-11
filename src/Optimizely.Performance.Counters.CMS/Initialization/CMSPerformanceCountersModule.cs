using EPiServer;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
#if CMS13
// V13 moved the Intercept extension out of Microsoft.Extensions.DependencyInjection into
// EPiServer.DependencyInjection.ServiceCollectionExtensions.
using EPiServer.DependencyInjection;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Shared;
using Optimizely.Performance.Counters.VersionDetection;
using static Optimizely.Performance.Counters.Shared.ModuleSupport;

namespace Optimizely.Performance.Counters.CMS.Initialization
{
    /// <summary>
    /// Initialization module for Optimizely CMS Performance Counters.
    /// Registers decorator pattern for IContentLoader and IContentRepository to instrument operations.
    /// </summary>
    // No [ModuleDependency]: all the work happens in ConfigureContainer, which the framework
    // runs while building the container - before any module initializes. The obvious candidate,
    // EPiServer.Web.InitializationModule, also lives outside EPiServer.CMS.Core, so depending on
    // it would mean taking a reference on the hosting package purely for an attribute.
    [InitializableModule]
    public class CMSPerformanceCountersModule : IConfigurableModule
    {
        private ILogger<CMSPerformanceCountersModule>? _logger;

        // Held separately from _logger so Initialize knows what to flush. ConfigureContainer runs
        // before the container can be resolved from, so everything it logs is buffered here and
        // replayed through the host's own logger once one exists.
        private DeferredLogger<CMSPerformanceCountersModule>? _deferredLogger;

        /// <summary>
        /// Registers the metric tracker and wraps the instrumented Optimizely services. Runs while
        /// the container is being built, before any module initializes.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        public void ConfigureContainer(ServiceConfigurationContext context)
        {
            _logger = _deferredLogger = ResolveLogger<CMSPerformanceCountersModule>(context);

            // Optimizely.Performance.DotNetCounters is a hard dependency of the Core package, so
            // if this module loaded at all, it is present. There is nothing to validate.
            _logger?.LogInformation(
                "CMS Version Detection:\n{VersionInfo}",
                OptimizelyVersionDetector.GetDetailedVersionInfo());

            VersionCompatibility.Validate(
                OptimizelyVersionDetector.DetectVersion(),
                OptimizelyVersionDetector.GetExpectedVersion(),
                packageName: "CMS",
                requiredAssembly: "EPiServer.CMS.Core",
                _logger);

            ConfigureTelemetry(context, _logger);
            RegisterMetricTracker(context);
            _logger?.LogInformation("Registered IMetricTracker: EventCounterMetricTracker");

            RegisterDecorators(context);

            _logger?.LogInformation("Optimizely CMS Performance Counters configured successfully");
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

            _logger?.LogInformation("Optimizely CMS Performance Counters initialized");
        }

        /// <summary>
        /// No-op beyond logging. The decorators are owned by the container and torn down with it.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        public void Uninitialize(InitializationEngine context) =>
            _logger?.LogInformation("Optimizely CMS Performance Counters uninitialized");

        private void RegisterDecorators(ServiceConfigurationContext context)
        {
            _logger?.LogInformation("Registering CMS performance counter decorators");

            // IContentLoader, IContentRepository and ISynchronizedObjectInstanceCache are
            // present in every supported major, so they are registered unconditionally.
            // IEventPublisher is a V13 addition - V11 and V12 raise events through the static
            // Event class, which has no seam to decorate.
            //
            // The lambda's first argument is IServiceLocator on V11 and IServiceProvider on
            // V12/V13, so it is left implicitly typed and every resolution goes through Resolve
            // rather than calling GetInstance or GetRequiredService directly.
            context.Services.Intercept<IContentLoader>(
                (services, defaultImplementation) => new InstrumentedContentLoader(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedContentLoader>>(services)));

            // Intercept IContentRepository with instrumented decorator
            context.Services.Intercept<IContentRepository>(
                (services, defaultImplementation) => new InstrumentedContentRepository(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedContentRepository>>(services)));

            // Intercept ISynchronizedObjectInstanceCache with instrumented decorator
            context.Services.Intercept<EPiServer.Framework.Cache.ISynchronizedObjectInstanceCache>(
                (services, defaultImplementation) => new InstrumentedSynchronizedObjectInstanceCache(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedSynchronizedObjectInstanceCache>>(services)));

#if CMS13
            // Intercept IEventPublisher with instrumented decorator (V13 only)
            context.Services.Intercept<EPiServer.Events.IEventPublisher>(
                (services, defaultImplementation) => new InstrumentedEventPublisher(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedEventPublisher>>(services)));

            _logger?.LogInformation(
                "Registered decorators: IContentLoader, IContentRepository, ISynchronizedObjectInstanceCache, IEventPublisher");
#else
            _logger?.LogInformation(
                "Registered decorators: IContentLoader, IContentRepository, ISynchronizedObjectInstanceCache. " +
                "IEventPublisher is V13-only and was not registered.");
#endif

            // TODO: Add more decorators
            // - IClient (Find search tracking - V11/V12 only)
            // - Graph client (V13 only)
        }
    }
}
