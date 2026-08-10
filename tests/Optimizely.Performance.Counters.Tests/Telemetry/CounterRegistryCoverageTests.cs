using System.Collections.Generic;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Keeps <see cref="EventCounterRegistry"/> and the decorators from drifting apart.
    /// <para>
    /// The registry is what consumers point Application Insights and DataDog at. A counter emitted
    /// but not registered is invisible in production; a counter registered but never emitted is a
    /// permanently empty chart. Neither shows up in a build.
    /// </para>
    /// </summary>
    public class CounterRegistryCoverageTests
    {
        private static readonly HashSet<string> Registered =
            new HashSet<string>(EventCounterRegistry.GetAllCounterNames());

        [Fact]
        public void The_registry_has_no_duplicates()
        {
            var names = EventCounterRegistry.GetAllCounterNames().ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void Every_counter_name_is_namespaced_under_Optimizely()
        {
            Assert.All(Registered, name => Assert.StartsWith("Optimizely.", name));
        }

        [Fact]
        public void Everything_the_decorators_emit_is_registered()
        {
            var emitted = EmitEverything();

            var unregistered = emitted.Except(Registered).OrderBy(n => n).ToList();

            Assert.True(
                unregistered.Count == 0,
                "These counters are emitted but missing from EventCounterRegistry, so no consumer " +
                "will collect them: " + string.Join(", ", unregistered));
        }

#if CMS13
        [Fact]
        public void Everything_registered_is_emitted_by_some_decorator()
        {
            // CMS 13 / Commerce 15 only: it is the one target where every decorator exists, so it
            // is the only place the registry can be checked in full.
            var emitted = EmitEverything();

            var neverEmitted = Registered
                .Except(emitted)
                .Except(DecoratorSweep.OutOfReach)
                .OrderBy(n => n)
                .ToList();

            Assert.True(
                neverEmitted.Count == 0,
                "These counters are registered but no decorator emits them, so they will chart as " +
                "permanently empty: " + string.Join(", ", neverEmitted));
        }
#endif

        /// <summary>
        /// Drives every decorator available on this target framework and returns the distinct set
        /// of counter names they produced.
        /// </summary>
        private static HashSet<string> EmitEverything()
        {
            var tracker = new RecordingMetricTracker();
            DecoratorSweep.DriveEverything(tracker);

            return new HashSet<string>(tracker.Names);
        }
    }
}
