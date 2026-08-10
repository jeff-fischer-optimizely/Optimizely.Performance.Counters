using System.Collections.Generic;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// A single metric emitted by a decorator.
    /// </summary>
    public sealed class TrackedMetric
    {
        public TrackedMetric(string name, double value, IReadOnlyDictionary<string, string> dimensions)
        {
            Name = name;
            Value = value;
            Dimensions = dimensions;
        }

        public string Name { get; }

        public double Value { get; }

        public IReadOnlyDictionary<string, string> Dimensions { get; }

        public override string ToString() =>
            Dimensions.Count == 0
                ? $"{Name}={Value}"
                : $"{Name}={Value} [{string.Join(", ", Dimensions.Select(d => $"{d.Key}={d.Value}"))}]";
    }

    /// <summary>
    /// Captures what a decorator emits instead of writing to the EventSource. Lets a test assert
    /// both the counter name and the dimensions, which is what the Application Insights and
    /// DataDog queries are ultimately written against.
    /// </summary>
    public sealed class RecordingMetricTracker : IMetricTracker
    {
        private readonly List<TrackedMetric> _metrics = new List<TrackedMetric>();

        /// <summary>Metrics emitted, in order.</summary>
        public IReadOnlyList<TrackedMetric> Metrics => _metrics;

        /// <summary>Distinct counter names emitted, without dimensions.</summary>
        public IEnumerable<string> Names => _metrics.Select(m => m.Name).Distinct();

        public void TrackMetric(string name, double value) =>
            Record(name, value);

        public void TrackMetric(string name, double value, string dimension1Name, string dimension1Value) =>
            Record(name, value, (dimension1Name, dimension1Value));

        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value) =>
            Record(name, value, (dimension1Name, dimension1Value), (dimension2Name, dimension2Value));

        public void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value,
            string dimension3Name, string dimension3Value) =>
            Record(name, value,
                (dimension1Name, dimension1Value),
                (dimension2Name, dimension2Value),
                (dimension3Name, dimension3Value));

        private void Record(string name, double value, params (string Name, string Value)[] dimensions)
        {
            var map = new Dictionary<string, string>();
            foreach (var dimension in dimensions)
            {
                map[dimension.Name] = dimension.Value;
            }

            _metrics.Add(new TrackedMetric(name, value, map));
        }
    }
}
