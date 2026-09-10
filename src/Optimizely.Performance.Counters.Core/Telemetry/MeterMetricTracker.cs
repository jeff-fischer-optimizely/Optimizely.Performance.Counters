#if NET6_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Metric tracker that publishes to a <see cref="Meter"/>, the successor to EventCounters and
    /// the only path that reaches OpenTelemetry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the EventCounter delivery path has an end date.
    /// <c>Microsoft.ApplicationInsights.EventCounterCollector</c> stops at 2.23.0 and has no 3.x,
    /// while the SDK it belongs to is at 3.x and Application Insights Classic 2.x retires in March
    /// 2027. <see cref="ApplicationInsightsRegistration"/> reflects for exactly one assembly,
    /// <c>Microsoft.AI.EventCounterCollector</c>; the moment a site moves to SDK 3.x or to
    /// <c>UseAzureMonitor()</c> that lookup finds nothing and every counter in this package is
    /// published to an audience of nobody. A meter is what those hosts collect instead, and one
    /// line - <c>.WithMetrics(m =&gt; m.AddMeter("Optimizely-Performance"))</c> - subscribes them
    /// to all of it.
    /// </para>
    /// <para>
    /// Additional to the EventSource, never a replacement. The EventSource is the only path that
    /// works on net472, and it is what <c>dotnet-counters</c> and the DataDog tracer already
    /// discover without being told. <see cref="CompositeMetricTracker"/> is what runs both.
    /// </para>
    /// <para>
    /// Every instrument is a <see cref="Histogram{T}"/>, including the ones that read as gauges.
    /// That is not laziness: an EventCounter *is* a histogram - it accumulates observations and
    /// reports count, mean, min and max per interval - so recording the same observations into a
    /// histogram is the faithful translation, and it is the one that keeps both paths reporting
    /// the same numbers. It is also a straight upgrade, because a histogram keeps buckets where an
    /// EventCounter kept only the four aggregates, so p95 becomes available for the first time. For
    /// a value published once per interval, such as a rate or an uptime, the histogram's mean is
    /// that value exactly and it charts identically.
    /// </para>
    /// <para>
    /// No units and no tags, deliberately. Instrument units are folded into the exported metric
    /// name by the Prometheus exporter, and tags would let the two paths disagree about what the
    /// counter set even is - see <see cref="TrackMetric(string, double, string, string)"/>. Name
    /// parity between the EventSource and the meter is the one property this path exists to
    /// guarantee, and both features cost it.
    /// </para>
    /// </remarks>
    public sealed class MeterMetricTracker : IMetricTracker, IDisposable
    {
        private readonly Meter _meter;

        // Same reasoning as the EventSource's counter dictionary: written once per name and read on
        // every measurement, so a lock here would serialise content loading process-wide. Recording
        // into a histogram takes no lock of its own.
        private readonly ConcurrentDictionary<string, Histogram<double>> _instruments =
            new ConcurrentDictionary<string, Histogram<double>>(StringComparer.Ordinal);

        // Guards instrument creation only - see GetOrCreateInstrument. Never held over a Record.
        private readonly object _creationGate = new object();

        // Shutdown ordering is not something a decorator can be asked to reason about: a flush timer
        // can fire once more after the container has disposed this. Creating an instrument on a
        // disposed meter is not a thing to find out the answer to on a live site, so it is not asked.
        private volatile bool _disposed;

        // Snapshot of the pre-created instruments, for IsEnabled to walk without allocating an
        // enumerator over the dictionary on a path the order decorator calls per save.
        private readonly Histogram<double>[] _published;

        /// <summary>
        /// Creates the meter and publishes every registered counter as an instrument.
        /// </summary>
        /// <remarks>
        /// Pre-created for symmetry with the EventSource rather than out of necessity. The
        /// EventSource has to do this - a counter created after a collector attaches joins a
        /// counter group whose polling timer was never armed, and is never read - whereas a
        /// <see cref="MeterListener"/> is told about instruments published at any time. Doing it
        /// anyway means the two paths list the same set from the same moment, so "the meter is
        /// missing a counter the EventSource has" is never a thing anyone has to diagnose. It costs
        /// nothing downstream: exporters create a time series on the first *measurement*, so the
        /// unused instruments do not become empty series.
        /// </remarks>
        public MeterMetricTracker()
            : this(CounterNames.MeterName)
        {
        }

        /// <summary>
        /// Creates the tracker on a named meter. For tests.
        /// </summary>
        /// <param name="meterName">Meter to publish the instruments on.</param>
        /// <remarks>
        /// A <see cref="MeterListener"/> is filtered by meter name and cannot tell one
        /// <see cref="Meter"/> instance from another with the same name, so a test that asserts
        /// what this tracker published would otherwise also see every other tracker alive in the
        /// process - including the ones a container test registered and never disposed. Giving the
        /// test its own name is the only way to make those assertions about one instance.
        /// </remarks>
        internal MeterMetricTracker(string meterName)
        {
            _meter = new Meter(meterName);

            var published = new List<Histogram<double>>();

            foreach (var name in EventCounterRegistry.GetAllCounterNames())
            {
                published.Add(GetOrCreateInstrument(name));
            }

            _published = published.ToArray();
        }

        /// <inheritdoc />
        /// <remarks>
        /// Exact rather than inferred: a listener is free to subscribe to some instruments and not
        /// others, so this asks all of them and stops at the first that says yes. The scan is only
        /// walked to the end when nothing is listening, which is the case in which there is nothing
        /// else to do either - and <see cref="CompositeMetricTracker"/> checks the EventSource
        /// first, whose answer is a single field read, so a site collecting through Application
        /// Insights never reaches this at all.
        /// <para>
        /// One-way on some runtimes: .NET 6 and .NET 8 leave <c>Instrument.Enabled</c> true after the
        /// last listener detaches, where .NET 9 and .NET 10 clear it. That errs in the harmless
        /// direction - a measurement recorded into an instrument nobody is listening to returns
        /// immediately - and it is not worth tracking listener counts here to correct.
        /// </para>
        /// </remarks>
        public bool IsEnabled
        {
            get
            {
                if (_disposed)
                {
                    return false;
                }

                foreach (var instrument in _published)
                {
                    if (instrument.Enabled)
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
            if (_disposed)
            {
                return;
            }

            if (!_instruments.TryGetValue(name, out var instrument))
            {
                instrument = GetOrCreateInstrument(name);
            }

            // No IsEnabled guard. Record already returns immediately when nothing is listening, and
            // the guard above would be a second check of the same thing on the hottest path here.
            instrument.Record(value);
        }

        /// <inheritdoc />
        /// <remarks>
        /// The dimensions are dropped, exactly as the EventSource drops them, and a meter could
        /// carry them. That is the point: the whole counter set is designed around EventCounters
        /// having no dimensions - one name per GC generation, one per eviction reason, one per
        /// cascade origin - and tagging here would mean the two backends disagreed about what the
        /// counters are. A site would get one shape from Application Insights and another from
        /// OpenTelemetry, and every dashboard would be backend-specific. Tags are worth adding
        /// later, deliberately, to both paths or neither.
        /// </remarks>
        public void TrackMetric(string name, double value, string dimension1Name, string dimension1Value) =>
            TrackMetric(name, value);

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value) =>
            TrackMetric(name, value);

        /// <inheritdoc />
        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value,
            string dimension3Name, string dimension3Value) =>
            TrackMetric(name, value);

        /// <summary>
        /// Disposes the meter, which unpublishes every instrument on it.
        /// </summary>
        /// <remarks>
        /// Disposing the meter is the only way to unpublish an instrument - <c>Instrument</c> has no
        /// <c>Dispose</c> of its own. The dictionary is deliberately left populated: the flag set
        /// here is what stops measurements, and clearing it would send a late arrival down the
        /// creation path instead.
        /// </remarks>
        public void Dispose()
        {
            _disposed = true;
            _meter.Dispose();
        }

        /// <remarks>
        /// <para>
        /// Under a lock, which the EventSource's equivalent is not. Creating an instrument publishes
        /// it to every active listener, and a <c>GetOrAdd</c> factory may run more than once under
        /// contention - the EventSource handles that by disposing the losing racer's counter, which
        /// unregisters it. An <c>Instrument</c> has no <c>Dispose</c>; nothing short of disposing the
        /// whole meter unpublishes one. So the duplicate has to not be created rather than be cleaned
        /// up, and the only way to guarantee that is to serialise the creation.
        /// </para>
        /// <para>
        /// Free in practice. The constructor creates all of them before the tracker is reachable, so
        /// this is uncontended at startup and then never called again - <see cref="TrackMetric(string, double)"/>
        /// only reaches it for a name that is not in <see cref="EventCounterRegistry"/>, which is a
        /// bug in the counter registration rather than a path to optimise.
        /// </para>
        /// </remarks>
        private Histogram<double> GetOrCreateInstrument(string name)
        {
            lock (_creationGate)
            {
                if (_instruments.TryGetValue(name, out var existing))
                {
                    return existing;
                }

                var created = _meter.CreateHistogram<double>(name);
                _instruments[name] = created;

                return created;
            }
        }
    }
}
#endif
