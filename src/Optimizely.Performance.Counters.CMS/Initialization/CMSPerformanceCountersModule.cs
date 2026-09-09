using System;
using EPiServer;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
#if !CMS11
using Microsoft.Extensions.Caching.Memory;
#endif
#if CMS13
// V13 moved the Intercept extension out of Microsoft.Extensions.DependencyInjection into
// EPiServer.DependencyInjection.ServiceCollectionExtensions.
using EPiServer.DependencyInjection;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.CMS.Diagnostics;
using Optimizely.Performance.Counters.Core.Configuration;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Http;
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

        // The one probe this package owns directly. The rest are process-wide and shared with
        // Commerce, so they live behind RuntimeProbes.
        private CacheLockProbe? _cacheLockProbe;

        // Read in ConfigureContainer and used again in Initialize. Defaulted rather than nullable
        // so that a module whose ConfigureContainer somehow did not run still behaves.
        private InstrumentationOptions _options = new InstrumentationOptions();

        /// <summary>
        /// Registers the metric tracker and wraps the instrumented Optimizely services. Runs while
        /// the container is being built, before any module initializes.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        public void ConfigureContainer(ServiceConfigurationContext context)
        {
            _logger = _deferredLogger = ResolveLogger<CMSPerformanceCountersModule>(context);

            _options = LoadOptions(context, _logger);

            if (!_options.Enabled)
            {
                // Before the version check as well as before the registrations. A site that has
                // switched this package off should not be told its Optimizely version looks wrong
                // by a package that is about to do nothing.
                _logger?.LogInformation(
                    "Optimizely CMS Performance Counters are switched off by configuration " +
                    "('{SectionName}:Enabled' is false). Nothing is decorated and no probe runs.",
                    InstrumentationOptions.SectionName);
                return;
            }

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

#if !CMS11
            if (_options.Logging.Enabled)
            {
                // Registered here, not started here. The logging factory reads its providers out of
                // the container once, so the provider has to exist before the container is built;
                // it stays inert until Initialize publishes a recorder for it to count into.
                RegisterLogWriteRateProvider(context);
            }

            if (_options.Http.Enabled)
            {
                // Registered here for the same reason, and with the same consequence: the host
                // reads its startup filters while building the request pipeline, which has already
                // happened by Initialize. The middleware goes in inert.
                RegisterHttpCacheability(context, _logger);
            }

            if (_options.Cache.Cascade.Enabled)
            {
                RegisterCascadeInstrumentation(context);
            }
            else
            {
                _logger?.LogInformation(
                    "Cache dependency cascade instrumentation is switched off by configuration " +
                    "('{SectionName}:Cache:Cascade:Enabled' is false). IMemoryCache is left " +
                    "undecorated; the cache hit, miss and invalidation rates are unaffected.",
                    InstrumentationOptions.SectionName);
            }
#endif

            _logger?.LogInformation("Optimizely CMS Performance Counters configured successfully");
        }

        /// <summary>
        /// Replays what <see cref="ConfigureContainer"/> logged, now that a container exists to
        /// resolve the host's own logger from, and starts the probes. No registration happens here.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        /// <remarks>
        /// The probes start here rather than in <see cref="ConfigureContainer"/> because they are
        /// not services: nothing resolves them, they hold their own threads, and they need a built
        /// container to get a tracker out of.
        /// </remarks>
        public void Initialize(InitializationEngine context)
        {
            _logger = FlushLogger(_deferredLogger, context) ?? _logger;
            _deferredLogger = null;

            if (!_options.Enabled)
            {
                // ConfigureContainer already said why, and it said it before the container was
                // built, so this is where that line reaches the host's log.
                return;
            }

            StartRuntimeProbes(context, _options.Probes, _logger);
            StartCacheLockProbe(context);
            StartLogWriteRate(context, _options.Logging, _logger);
            StartHttpCacheability(context, _options.Http, _logger);

            _logger?.LogInformation("Optimizely CMS Performance Counters initialized");
        }

        /// <summary>
        /// Stops the probes. The decorators need no teardown - they are owned by the container and
        /// go with it - but the probes own threads, so they do.
        /// </summary>
        /// <param name="context">Initialization context supplied by the framework.</param>
        public void Uninitialize(InitializationEngine context)
        {
            RuntimeProbes.Stop();
            LogWriteRateMonitor.Stop();
            HttpCacheabilityMonitor.Stop();

            _cacheLockProbe?.Dispose();
            _cacheLockProbe = null;

            _logger?.LogInformation("Optimizely CMS Performance Counters uninitialized");
        }

        /// <remarks>
        /// Owned by this module rather than by <see cref="RuntimeProbes"/>: it reads an Optimizely
        /// internal, so it has no business running in a Commerce-only host.
        /// </remarks>
        private void StartCacheLockProbe(InitializationEngine context)
        {
            var metrics = ResolveFromEngine<IMetricTracker>(context);

            if (metrics == null)
            {
                return;
            }

            _cacheLockProbe = new CacheLockProbe(
                metrics,
                _options.Probes.CacheLock,
                ResolveFromEngine<ILoggerFactory>(context)?.CreateLogger<CacheLockProbe>());

            // Start is a no-op when the options disable the probe, so the decision stays in one
            // place - the probe - rather than being made once here and once there.
            _cacheLockProbe.Start();
        }

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

            // Intercept ISynchronizedObjectInstanceCache with instrumented decorator. The cascade
            // recorder is resolved optionally: it is only registered once the memory cache one layer
            // down has actually been decorated, so asking for it here is how this layer finds out
            // whether anything is counting entries for it.
            context.Services.Intercept<EPiServer.Framework.Cache.ISynchronizedObjectInstanceCache>(
                (services, defaultImplementation) => new InstrumentedSynchronizedObjectInstanceCache(
                    defaultImplementation,
                    Resolve<IMetricTracker>(services),
                    Resolve<ILogger<InstrumentedSynchronizedObjectInstanceCache>>(services),
                    CascadeRecorder(services)));

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

#if CMS11
        /// <remarks>
        /// Always null on V11. The cascade is counted at <c>IMemoryCache.Remove</c>, and CMS 11
        /// caches through the System.Web runtime cache, so there is no memory cache beneath its
        /// object cache to count at - the same difference that leaves the cache lock counters empty
        /// on that version.
        /// </remarks>
        private static CacheCascadeRecorder? CascadeRecorder(IServiceLocator services)
        {
            _ = services;
            return null;
        }
#else
        /// <remarks>
        /// Optional by design. A null here means the memory cache could not be decorated, and the
        /// cache decorator then measures the rates only - which is the honest outcome, because with
        /// nothing counting entries every cascade would measure as zero.
        /// </remarks>
        private static CacheCascadeRecorder? CascadeRecorder(IServiceProvider services) =>
            services.GetService<CacheCascadeRecorder>();

        /// <summary>
        /// Installs the layer that counts the entries a cascade discards.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deferred to <see cref="ServiceConfigurationContext.ConfigurationComplete"/> because
        /// <see cref="IMemoryCache"/> belongs to the host, not to us, and nothing guarantees it has
        /// been registered by the time this module configures. ConfigurationComplete runs once
        /// everything else has registered and registration is still open there, so it is the one
        /// point that cannot depend on module ordering.
        /// </para>
        /// <para>
        /// The recorder is registered after the interception rather than before it, so that a
        /// failure to decorate leaves no recorder in the container at all. That is what makes the
        /// optional resolution above meaningful rather than merely defensive.
        /// </para>
        /// </remarks>
        private void RegisterCascadeInstrumentation(ServiceConfigurationContext context)
        {
            context.ConfigurationComplete += (_, e) =>
            {
                try
                {
                    e.Services.Intercept<IMemoryCache>(
                        (services, defaultImplementation) => new InstrumentedMemoryCache(
                            defaultImplementation,
                            Resolve<CacheCascadeRecorder>(services)));

                    var cascadeOptions = _options.Cache.Cascade;

                    e.Services.AddSingleton(services => new CacheCascadeRecorder(
                        Resolve<IMetricTracker>(services),
                        cascadeOptions,
                        services.GetService<ILogger<CacheCascadeRecorder>>()));

                    _logger?.LogInformation(
                        "Cache dependency cascade instrumentation installed on IMemoryCache.");
                }
                catch (Exception ex)
                {
                    // A site with no cascade counters is a nuisance; a site that will not start is
                    // an outage. The rate counters are unaffected.
                    _logger?.LogWarning(
                        ex,
                        "Could not decorate IMemoryCache, so cache dependency cascades will not be " +
                        "measured. Cache hit rate, invalidation rate and the cache lock counters are " +
                        "unaffected, and cache behaviour is unchanged.");
                }
            };
        }
#endif
    }
}
