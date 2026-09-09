#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ApplicationInsights.Extensibility.EventCounterCollector;
using Microsoft.Extensions.DependencyInjection;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Whether <see cref="ApplicationInsightsRegistration"/> still binds to Application Insights.
    /// <para>
    /// It binds entirely by reflection, on purpose: Application Insights is optional, and a site
    /// without it has to be able to load Core without the AI assemblies present. The cost is that
    /// every part of the binding is a string resolved at runtime, and each one fails by logging and
    /// returning rather than by throwing - so a binding that has stopped working is indistinguishable
    /// from a host that simply has no Application Insights, and the compiler cannot see either.
    /// Only a real <see cref="EventCounterCollectionModule"/> can tell the difference, which is what
    /// these tests put in front of it.
    /// </para>
    /// <para>
    /// This is Stage 4 of docs/SMOKE_TEST.md as far as it can be taken without an Azure resource. It
    /// stops at the point the counters are handed to the collection module; that the module then
    /// ships them to Azure is Microsoft's code and Microsoft's problem.
    /// </para>
    /// <para>
    /// Not compiled on net472. V11 exposes no <see cref="IServiceCollection"/>, so there is no
    /// registration path there to test - see the gap noted in docs/SMOKE_TEST.md.
    /// </para>
    /// </summary>
    public class ApplicationInsightsRegistrationTests
    {
        /// <summary>
        /// Matched by name rather than by type. <c>ITelemetryModuleConfigurator</c> is how the
        /// Application Insights DI extensions record a pending module configuration, but it sits in
        /// a namespace that moved between the 2.x and 3.x packages, and the tests run against both.
        /// </summary>
        private const string ConfiguratorInterface = "ITelemetryModuleConfigurator";

        [Fact]
        public void Application_insights_is_detected_when_it_is_present()
        {
            var detected = ApplicationInsightsBridge.DetectTelemetrySystems();

            Assert.True(
                detected.ApplicationInsightsAvailable,
                "Application Insights is referenced by this test project, so detection missing it " +
                "means the type name it probes for no longer resolves. Detection gates the whole " +
                "registration: TelemetryStartup does not even call it when this is false. Got: " +
                detected);

            Assert.NotNull(detected.ApplicationInsightsVersion);
        }

        /// <summary>
        /// The one that matters: every counter the registry knows about is actually requested from
        /// the collection module, under the exact name it is published as.
        /// </summary>
        /// <remarks>
        /// Application Insights subscribes by exact (source, counter) pair and silently collects
        /// nothing for a pair that does not match, so a name that drifts on either side of this
        /// hand-off produces an empty metric rather than an error - the same failure that
        /// <c>Name[Operation=Get]</c> caused, one layer further out.
        /// </remarks>
        [Fact]
        public void Every_counter_in_the_registry_is_requested_from_the_module()
        {
            var registered = Register(times: 1);

            var requested = registered.Module.Counters
                .Where(request => request.EventSourceName == CounterNames.EventSourceName)
                .Select(request => request.EventCounterName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var expected = EventCounterRegistry.GetAllCounterNames()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected, requested);
        }

        /// <summary>
        /// The connection pool counters come from SqlClient's own event source, so nothing else in
        /// this package would notice if they stopped being asked for.
        /// </summary>
        [Fact]
        public void Every_sql_client_pool_counter_is_requested_from_the_module()
        {
            var registered = Register(times: 1);

            var requested = registered.Module.Counters
                .Where(request => request.EventSourceName == SqlClientCounters.EventSourceName)
                .Select(request => request.EventCounterName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var expected = SqlClientCounters.All
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected, requested);
        }

        /// <summary>
        /// A site already collecting SqlClient's counters must not end up collecting them twice.
        /// </summary>
        /// <remarks>
        /// Registering counters from somebody else's event source is what makes this reachable.
        /// While this only ever asked for its own, a collision could only come from installing both
        /// packages; now it can come from the site's own configuration.
        /// </remarks>
        [Fact]
        public void A_counter_the_site_already_collects_is_not_requested_again()
        {
            var alreadyCollected = SqlClientCounters.All[0];

            var registered = Register(
                times: 1,
                new EventCounterCollectionRequest(SqlClientCounters.EventSourceName, alreadyCollected));

            Assert.Single(
                registered.Module.Counters,
                request => request.EventSourceName == SqlClientCounters.EventSourceName &&
                           request.EventCounterName == alreadyCollected);
        }

        [Fact]
        public void The_registration_reaches_the_collection_module_at_all()
        {
            var registered = Register(times: 1);

            Assert.True(
                registered.ConfiguratorsApplied > 0,
                "Nothing was registered against EventCounterCollectionModule. Every step of the " +
                "binding fails by logging and returning, so the log is the diagnosis:" +
                Environment.NewLine + registered.Log.Transcript());
        }

        /// <summary>
        /// Registration reports failure by logging it, so a silent log is part of the contract.
        /// </summary>
        /// <remarks>
        /// <c>RegisterEventCounters</c> catches everything and logs it, and
        /// <see cref="TelemetryStartup"/> then announces success regardless of what happened. An
        /// operator following Stage 2 of docs/SMOKE_TEST.md reads that success line, so anything
        /// logged at Error here is a line the log contradicts a few entries later.
        /// </remarks>
        [Fact]
        public void The_registration_reports_no_failure()
        {
            var registered = Register(times: 1);

            Assert.False(
                registered.Log.Failures.Any(),
                "Registration logged a failure:" + Environment.NewLine + registered.Log.Transcript());
        }

        /// <summary>
        /// The CMS and Commerce packages both register telemetry and are routinely installed side by
        /// side, which docs/SMOKE_TEST.md Stage 5 says is safe. Both call this on the same container.
        /// </summary>
        [Fact]
        public void Registering_twice_does_not_request_every_counter_twice()
        {
            var registered = Register(times: 2);

            var duplicated = registered.Module.Counters
                .GroupBy(request => request.EventSourceName + "/" + request.EventCounterName, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key + " x" + group.Count())
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                duplicated.Count == 0,
                "Installing both packages asks Application Insights to collect the same counters " +
                "more than once: " + string.Join(", ", duplicated));
        }

        /// <summary>
        /// Runs the registration against a fresh container and applies whatever it left behind to a
        /// real <see cref="EventCounterCollectionModule"/>, the way Application Insights does at
        /// startup when it builds its telemetry configuration.
        /// </summary>
        /// <param name="times">
        /// How many packages are doing the registering. Two is the Commerce site case.
        /// </param>
        /// <param name="alreadyCollected">
        /// Counters the site configured for itself before this package ran.
        /// </param>
        private static Registration Register(int times, params EventCounterCollectionRequest[] alreadyCollected)
        {
            var services = new ServiceCollection();
            var log = new RecordingLogger();

            for (var i = 0; i < times; i++)
            {
                ApplicationInsightsRegistration.RegisterEventCounters(services, log);
            }

            var module = new EventCounterCollectionModule();
            var applied = 0;

            foreach (var request in alreadyCollected)
            {
                module.Counters.Add(request);
            }

            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType.Name != ConfiguratorInterface ||
                    descriptor.ImplementationInstance is not object configurator)
                {
                    continue;
                }

                var configuratorType = configurator.GetType();

                if (configuratorType.GetProperty("TelemetryModuleType")?.GetValue(configurator) is not Type moduleType ||
                    moduleType != typeof(EventCounterCollectionModule))
                {
                    continue;
                }

                // Configure(module, options), not Configure(module). Both exist, and this is the one
                // Application Insights itself calls when it builds its telemetry configuration at
                // startup - which is the whole point of driving it from here rather than asserting
                // on what got put in the container.
                var configure = configuratorType
                    .GetMethods()
                    .Single(method => method.Name == "Configure" && method.GetParameters().Length == 2);

                // Default options. Nothing in the registration reads anything off them, and taking
                // the type from the parameter avoids naming ApplicationInsightsServiceOptions here.
                var options = Activator.CreateInstance(configure.GetParameters()[1].ParameterType);

                configure.Invoke(configurator, new[] { module, options });
                applied++;
            }

            return new Registration(module, applied, log);
        }

        private sealed class Registration
        {
            internal Registration(EventCounterCollectionModule module, int configuratorsApplied, RecordingLogger log)
            {
                Module = module;
                ConfiguratorsApplied = configuratorsApplied;
                Log = log;
            }

            internal EventCounterCollectionModule Module { get; }

            /// <summary>How many pending configurations named the collection module.</summary>
            internal int ConfiguratorsApplied { get; }

            internal RecordingLogger Log { get; }
        }
    }
}
#endif
