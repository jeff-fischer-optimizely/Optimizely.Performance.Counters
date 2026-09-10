using System;
using System.Linq;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Optimizely.Performance.Counters.Core.Deployment;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Covers the telemetry stamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Against a real <c>TelemetryConfiguration</c> and real telemetry items, because Core reaches
    /// all of it by reflection and must keep doing so - the Application Insights assembly version
    /// tracks its package version, so a compile-time reference would bind Core to one identity. Only
    /// a real SDK can tell whether the reflected names still bind. Same reasoning as
    /// <c>ApplicationInsightsRegistrationTests</c>.
    /// </para>
    /// <para>
    /// The point of the stamp is that "did the deployment cause this" becomes a group-by on
    /// <c>requests</c> rather than a join against a guessed time range, so the tests here are mostly
    /// about the stamp landing on ordinary telemetry rather than on anything of ours.
    /// </para>
    /// </remarks>
    public class DeploymentFingerprintInitializerTests
    {
        [Fact]
        public void The_fingerprint_is_added_to_a_request()
        {
            using var configuration = NewConfiguration();

            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            Assert.NotNull(proxy);

            proxy!.Fingerprint = "0123456789abcdef";

            var request = new RequestTelemetry();

            Initialize(configuration, request);

            Assert.Equal("0123456789abcdef", request.Properties["DeploymentFingerprint"]);
        }

        [Fact]
        public void The_dimension_is_named_what_the_queries_expect()
        {
            // Spelled out here because every saved query and alert built on this feature spells it
            // out too, and no compiler is watching that.
            Assert.Equal("DeploymentFingerprint", DeploymentFingerprintProxy.PropertyName);
        }

        [Fact]
        public void Nothing_is_stamped_before_the_first_scan_has_finished()
        {
            // The initializer is attached during initialization and the scan runs afterwards on a
            // background thread. Between the two there is no fingerprint, and an empty or
            // placeholder value would be worse than no dimension at all - it would group cleanly.
            using var configuration = NewConfiguration();

            DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            var request = new RequestTelemetry();

            Initialize(configuration, request);

            Assert.False(request.Properties.ContainsKey("DeploymentFingerprint"));
        }

        [Fact]
        public void The_host_version_is_left_alone()
        {
            // The decision the DXP telemetry settled: application_Version there carries the site's
            // own release, so filling it in "when empty" would mean one column holding a release
            // number on one host and a fingerprint on another, with no way for a query to tell
            // which. The host keeps that column; this adds its own.
            using var configuration = NewConfiguration();

            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            proxy!.Fingerprint = "0123456789abcdef";

            var request = new RequestTelemetry();

            Initialize(configuration, request);

            Assert.True(string.IsNullOrEmpty(request.Context.Component.Version));
        }

        [Fact]
        public void A_version_the_host_already_set_survives()
        {
            using var configuration = NewConfiguration();

            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            proxy!.Fingerprint = "0123456789abcdef";

            var request = new RequestTelemetry();
            request.Context.Component.Version = "8.0.30";

            Initialize(configuration, request);

            Assert.Equal("8.0.30", request.Context.Component.Version);
            Assert.Equal("0123456789abcdef", request.Properties["DeploymentFingerprint"]);
        }

        [Fact]
        public void A_dimension_somebody_else_set_is_not_overwritten()
        {
            using var configuration = NewConfiguration();

            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            proxy!.Fingerprint = "0123456789abcdef";

            var request = new RequestTelemetry();
            request.Properties["DeploymentFingerprint"] = "set by the host";

            Initialize(configuration, request);

            Assert.Equal("set by the host", request.Properties["DeploymentFingerprint"]);
        }

        [Fact]
        public void Every_kind_of_telemetry_is_stamped()
        {
            // Not just requests. A dependency slowing down and an exception rate climbing are both
            // things a deployment causes, and both are answered by grouping on this dimension.
            using var configuration = NewConfiguration();

            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            proxy!.Fingerprint = "0123456789abcdef";

            var items = new ITelemetry[]
            {
                new RequestTelemetry(),
                new DependencyTelemetry(),
                new ExceptionTelemetry(new InvalidOperationException()),
                new EventTelemetry("OptiCounters.DeploymentManifest"),
                new TraceTelemetry("a log line"),
            };

            foreach (var item in items)
            {
                Initialize(configuration, item);
            }

            Assert.All(items, item => Assert.Equal(
                "0123456789abcdef",
                ((ISupportProperties)item).Properties["DeploymentFingerprint"]));
        }

        [Fact]
        public void Detaching_stops_the_stamp()
        {
            using var configuration = NewConfiguration();

            var before = configuration.TelemetryInitializers.Count;
            var proxy = DeploymentFingerprintInitializer.TryAttach(Provider(configuration), logger: null);

            Assert.Equal(before + 1, configuration.TelemetryInitializers.Count);

            DeploymentFingerprintInitializer.Detach(proxy!, Provider(configuration));

            Assert.Equal(before, configuration.TelemetryInitializers.Count);
        }

        [Fact]
        public void A_session_that_is_disposed_leaves_nothing_stamping()
        {
            using var configuration = NewConfiguration();
            var provider = Provider(configuration);
            var before = configuration.TelemetryInitializers.Count;

            using (new DeploymentSession(
                new DeploymentOptions { InventoryMode = "Never" },
                "V12",
                provider,
                logger: null,
                sink: new RecordingDeploymentEventSink()))
            {
                Assert.Equal(before + 1, configuration.TelemetryInitializers.Count);
            }

            Assert.Equal(before, configuration.TelemetryInitializers.Count);
        }

        [Fact]
        public void The_stamp_can_be_switched_off()
        {
            using var configuration = NewConfiguration();
            var provider = Provider(configuration);
            var before = configuration.TelemetryInitializers.Count;

            using var session = new DeploymentSession(
                new DeploymentOptions { InventoryMode = "Never", StampTelemetry = false },
                "V12",
                provider,
                logger: null,
                sink: new RecordingDeploymentEventSink());

            Assert.Equal(before, configuration.TelemetryInitializers.Count);
        }

        [Fact]
        public void Nothing_is_attached_when_there_is_no_configuration_to_attach_to()
        {
            // A V11 site with no Application Insights at all. It has to be silent about it, not
            // throw, because the deployment events still go to the log and are still useful there.
            var logger = new RecordingLogger();

            DeploymentFingerprintInitializer.TryAttach(new EmptyProvider(), logger);

            Assert.Empty(logger.Failures);
        }

        /// <remarks>
        /// Not <c>TelemetryConfiguration.Active</c>, which is a process-wide static: a test that
        /// mutated its initializer list would leak into every other test in the assembly. Left
        /// unconfigured, because nothing here sends anything - the initializers are invoked
        /// directly - so it needs no destination.
        /// </remarks>
        private static TelemetryConfiguration NewConfiguration() => new TelemetryConfiguration();

        private static void Initialize(TelemetryConfiguration configuration, ITelemetry item)
        {
            foreach (var initializer in configuration.TelemetryInitializers.ToList())
            {
                initializer.Initialize(item);
            }
        }

        private static IServiceProvider Provider(TelemetryConfiguration configuration) =>
            new SingleServiceProvider(configuration);

        /// <summary>Hands back one configuration, standing in for the host's container.</summary>
        private sealed class SingleServiceProvider : IServiceProvider
        {
            private readonly TelemetryConfiguration _configuration;

            internal SingleServiceProvider(TelemetryConfiguration configuration) =>
                _configuration = configuration;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(TelemetryConfiguration) ? _configuration : null;
        }

        /// <summary>A container with nothing in it.</summary>
        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }
}
