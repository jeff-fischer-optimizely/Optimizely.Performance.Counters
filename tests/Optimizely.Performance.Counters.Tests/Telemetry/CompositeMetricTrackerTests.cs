using System;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    /// <summary>
    /// Publishing one measurement to two backends, which is what lets the EventSource and the meter
    /// run at once without any decorator knowing there are two.
    /// </summary>
    public class CompositeMetricTrackerTests
    {
        [Fact]
        public void Every_tracker_receives_the_measurement()
        {
            var first = new RecordingMetricTracker();
            var second = new RecordingMetricTracker();

            var composite = new CompositeMetricTracker(first, second);
            composite.TrackMetric(CounterNames.CmsContent.Load.TimeMs, 12.0);

            foreach (var tracker in new[] { first, second })
            {
                var metric = Assert.Single(tracker.Metrics);
                Assert.Equal(CounterNames.CmsContent.Load.TimeMs, metric.Name);
                Assert.Equal(12.0, metric.Value);
            }
        }

        /// <remarks>
        /// All four overloads, because a composite that forwarded three of them and quietly dropped
        /// the fourth would look correct in every test written against the counters this package
        /// emits today - nothing currently passes three dimensions - and would lose measurements the
        /// day something did.
        /// </remarks>
        [Fact]
        public void Every_overload_is_forwarded_with_its_dimensions_intact()
        {
            var recorder = new RecordingMetricTracker();
            var composite = new CompositeMetricTracker(recorder, new RecordingMetricTracker());

            composite.TrackMetric("none", 0.0);
            composite.TrackMetric("one", 1.0, "a", "1");
            composite.TrackMetric("two", 2.0, "a", "1", "b", "2");
            composite.TrackMetric("three", 3.0, "a", "1", "b", "2", "c", "3");

            Assert.Equal(
                new[] { "none", "one", "two", "three" },
                recorder.Metrics.Select(m => m.Name).ToArray());

            Assert.Equal(
                new[] { 0, 1, 2, 3 },
                recorder.Metrics.Select(m => m.Dimensions.Count).ToArray());

            Assert.Equal("3", recorder.Metrics.Last().Dimensions["c"]);
        }

        [Fact]
        public void One_collecting_backend_is_enough_to_report_enabled()
        {
            var off = new RecordingMetricTracker { IsEnabled = false };
            var on = new RecordingMetricTracker { IsEnabled = true };

            // Either way round: the answer is about the pair, not about the order.
            Assert.True(new CompositeMetricTracker(off, on).IsEnabled);
            Assert.True(new CompositeMetricTracker(on, off).IsEnabled);
        }

        /// <remarks>
        /// The one consumer of <see cref="IMetricTracker.IsEnabled"/> is the order decorator, which
        /// uses it to skip computing cart totals nobody will read. False here has to mean nobody is
        /// collecting on any path, or the site pays for totals that are discarded twice over.
        /// </remarks>
        [Fact]
        public void Nothing_collecting_anywhere_reports_disabled()
        {
            var composite = new CompositeMetricTracker(
                new RecordingMetricTracker { IsEnabled = false },
                new RecordingMetricTracker { IsEnabled = false });

            Assert.False(composite.IsEnabled);
        }

        /// <remarks>
        /// Ordering is the only reason the registration puts the EventCounter tracker first: its
        /// answer is a field read, where the meter tracker's is a scan of every instrument. Cheap
        /// first only pays if the scan is genuinely skipped.
        /// </remarks>
        [Fact]
        public void The_enabled_check_stops_at_the_first_tracker_that_says_yes()
        {
            var second = new CountingTracker { IsEnabled = true };
            var composite = new CompositeMetricTracker(
                new CountingTracker { IsEnabled = true }, second);

            Assert.True(composite.IsEnabled);
            Assert.Equal(0, second.EnabledChecks);
        }

        /// <remarks>
        /// An empty composite satisfies <see cref="IMetricTracker"/> perfectly and discards
        /// everything, which is the single failure this package is least able to notice in
        /// production - the counters read zero, and zero is also what a healthy idle site reports.
        /// So it fails at wiring time instead.
        /// </remarks>
        [Fact]
        public void A_composite_with_nowhere_to_publish_is_rejected()
        {
            Assert.Throws<ArgumentException>(() => new CompositeMetricTracker());
            Assert.Throws<ArgumentException>(() => new CompositeMetricTracker(null!, null!));
        }

        [Fact]
        public void A_composite_will_not_take_a_null_list()
        {
            Assert.Throws<ArgumentNullException>(
                () => new CompositeMetricTracker((IMetricTracker[])null!));
        }

        [Fact]
        public void A_null_tracker_alongside_a_real_one_is_dropped_rather_than_fatal()
        {
            var recorder = new RecordingMetricTracker();
            var composite = new CompositeMetricTracker(null!, recorder, null!);

            composite.TrackMetric(CounterNames.CmsContent.Load.TimeMs, 1.0);

            Assert.Single(recorder.Metrics);
        }

        /// <remarks>
        /// The composite is what the container holds, so it is the only thing with a chance to
        /// dispose the meter underneath it. A composite that swallowed disposal would leave the
        /// meter published for the life of the process.
        /// </remarks>
        [Fact]
        public void Disposing_the_composite_disposes_the_trackers_that_can_be()
        {
            var disposable = new CountingTracker();
            var composite = new CompositeMetricTracker(disposable, new RecordingMetricTracker());

            composite.Dispose();

            Assert.True(disposable.Disposed);
        }

        private sealed class CountingTracker : IMetricTracker, IDisposable
        {
            private bool _enabled;

            public int EnabledChecks { get; private set; }

            public bool Disposed { get; private set; }

            public bool IsEnabled
            {
                get
                {
                    EnabledChecks++;
                    return _enabled;
                }

                set => _enabled = value;
            }

            public void TrackMetric(string name, double value)
            {
            }

            public void TrackMetric(string name, double value, string d1n, string d1v)
            {
            }

            public void TrackMetric(string name, double value, string d1n, string d1v, string d2n, string d2v)
            {
            }

            public void TrackMetric(string name, double value,
                string d1n, string d1v, string d2n, string d2v, string d3n, string d3v)
            {
            }

            public void Dispose() => Disposed = true;
        }
    }
}
