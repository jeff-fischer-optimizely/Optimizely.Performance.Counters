using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// The last hop: what a subscriber actually receives, values included.
    /// <para>
    /// <see cref="EventSourceCounterNameTests"/> checks the names that come out of the same
    /// observation. This checks that the numbers behind them survive the trip, and that a subscriber
    /// receives anything at all in the first place - which for most of the life of this code it did
    /// not, on every target framework where an EventCounter does the delivering.
    /// </para>
    /// <para>
    /// This is the closest thing to Stage 3 of docs/SMOKE_TEST.md that runs without a licensed site:
    /// same EventSource, same subscriber mechanism <c>dotnet-counters</c> and Application Insights'
    /// EventCounterCollectionModule use, same names, same aggregation.
    /// </para>
    /// </summary>
    public class CounterDeliveryTests
    {
        /// <summary>
        /// Counter this test asserts a value for. The cache decorator reports it from its flush as
        /// the number of reads the window saw, so a sweep that ran at all makes it positive - unlike
        /// the duration counters, whose stubs can finish inside the timer's resolution.
        /// </summary>
        private const string CounterWithAKnownValue = CounterNames.CmsCache.Operations;

        private static readonly PublishedCounterNames.Result Observed =
            PublishedCounterNames.Observe(() => DecoratorSweep.DriveEverything(new EventCounterMetricTracker()));

        [Fact]
        public void The_counters_reach_a_subscriber()
        {
            // Guards everything below, here and in EventSourceCounterNameTests: every name
            // assertion passes trivially against an empty observation.
            Assert.NotEmpty(Observed.Names);
        }

        [Fact]
        public void The_value_behind_a_counter_survives_the_trip()
        {
            var operations = Observed.ValueOf(CounterWithAKnownValue);

            Assert.True(
                operations.HasValue,
                CounterWithAKnownValue + " never arrived. Delivered: " +
                string.Join(", ", Observed.Names.OrderBy(n => n, StringComparer.Ordinal)));

            Assert.True(
                operations > 0,
                CounterWithAKnownValue + " arrived as " +
                operations!.Value.ToString(CultureInfo.InvariantCulture) +
                ". The sweep performs reads on every pass, so a zero here means the value was lost " +
                "between the decorator and the wire even though the counter itself arrived.");
        }

        /// <summary>
        /// Keeps the observation able to tell a published counter from an existing one.
        /// </summary>
        /// <remarks>
        /// Every counter now exists from the moment the EventSource is constructed and reports on
        /// every tick whether or not anything wrote to it, so "this counter reached a subscriber" no
        /// longer implies "some decorator published it". <see cref="PublishedCounterNames"/> draws
        /// the line at a non-empty interval. If that filter ever stops working, the name coverage in
        /// <see cref="EventSourceCounterNameTests"/> starts passing against all thirty names
        /// regardless of what the decorators do, and stops being a test of anything. The two cart
        /// counters are the ones that make it observable: they exist, they report, and the sweep
        /// cannot write to them.
        /// </remarks>
        [Fact]
        public void A_counter_that_exists_but_was_never_written_is_not_counted_as_published()
        {
            var published = new HashSet<string>(Observed.Names, StringComparer.Ordinal);

            Assert.All(
                DecoratorSweep.OutOfReach,
                name => Assert.False(
                    published.Contains(name),
                    name + " is reported as published, but nothing in the sweep can write to it. " +
                    "The observation is counting counters that merely exist."));
        }

#if NET6_0_OR_GREATER
        /// <summary>
        /// The regression guard on creating the counters up front.
        /// </summary>
        /// <remarks>
        /// An EventCounter reports through a CounterGroup that arms its polling timer by handling
        /// the Enable command, and the group is created by the first counter on the source. Creating
        /// counters lazily on first measurement therefore meant a subscriber that attached before
        /// any measurement had happened armed nothing and received nothing - permanently, not until
        /// the next tick. That is the ordinary order on a real site: Application Insights subscribes
        /// at startup, the first content load comes later.
        /// <para>
        /// Driving nothing is what makes this decisive. Every counter must report even with an empty
        /// interval, because that is the proof that the timer is running for a subscriber that has
        /// seen no traffic. net472 is excluded: it has no polling, and writes each measurement as it
        /// happens.
        /// </para>
        /// </remarks>
        [Fact]
        public void A_subscriber_that_has_seen_no_traffic_still_receives_every_counter()
        {
            var registered = EventCounterRegistry.GetAllCounterNames().ToList();
            var reporting = new HashSet<string>(PublishedCounterNames.ObserveIdle().Reporting, StringComparer.Ordinal);

            var silent = registered
                .Where(name => !reporting.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                silent.Count == 0,
                "These counters never reported to a subscriber that drove no traffic, so a " +
                "collector attaching before the first measurement will not see them: " +
                string.Join(", ", silent));
        }
#endif
    }
}
