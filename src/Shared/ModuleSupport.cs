using System;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Configuration;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Http;
using Optimizely.Performance.Counters.Core.Telemetry;
#if !CMS11
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif

namespace Optimizely.Performance.Counters.Shared
{
    /// <summary>
    /// The parts of an initialization module that differ by Optimizely major rather than by
    /// package. The CMS and Commerce modules do all of this identically, so it is written once
    /// here and compiled into both assemblies as a linked source file.
    /// <para>
    /// Linked source rather than a shared type in Core, because every member below touches an
    /// EPiServer type. Core deliberately takes no dependency on EPiServer, which is what lets it
    /// be referenced by both packages without dragging CMS assemblies into a Commerce-only host.
    /// The type is internal, so compiling it into two assemblies cannot produce an ambiguous
    /// reference for a consumer that installs both packages.
    /// </para>
    /// </summary>
    internal static class ModuleSupport
    {
        /// <summary>
        /// Supplies a logger for the container-configuration phase, before a container exists to
        /// resolve one from.
        /// </summary>
        /// <typeparam name="TModule">The module doing the logging, used as the log category.</typeparam>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <returns>
        /// A buffering logger whose contents <see cref="FlushLogger{TModule}"/> replays once the
        /// container is built, or null on V11. On V11 <c>IServiceConfigurationProvider</c> exposes
        /// no service collection to log through at all; nothing fails silently as a result,
        /// because the version and telemetry checks throw rather than log when they matter.
        /// </returns>
        internal static DeferredLogger<TModule>? ResolveLogger<TModule>(ServiceConfigurationContext context)
        {
            _ = context;
#if CMS11
            return null;
#else
            // Deliberately not context.Services.BuildServiceProvider(). See DeferredLogger for what
            // that cost, and why nothing here resolves anything until Initialize.
            return new DeferredLogger<TModule>();
#endif
        }

        /// <summary>
        /// Replays what <see cref="ResolveLogger{TModule}"/> buffered, and hands back the real
        /// logger for the rest of the module's life.
        /// </summary>
        /// <typeparam name="TModule">The module doing the logging, used as the log category.</typeparam>
        /// <param name="deferred">The buffering logger, or null.</param>
        /// <param name="context">Initialization context, holding the built container.</param>
        /// <returns>
        /// The host's configured logger, or <paramref name="deferred"/> unchanged if none could be
        /// resolved. Never throws: a module must not take a site down over its own logging.
        /// </returns>
        internal static ILogger<TModule>? FlushLogger<TModule>(
            DeferredLogger<TModule>? deferred,
            InitializationEngine context)
        {
            if (deferred == null)
            {
                return null;
            }

            try
            {
                // V13 added InitializationEngine.Services and marked Locate obsolete in the same
                // release; V12 has only Locate. Both reach the same container.
#if CMS13
                var logger = context.Services.GetRequiredService<ILogger<TModule>>();
#else
                var logger = context.Locate.Advanced.GetInstance<ILogger<TModule>>();
#endif
                deferred.FlushTo(logger);
                return logger;
            }
            catch (Exception)
            {
                // A host with no logging registered is unusual but not our business to object to.
                // The buffer is dropped and the module carries on logging into it harmlessly.
                return deferred;
            }
        }

        /// <summary>
        /// Reads the instrumentation options out of the host's configuration.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The bound options, or the defaults if there was nothing to bind from.</returns>
        /// <remarks>
        /// Called at the top of <c>ConfigureContainer</c>, before anything is registered, because
        /// the first thing the options can say is that none of it should be. Both modules call this
        /// and each gets its own instance; they read the same file, so the values agree, and the
        /// alternative - one module publishing options the other reads - would make the behaviour
        /// of a Commerce site depend on whether the CMS package happens to be installed.
        /// </remarks>
        internal static InstrumentationOptions LoadOptions(
            ServiceConfigurationContext context, ILogger? logger)
        {
#if CMS11
            // V11 has no IServiceCollection to find an IConfiguration in. The loader reads
            // appSettings there instead and ignores what it is passed.
            _ = context;
            return InstrumentationConfiguration.Load(null, logger);
#else
            return InstrumentationConfiguration.Load(context.Services, logger);
#endif
        }

        /// <summary>
        /// Detects the host's telemetry systems and subscribes Application Insights to our
        /// counters. Bridges the V11/V12+ difference in how services are exposed.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <param name="logger">Log sink; may be null.</param>
        internal static void ConfigureTelemetry(ServiceConfigurationContext context, ILogger? logger)
        {
#if CMS11
            // V11 exposes IServiceConfigurationProvider, not IServiceCollection, so the Application
            // Insights registration has nothing to attach to. Detection still runs and is logged.
            _ = context;
            TelemetryStartup.Configure(null, logger);
#else
            TelemetryStartup.Configure(context.Services, logger);
#endif
        }

        /// <summary>
        /// Registers the EventCounter-based metric tracker, which works with Application Insights,
        /// DataDog and dotnet-counters alike.
        /// <para>
        /// TryAdd rather than Add: the CMS and Commerce packages both register this tracker and
        /// are routinely installed side by side. A second registration would create a second
        /// instance writing to the same EventSource for no benefit.
        /// </para>
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        internal static void RegisterMetricTracker(ServiceConfigurationContext context)
        {
#if CMS11
            context.Services.TryAdd<IMetricTracker>(
                locator => new EventCounterMetricTracker(), ServiceInstanceScope.Singleton);
#else
            context.Services.TryAddSingleton<IMetricTracker, EventCounterMetricTracker>();
#endif
        }

        /// <summary>
        /// Resolves a service from the built container, once one exists.
        /// </summary>
        /// <typeparam name="T">Service to resolve.</typeparam>
        /// <param name="context">Initialization context, holding the built container.</param>
        /// <returns>The service, or null if it could not be resolved.</returns>
        /// <remarks>
        /// For things a module needs during <c>Initialize</c> rather than during registration - the
        /// probes, which have to be handed a tracker and a logger factory. Returns null rather than
        /// throwing, on the same reasoning as <see cref="FlushLogger{TModule}"/>: no counter is
        /// worth a site that will not start.
        /// </remarks>
        internal static T? ResolveFromEngine<T>(InitializationEngine context)
            where T : class
        {
            try
            {
                // V13 added InitializationEngine.Services and marked Locate obsolete in the same
                // release; V11 and V12 have only Locate. Both reach the same container.
#if CMS13
                return context.Services.GetService(typeof(T)) as T;
#else
                return context.Locate.Advanced.GetInstance<T>();
#endif
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Starts the process-wide runtime probes, if nothing has started them already.
        /// </summary>
        /// <param name="context">Initialization context, holding the built container.</param>
        /// <param name="options">Probe options, as read from configuration.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <remarks>
        /// Called from both the CMS and the Commerce module. <see cref="RuntimeProbes"/> is the
        /// thing that makes the second call a no-op, so neither module has to know whether the
        /// other is installed.
        /// </remarks>
        internal static void StartRuntimeProbes(
            InitializationEngine context, ProbeOptions options, ILogger? logger)
        {
            var metrics = ResolveFromEngine<IMetricTracker>(context);

            if (metrics == null)
            {
                logger?.LogInformation(
                    "Runtime probes were not started: no IMetricTracker could be resolved.");
                return;
            }

            if (RuntimeProbes.Start(metrics, options, ResolveFromEngine<ILoggerFactory>(context)))
            {
                // Named individually rather than as "the runtime probes", because configuration can
                // now switch them off one at a time and a log line that says three started when two
                // did is worse than no line at all.
#if CMS11
                logger?.LogInformation(
                    "Runtime probes started: thread pool {ThreadPool}. The GC pause and lock " +
                    "contention probes need runtime APIs .NET Framework does not have, so they " +
                    "never run on this version whatever they are configured to.",
                    Started(options.ThreadPool.Enabled));
#else
                logger?.LogInformation(
                    "Runtime probes started: thread pool {ThreadPool}, GC pause {Gc}, " +
                    "lock contention {Contention}.",
                    Started(options.ThreadPool.Enabled),
                    Started(options.GarbageCollection.Enabled),
                    Started(options.Contention.Enabled));
#endif
            }
        }

        private static string Started(bool enabled) => enabled ? "on" : "off";

#if !CMS11
        /// <summary>
        /// Registers the logging provider that counts writes on V12 and V13.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <remarks>
        /// <para>
        /// A provider has to be registered here rather than started in <c>Initialize</c>, because
        /// the logging factory reads its providers out of the container once and never asks again.
        /// The counting itself only begins when <see cref="StartLogWriteRate"/> publishes a
        /// recorder; until then the provider is registered and inert.
        /// </para>
        /// <para>
        /// TryAddEnumerable rather than AddSingleton: it dedupes by implementation type, which is
        /// exactly the case where the CMS and Commerce packages are installed side by side and both
        /// register the same provider. Two providers would count every write twice.
        /// </para>
        /// </remarks>
        internal static void RegisterLogWriteRateProvider(ServiceConfigurationContext context)
        {
            // Qualified: EPiServer.ServiceLocation has a ServiceDescriptor of its own, and both
            // namespaces are in scope here.
            context.Services.TryAddEnumerable(
                Microsoft.Extensions.DependencyInjection.ServiceDescriptor
                    .Singleton<ILoggerProvider, LogWriteRateLoggerProvider>());
        }
#endif

        /// <summary>
        /// Starts the log write rate counters, if nothing has started them already.
        /// </summary>
        /// <param name="context">Initialization context, holding the built container.</param>
        /// <param name="options">Log write rate options, as read from configuration.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <remarks>
        /// Called from both modules, and idempotent for the same reason
        /// <see cref="StartRuntimeProbes"/> is: <see cref="LogWriteRateMonitor"/> makes the second
        /// call a no-op, so neither module has to know whether the other is installed.
        /// </remarks>
        internal static void StartLogWriteRate(
            InitializationEngine context, LogWriteRateOptions options, ILogger? logger)
        {
            if (!options.Enabled)
            {
                return;
            }

            var metrics = ResolveFromEngine<IMetricTracker>(context);

            if (metrics == null)
            {
                logger?.LogInformation(
                    "Log write rate counters were not started: no IMetricTracker could be resolved.");
                return;
            }

            LogWriteRateMonitor.Start(metrics, options, logger);
        }

#if !CMS11
        /// <summary>
        /// Registers the startup filter that measures outbound response cacheability on V12 and
        /// V13.
        /// </summary>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <remarks>
        /// Registered here rather than started in <c>Initialize</c> for the same reason the logging
        /// provider is: the host reads its startup filters out of the container while it builds the
        /// request pipeline, and by <c>Initialize</c> that has happened. The middleware is in place
        /// and inert until <see cref="StartHttpCacheability"/> publishes a recorder.
        /// <para>
        /// The de-duplication that keeps a site with both packages from counting every response
        /// twice is inside <c>HttpCacheabilityRegistration</c>, along with the ASP.NET Core types -
        /// neither the CMS nor the Commerce assembly references those, and neither needs to.
        /// </para>
        /// </remarks>
        internal static void RegisterHttpCacheability(
            ServiceConfigurationContext context, ILogger? logger) =>
            HttpCacheabilityRegistration.Register(context.Services, logger);
#endif

        /// <summary>
        /// Starts the outbound response cacheability counters, if nothing has started them already.
        /// </summary>
        /// <param name="context">Initialization context, holding the built container.</param>
        /// <param name="options">Cacheability options, as read from configuration.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <remarks>
        /// Called from both modules, and idempotent for the same reason
        /// <see cref="StartLogWriteRate"/> is: <see cref="HttpCacheabilityMonitor"/> makes the
        /// second call a no-op, so neither module has to know whether the other is installed.
        /// </remarks>
        internal static void StartHttpCacheability(
            InitializationEngine context, HttpCacheabilityOptions options, ILogger? logger)
        {
            if (!options.Enabled)
            {
                return;
            }

            var metrics = ResolveFromEngine<IMetricTracker>(context);

            if (metrics == null)
            {
                logger?.LogInformation(
                    "Response cacheability counters were not started: no IMetricTracker could be " +
                    "resolved.");
                return;
            }

            HttpCacheabilityMonitor.Start(metrics, options, logger);
        }

#if CMS11
        /// <summary>
        /// Resolves a dependency for a decorator. V11 hands the interception callback an
        /// <c>IServiceLocator</c>; V12 and V13 hand it an <c>IServiceProvider</c>. Keeping the
        /// difference here lets the decorator registrations stay version-neutral.
        /// </summary>
        /// <typeparam name="T">Service to resolve.</typeparam>
        /// <param name="services">The resolver handed to the interception callback.</param>
        /// <returns>The resolved service.</returns>
        internal static T Resolve<T>(IServiceLocator services) => services.GetInstance<T>();
#else
        /// <summary>
        /// Resolves a dependency for a decorator. V11 hands the interception callback an
        /// <c>IServiceLocator</c>; V12 and V13 hand it an <c>IServiceProvider</c>. Keeping the
        /// difference here lets the decorator registrations stay version-neutral.
        /// </summary>
        /// <typeparam name="T">Service to resolve.</typeparam>
        /// <param name="services">The resolver handed to the interception callback.</param>
        /// <returns>The resolved service.</returns>
        internal static T Resolve<T>(IServiceProvider services)
            where T : notnull =>
            services.GetRequiredService<T>();
#endif
    }
}
