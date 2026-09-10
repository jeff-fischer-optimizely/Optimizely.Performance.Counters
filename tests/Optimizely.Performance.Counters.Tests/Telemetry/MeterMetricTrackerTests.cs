#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// The second delivery path: the same counters, published to a <see cref="Meter"/> so that a
    /// host collecting through OpenTelemetry or Application Insights SDK 3.x receives them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CounterDeliveryTests"/> is the same question asked of the EventSource, and the two
    /// are deliberately shaped alike: publish everything up front, deliver the value intact, do not
    /// let a counter exist on one path and not the other. The reason there are two paths at all is
    /// that neither reaches every host - EventCounters have no collector in Application Insights
    /// SDK 3.x, and .NET Framework has no meters.
    /// </para>
    /// <para>
    /// Every test but one gives its tracker a meter name of its own. A <see cref="MeterListener"/>
    /// is filtered by meter name and cannot tell one <see cref="Meter"/> instance from another, so
    /// a listener on the package's own name observes every tracker alive in the process - which
    /// includes the ones the container tests register and never dispose. Unique names make these
    /// assertions about one instance rather than about the state of the whole test run, which is
    /// what serialising the tests would have bought and would not have been enough.
    /// </para>
    /// </remarks>
    public class MeterMetricTrackerTests
    {
        [Fact]
        public void The_meter_is_named_the_same_as_the_event_source()
        {
            // Not a tautology worth deleting: the two names are separate constants, and a site that
            // reads a counter off dotnet-counters and then goes looking for it in OpenTelemetry
            // should not have to discover that the identifier changed on the way.
            Assert.Equal(CounterNames.EventSourceName, CounterNames.MeterName);
        }

        /// <summary>
        /// The tracker a site actually gets - built by the parameterless constructor - publishes on
        /// the package's meter and not on some name only this test file knows.
        /// </summary>
        /// <remarks>
        /// The one test that uses the shared name, so it asserts presence rather than an exact set:
        /// another tracker publishing the same names concurrently is indistinguishable from this
        /// one, and that is precisely the condition the rest of the file avoids.
        /// </remarks>
        [Fact]
        public void The_default_tracker_publishes_on_the_package_meter()
        {
            using var tracker = new MeterMetricTracker();
            using var listener = Observe(CounterNames.MeterName, out var published, out _);

            Assert.Contains(CounterNames.CmsContent.Load.TimeMs, published);

            GC.KeepAlive(tracker);
        }

        /// <summary>
        /// Name parity with the EventSource, which is the whole reason this path exists in the shape
        /// it does rather than as a fresh, better-named counter set.
        /// </summary>
        /// <remarks>
        /// Both paths enumerate <see cref="EventCounterRegistry"/>, so this is checking that the
        /// enumeration actually happened - that the constructor published every name rather than
        /// waiting for a first measurement. A tracker that published lazily would pass every other
        /// test in this file and still leave a collector that attached at startup looking at a meter
        /// with nothing on it.
        /// </remarks>
        [Fact]
        public void Every_registered_counter_is_published_as_an_instrument()
        {
            var meterName = UniqueMeterName();

            using var tracker = new MeterMetricTracker(meterName);
            using var listener = Observe(meterName, out var published, out _);

            var expected = EventCounterRegistry.GetAllCounterNames()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var actual = published
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected, actual);

            GC.KeepAlive(tracker);
        }

        [Fact]
        public void A_measurement_reaches_a_listener_with_its_value()
        {
            var meterName = UniqueMeterName();

            using var tracker = new MeterMetricTracker(meterName);
            using var listener = Observe(meterName, out _, out var measurements);

            tracker.TrackMetric(CounterNames.CmsContent.Load.TimeMs, 42.5);

            Assert.Contains(
                measurements,
                m => m.Name == CounterNames.CmsContent.Load.TimeMs && m.Value == 42.5);
        }

        /// <remarks>
        /// The counterpart of the same rule in <c>EventCounterMetricTracker</c>. A meter could carry
        /// the dimensions as tags, and that is exactly why this is asserted: taking the offer would
        /// give a site one counter shape from Application Insights and a different one from
        /// OpenTelemetry, and every dashboard written against either would be wrong on the other.
        /// </remarks>
        [Fact]
        public void Dimensions_are_dropped_rather_than_becoming_tags()
        {
            var meterName = UniqueMeterName();

            using var tracker = new MeterMetricTracker(meterName);
            using var listener = Observe(meterName, out _, out var measurements);

            tracker.TrackMetric(CounterNames.CmsContent.Load.TimeMs, 1.0, "Operation", "Get");
            tracker.TrackMetric(CounterNames.CmsContent.Save.TimeMs, 2.0, "a", "1", "b", "2");
            tracker.TrackMetric(CounterNames.CmsContent.Delete.TimeMs, 3.0, "a", "1", "b", "2", "c", "3");

            var tagged = measurements.Where(m => m.Tags.Count > 0).ToList();

            Assert.True(
                tagged.Count == 0,
                "These measurements arrived with tags: " + string.Join(", ", tagged.Select(m => m.Name)) +
                ". Tagging one path and not the other makes the two backends disagree about what the " +
                "counter set is.");

            Assert.Equal(3, measurements.Count);
        }

        /// <remarks>
        /// Not a supported way to use this - every name should be in the registry, and
        /// <c>CounterRegistryCoverageTests</c> is what enforces that. It is asserted because the
        /// alternative behaviour is a <c>KeyNotFoundException</c> thrown from inside a decorator on
        /// a content load, which is a far worse way to find out that a counter was never registered
        /// than a counter that quietly works.
        /// </remarks>
        [Fact]
        public void A_name_that_is_not_in_the_registry_is_created_on_demand()
        {
            const string Unregistered = "Optimizely.Tests.NotInTheRegistry";

            var meterName = UniqueMeterName();

            using var tracker = new MeterMetricTracker(meterName);
            using var listener = Observe(meterName, out _, out var measurements);

            tracker.TrackMetric(Unregistered, 7.0);

            Assert.Contains(measurements, m => m.Name == Unregistered && m.Value == 7.0);
        }

        [Fact]
        public void Nothing_listening_means_the_tracker_reports_disabled()
        {
            using var tracker = new MeterMetricTracker(UniqueMeterName());

            Assert.False(tracker.IsEnabled);
        }

        /// <remarks>
        /// Only the transition into enabled is asserted. Whether <c>Instrument.Enabled</c> goes back
        /// to false when the last listener detaches is a runtime detail that differs across the
        /// supported targets - .NET 6 and .NET 8 leave it true, .NET 9 and .NET 10 clear it - and
        /// the direction that matters is this one: a tracker that under-reported enabled would have
        /// callers skip work a collector was waiting for. Over-reporting after a collector detaches
        /// costs a <c>Record</c> that goes nowhere.
        /// </remarks>
        [Fact]
        public void A_subscriber_makes_the_tracker_report_enabled()
        {
            var meterName = UniqueMeterName();

            using var tracker = new MeterMetricTracker(meterName);

            Assert.False(tracker.IsEnabled);

            using (Observe(meterName, out _, out _))
            {
                Assert.True(tracker.IsEnabled);
            }
        }

        /// <remarks>
        /// An <c>Instrument</c> cannot be unpublished on its own - it has no <c>Dispose</c> - so
        /// disposing the meter is the only thing that ends the publication. That is why the container
        /// registration uses a factory: a tracker the container does not own is a meter that outlives
        /// every container built in the process.
        /// </remarks>
        [Fact]
        public void Disposing_the_tracker_unpublishes_its_instruments()
        {
            var meterName = UniqueMeterName();

            var tracker = new MeterMetricTracker(meterName);
            tracker.Dispose();

            using var listener = Observe(meterName, out var published, out _);

            Assert.Empty(published);
        }

        [Fact]
        public void A_measurement_after_disposal_is_dropped_rather_than_throwing()
        {
            var tracker = new MeterMetricTracker(UniqueMeterName());
            tracker.Dispose();

            // Shutdown ordering is not something a decorator can be asked to reason about. A flush
            // timer that fires once more after the container has gone must not take the process with
            // it.
            tracker.TrackMetric(CounterNames.CmsContent.Load.TimeMs, 1.0);
        }

        /// <summary>
        /// A meter name no other tracker in the process is publishing to.
        /// </summary>
        private static string UniqueMeterName() =>
            CounterNames.MeterName + ".Test." + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Attaches a listener to one named meter and returns it, with the instruments it saw and a
        /// live list of the measurements it receives.
        /// </summary>
        /// <remarks>
        /// Started after the caller has created its tracker, which is the order that matters:
        /// <see cref="MeterListener.Start"/> replays the instruments that already exist, so this
        /// observes publication that happened before the listener did - the case the EventSource
        /// gets wrong and which is the entire subject of
        /// <see cref="Every_registered_counter_is_published_as_an_instrument"/>.
        /// </remarks>
        private static MeterListener Observe(
            string meterName, out List<string> published, out List<Measurement> measurements)
        {
            var seen = new List<string>();
            var recorded = new List<Measurement>();

            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName)
                    {
                        seen.Add(instrument.Name);
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };

            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                var pairs = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var tag in tags)
                {
                    pairs[tag.Key] = tag.Value;
                }

                recorded.Add(new Measurement(instrument.Name, value, pairs));
            });

            listener.Start();

            published = seen;
            measurements = recorded;

            return listener;
        }

        private sealed class Measurement
        {
            public Measurement(string name, double value, IReadOnlyDictionary<string, object?> tags)
            {
                Name = name;
                Value = value;
                Tags = tags;
            }

            public string Name { get; }

            public double Value { get; }

            public IReadOnlyDictionary<string, object?> Tags { get; }
        }
    }
}
#endif
