using System;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
#if CMS11
using EPiServer.ServiceLocation.Internal;
using StructureMap;
#else
using Microsoft.Extensions.DependencyInjection;
#endif

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// A real Optimizely container, so an initialization module can be run against it and the
    /// services it claims to decorate can actually be resolved.
    /// <para>
    /// The registration code is the one part of this library that cannot be checked by
    /// construction. <c>Intercept</c> takes a different first callback argument on every major -
    /// <see cref="IServiceLocator"/> on V11, <see cref="IServiceProvider"/> on V12 and V13 - and
    /// lives in a different namespace on V13 again. All of that compiles behind <c>#if</c>, so a
    /// wrong resolution or a missing registration surfaces only when the container is asked for
    /// the service, which is startup on a real site.
    /// </para>
    /// <para>
    /// V11 uses StructureMap and V12/V13 use <c>IServiceCollection</c>. This type hides that
    /// difference so the tests read the same on every target.
    /// </para>
    /// </summary>
    public sealed class ContainerHarness
    {
#if CMS11
        private readonly Container _container = new Container();
        private readonly StructureMapConfiguration _services;
        private IServiceLocator? _locator;
#else
        private readonly ServiceCollection _services = new ServiceCollection();
        private ServiceProvider? _provider;
#endif

        private ContainerHarness()
        {
#if CMS11
            _services = new StructureMapConfiguration(_container);
#endif
            // The decorators take Microsoft.Extensions.Logging loggers, so whatever the host does
            // for logging, the container has to be able to hand one out.
            RegisterLogging();
        }

        /// <summary>
        /// Creates a container holding a stand-in for every service the modules decorate, which is
        /// what <c>Intercept</c> needs: it wraps an existing registration and does nothing at all
        /// if there is none.
        /// </summary>
        public static ContainerHarness WithOptimizelyServicesRegistered()
        {
            var harness = new ContainerHarness();

            harness.Register(RecordingProxy.Create<EPiServer.IContentLoader>().Instance);
            harness.Register(RecordingProxy.Create<EPiServer.IContentRepository>().Instance);
            harness.Register<EPiServer.Framework.Cache.ISynchronizedObjectInstanceCache>(new StubCache());
            harness.Register(RecordingProxy.Create<EPiServer.Commerce.Order.IOrderRepository>().Instance);
#if CMS13
            harness.Register(RecordingProxy.Create<EPiServer.Events.IEventPublisher>().Instance);
#endif

            return harness;
        }

        /// <summary>
        /// The context an initialization module's <c>ConfigureContainer</c> expects.
        /// </summary>
        public ServiceConfigurationContext Context => new ServiceConfigurationContext(HostType.TestFramework, _services);

        /// <summary>
        /// Resolves a service from the configured container, exactly as the framework would.
        /// </summary>
        /// <typeparam name="TService">Service to resolve.</typeparam>
        public TService Resolve<TService>()
            where TService : notnull
        {
#if CMS11
            _locator ??= new StructureMapServiceLocator(_container);
            return _locator.GetInstance<TService>();
#else
            _provider ??= _services.BuildServiceProvider();
            return _provider.GetRequiredService<TService>();
#endif
        }

        private void Register<TService>(TService instance)
            where TService : class
        {
#if CMS11
            // Seeded through StructureMap rather than through IServiceConfigurationProvider.Add,
            // whose object overload records a returned type of System.Object - which StructureMap's
            // interception policy then refuses to attach an IContentLoader interceptor to. A real
            // site's registrations name their implementation type, so this matches what Intercept
            // meets in production.
            _container.Configure(registry => registry.For<TService>().Use(instance));
#else
            _services.AddSingleton(instance);
#endif
        }

        private void RegisterLogging()
        {
#if CMS11
            // V11 predates Microsoft.Extensions.Logging and registers nothing for it, so the open
            // generic has to be supplied here the way a V11 site would have to supply it.
            _container.Configure(registry => registry.For(typeof(ILogger<>)).Use(typeof(NullLogger<>)));
#else
            _services.AddLogging();
#endif
        }
    }
}
