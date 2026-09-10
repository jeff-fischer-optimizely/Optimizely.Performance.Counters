using System;
using System.Collections.Generic;
using System.Linq;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Publishes every measurement to more than one tracker, so a counter can reach two telemetry
    /// backends without the decorator that produced it knowing there are two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists for one pairing: the EventSource and the meter. Both have to run at once, for an
    /// entire release cycle at least. The EventSource is the only path that works on net472 and the
    /// only one <c>dotnet-counters</c> and the DataDog tracer find without being configured; the
    /// meter is the only one that reaches OpenTelemetry, Azure Monitor's distro, and Application
    /// Insights SDK 3.x. Neither is a superset of the other, and a site cannot be asked to pick
    /// before it knows which of its collectors will still exist in 2027.
    /// </para>
    /// <para>
    /// Order matters, mildly. <see cref="IsEnabled"/> short-circuits, so the cheapest tracker to ask
    /// goes first - which is the EventSource, whose answer is a field read.
    /// </para>
    /// </remarks>
    public sealed class CompositeMetricTracker : IMetricTracker, IDisposable
    {
        private readonly IMetricTracker[] _trackers;

        /// <summary>
        /// Composes the given trackers, in the order they are given.
        /// </summary>
        /// <param name="trackers">The trackers to publish to. Nulls are dropped.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trackers"/> is null.</exception>
        /// <exception cref="ArgumentException">No non-null tracker was given.</exception>
        public CompositeMetricTracker(params IMetricTracker[] trackers)
            : this((IEnumerable<IMetricTracker>)(trackers ?? throw new ArgumentNullException(nameof(trackers))))
        {
        }

        /// <summary>
        /// Composes the given trackers, in enumeration order.
        /// </summary>
        /// <param name="trackers">The trackers to publish to. Nulls are dropped.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trackers"/> is null.</exception>
        /// <exception cref="ArgumentException">No non-null tracker was given.</exception>
        public CompositeMetricTracker(IEnumerable<IMetricTracker> trackers)
        {
            if (trackers == null)
            {
                throw new ArgumentNullException(nameof(trackers));
            }

            _trackers = trackers.Where(t => t != null).ToArray();

            // An empty composite would satisfy IMetricTracker and silently discard everything, which
            // is the failure this whole package is least able to notice. Constructing one is a wiring
            // mistake, so it fails at wiring time.
            if (_trackers.Length == 0)
            {
                throw new ArgumentException(
                    "A composite metric tracker needs at least one tracker to publish to.", nameof(trackers));
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// True if any of them is collecting. The one caller of this - the order decorator, deciding
        /// whether to compute cart totals nobody asked for - wants to know whether the work will be
        /// seen by anyone, not by everyone.
        /// </remarks>
        public bool IsEnabled
        {
            get
            {
                foreach (var tracker in _trackers)
                {
                    if (tracker.IsEnabled)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <inheritdoc />
        public void TrackMetric(string name, double value)
        {
            foreach (var tracker in _trackers)
            {
                tracker.TrackMetric(name, value);
            }
        }

        /// <inheritdoc />
        public void TrackMetric(string name, double value, string dimension1Name, string dimension1Value)
        {
            foreach (var tracker in _trackers)
            {
                tracker.TrackMetric(name, value, dimension1Name, dimension1Value);
            }
        }

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value)
        {
            foreach (var tracker in _trackers)
            {
                tracker.TrackMetric(name, value,
                    dimension1Name, dimension1Value,
                    dimension2Name, dimension2Value);
            }
        }

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value,
            string dimension3Name, string dimension3Value)
        {
            foreach (var tracker in _trackers)
            {
                tracker.TrackMetric(name, value,
                    dimension1Name, dimension1Value,
                    dimension2Name, dimension2Value,
                    dimension3Name, dimension3Value);
            }
        }

        /// <summary>
        /// Disposes the composed trackers that are disposable.
        /// </summary>
        /// <remarks>
        /// The container owns this object's lifetime and the composed trackers have no other owner,
        /// so disposing them here is the only thing that ever will. In an Optimizely site that means
        /// at shutdown and never otherwise; in a test it means at the end of the test, which is what
        /// releases the meter name.
        /// </remarks>
        public void Dispose()
        {
            foreach (var tracker in _trackers)
            {
                (tracker as IDisposable)?.Dispose();
            }
        }
    }
}
