using System;
using EPiServer.ServiceLocation;
using Microsoft.Extensions.Logging;
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
        /// Resolves a logger while the container is still being built.
        /// </summary>
        /// <typeparam name="TModule">The module doing the logging, used as the log category.</typeparam>
        /// <param name="context">Container configuration context supplied by the framework.</param>
        /// <returns>
        /// A logger, or null. On V11 this is always null: <c>IServiceConfigurationProvider</c> has
        /// no <c>BuildServiceProvider</c>, so no logger exists yet. Nothing here fails silently as
        /// a result - the version and telemetry checks throw rather than log when they matter.
        /// </returns>
        internal static ILogger<TModule>? ResolveLogger<TModule>(ServiceConfigurationContext context)
        {
#if CMS11
            _ = context;
            return null;
#else
            return context.Services.BuildServiceProvider().GetService<ILogger<TModule>>();
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
