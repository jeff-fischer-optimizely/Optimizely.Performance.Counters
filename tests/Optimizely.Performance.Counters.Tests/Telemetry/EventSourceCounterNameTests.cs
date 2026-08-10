using System.Collections.Generic;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Checks the names that leave the process against the names consumers subscribe to.
    /// <para>
    /// <see cref="CounterRegistryCoverageTests"/> stops at <see cref="IMetricTracker"/>. Everything
    /// past that point - <see cref="EventCounterMetricTracker"/> and the EventSource - can still
    /// rewrite a name on its way out, and Application Insights matches
    /// <see cref="EventCounterRegistry"/> exactly. That is precisely how the counters were
    /// previously published as <c>Name[Operation=Get]</c> while the registry offered
    /// <c>Name</c>: a mismatch that cost most of the counters their collection and that no test
    /// short of this one could see.
    /// </para>
    /// </summary>
    public class EventSourceCounterNameTests
    {
        private static readonly HashSet<string> Registered =
            new HashSet<string>(EventCounterRegistry.GetAllCounterNames());

        private static readonly PublishedCounterNames.Result Observed =
            PublishedCounterNames.Observe(() => DecoratorSweep.DriveEverything(new EventCounterMetricTracker()));

        private static IReadOnlyCollection<string> Published => Observed.Names;

        [Fact]
        public void The_EventSource_initialized_without_faulting()
        {
            // EventSource swallows its own configuration errors and reports them as events rather
            // than throwing, so a faulted source publishes nothing while looking perfectly healthy
            // to every caller. This is how a duplicate event ID on .NET Framework silenced every
            // V11 counter.
            Assert.True(
                Observed.Errors.Count == 0,
                "The EventSource reported errors about itself, so it is publishing nothing: " +
                string.Join(" | ", Observed.Errors));
        }

        [Fact]
        public void The_decorators_reach_the_EventSource()
        {
            // Guards the rest of this class: every assertion below passes trivially if the sweep
            // published nothing at all.
            Assert.NotEmpty(Published);
        }

        [Fact]
        public void Every_published_name_is_one_a_consumer_subscribes_to()
        {
            var uncollected = Published.Except(Registered).OrderBy(n => n).ToList();

            Assert.True(
                uncollected.Count == 0,
                "These names reached the EventSource but are not in EventCounterRegistry, so " +
                "Application Insights and DataDog will not collect them: " +
                string.Join(", ", uncollected));
        }

        [Fact]
        public void The_tracker_does_not_rewrite_names_on_the_way_out()
        {
            // The dimensioned overloads of EventCounterMetricTracker exist and the decorators use
            // them, so this is the assertion that keeps them from encoding a dimension into the
            // name again.
            Assert.All(Published, name => Assert.DoesNotContain('[', name));
        }

#if CMS13
        [Fact]
        public void Every_subscribed_name_is_one_some_decorator_publishes()
        {
            // CMS 13 / Commerce 15 only, for the same reason as the registry coverage test: it is
            // the one target where every decorator exists.
            var neverPublished = Registered
                .Except(Published)
                .Except(DecoratorSweep.OutOfReach)
                .OrderBy(n => n)
                .ToList();

            Assert.True(
                neverPublished.Count == 0,
                "Application Insights subscribes to these but nothing publishes them, so they " +
                "will chart as permanently empty: " + string.Join(", ", neverPublished));
        }
#endif
    }
}
