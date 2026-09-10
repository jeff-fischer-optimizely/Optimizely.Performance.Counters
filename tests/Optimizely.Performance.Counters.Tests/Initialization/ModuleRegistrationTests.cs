using EPiServer;
using EPiServer.Framework.Cache;
using EPiServer.Commerce.Order;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.CMS.Initialization;
using Optimizely.Performance.Counters.Commerce.Decorators;
using Optimizely.Performance.Counters.Commerce.Initialization;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Initialization
{
    /// <summary>
    /// Runs the initialization modules against a real container and resolves what they registered.
    /// <para>
    /// Everything else in this suite constructs the decorators directly, which proves they measure
    /// correctly but says nothing about whether a site ever gets one. Registration is the part that
    /// differs most between majors and is checked least by the compiler, and a mistake there is
    /// invisible: the site starts, the services resolve to the undecorated Optimizely
    /// implementations, and every counter reads zero forever.
    /// </para>
    /// </summary>
    public class ModuleRegistrationTests
    {
        private static ContainerHarness ConfiguredForCms()
        {
            var harness = ContainerHarness.WithOptimizelyServicesRegistered();
            new CMSPerformanceCountersModule().ConfigureContainer(harness.Context);
            return harness;
        }

        private static ContainerHarness ConfiguredForCommerce()
        {
            var harness = ContainerHarness.WithOptimizelyServicesRegistered();
            new CommercePerformanceCountersModule().ConfigureContainer(harness.Context);
            return harness;
        }

        /// <summary>
        /// Asserts that what the container hands out is the tracker this major is meant to get.
        /// </summary>
        /// <remarks>
        /// Two answers rather than one. V12 and V13 publish to the EventSource and to a
        /// <c>Meter</c> at once, because neither path reaches every host - Application Insights
        /// SDK 3.x and <c>UseAzureMonitor()</c> collect no EventCounters, and <c>dotnet-counters</c>
        /// and the DataDog tracer find the EventSource without being told. V11 gets the EventSource
        /// alone: .NET Framework has no <c>System.Diagnostics.Metrics</c> to publish to.
        /// </remarks>
        private static void AssertRegisteredTracker(IMetricTracker tracker)
        {
#if CMS11
            Assert.IsType<EventCounterMetricTracker>(tracker);
#else
            Assert.IsType<CompositeMetricTracker>(tracker);
#endif
        }

        [Fact]
        public void The_CMS_module_registers_a_metric_tracker()
        {
            // Without this the decorators cannot be constructed at all, so every resolution below
            // would fail for a reason that has nothing to do with interception.
            AssertRegisteredTracker(ConfiguredForCms().Resolve<IMetricTracker>());
        }

        [Fact]
        public void The_CMS_module_decorates_the_content_loader()
        {
            Assert.IsType<InstrumentedContentLoader>(ConfiguredForCms().Resolve<IContentLoader>());
        }

        [Fact]
        public void The_CMS_module_decorates_the_content_repository()
        {
            Assert.IsType<InstrumentedContentRepository>(ConfiguredForCms().Resolve<IContentRepository>());
        }

        [Fact]
        public void The_CMS_module_decorates_the_cache()
        {
            Assert.IsType<InstrumentedSynchronizedObjectInstanceCache>(
                ConfiguredForCms().Resolve<ISynchronizedObjectInstanceCache>());
        }

#if CMS13
        [Fact]
        public void The_CMS_module_decorates_the_event_publisher()
        {
            // V13 only: V11 and V12 raise events through the static Event class, which has no seam.
            Assert.IsType<InstrumentedEventPublisher>(
                ConfiguredForCms().Resolve<EPiServer.Events.IEventPublisher>());
        }
#endif

        [Fact]
        public void The_Commerce_module_registers_a_metric_tracker()
        {
            // The Commerce package ships on its own, so it cannot lean on the CMS module having run.
            AssertRegisteredTracker(ConfiguredForCommerce().Resolve<IMetricTracker>());
        }

        [Fact]
        public void The_Commerce_module_decorates_the_order_repository()
        {
            Assert.IsType<InstrumentedOrderRepository>(ConfiguredForCommerce().Resolve<IOrderRepository>());
        }

        [Fact]
        public void The_Commerce_module_leaves_the_CMS_services_alone()
        {
            // A Commerce-only site must not end up with CMS decorators it never installed, and the
            // Commerce package must not need EPiServer.CMS.Core loaded to configure itself.
            Assert.IsNotType<InstrumentedContentLoader>(ConfiguredForCommerce().Resolve<IContentLoader>());
        }

        [Fact]
        public void Both_modules_can_configure_the_same_container()
        {
            // The supported install: a Commerce site with both packages. Each module registers its
            // own IMetricTracker, and TryAdd has to keep the second from displacing the first or
            // from throwing.
            var harness = ContainerHarness.WithOptimizelyServicesRegistered();

            new CMSPerformanceCountersModule().ConfigureContainer(harness.Context);
            new CommercePerformanceCountersModule().ConfigureContainer(harness.Context);

            Assert.IsType<InstrumentedContentLoader>(harness.Resolve<IContentLoader>());
            Assert.IsType<InstrumentedOrderRepository>(harness.Resolve<IOrderRepository>());
            AssertRegisteredTracker(harness.Resolve<IMetricTracker>());
        }
    }
}
