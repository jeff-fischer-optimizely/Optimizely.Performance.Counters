using System;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Abstraction for tracking performance metrics.
    /// Implementations can send to Application Insights, DataDog, or other telemetry systems.
    /// </summary>
    public interface IMetricTracker
    {
        /// <summary>
        /// Tracks a metric value.
        /// </summary>
        /// <param name="name">Metric name (e.g., "Optimizely.CMS.Content.LoadTimeMs")</param>
        /// <param name="value">Metric value</param>
        void TrackMetric(string name, double value);

        /// <summary>
        /// Tracks a metric value with one dimension.
        /// </summary>
        /// <param name="name">Metric name</param>
        /// <param name="value">Metric value</param>
        /// <param name="dimension1Name">Dimension name (e.g., "Operation")</param>
        /// <param name="dimension1Value">Dimension value (e.g., "Get")</param>
        void TrackMetric(string name, double value, string dimension1Name, string dimension1Value);

        /// <summary>
        /// Tracks a metric value with two dimensions.
        /// </summary>
        /// <param name="name">Metric name</param>
        /// <param name="value">Metric value</param>
        /// <param name="dimension1Name">First dimension name</param>
        /// <param name="dimension1Value">First dimension value</param>
        /// <param name="dimension2Name">Second dimension name</param>
        /// <param name="dimension2Value">Second dimension value</param>
        void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value);

        /// <summary>
        /// Tracks a metric value with three dimensions.
        /// </summary>
        void TrackMetric(string name, double value,
            string dimension1Name, string dimension1Value,
            string dimension2Name, string dimension2Value,
            string dimension3Name, string dimension3Value);
    }
}
