using System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Metrics;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Infrastructure
{
    /// <summary>
    /// Base class for all performance counters with Application Insights integration.
    /// </summary>
    public abstract class PerformanceCounterBase : IPerformanceCounter, IDisposable
    {
        /// <summary>Application Insights client the derived counter reports through.</summary>
        protected readonly TelemetryClient TelemetryClient;

        /// <summary>Logger for instrumentation failures; never used to fail the caller.</summary>
        protected readonly ILogger Logger;
        private bool _isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PerformanceCounterBase"/> class.
        /// </summary>
        /// <param name="telemetryClient">Application Insights telemetry client.</param>
        /// <param name="logger">Logger instance.</param>
        protected PerformanceCounterBase(TelemetryClient telemetryClient, ILogger logger)
        {
            TelemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public abstract string Category { get; }

        /// <inheritdoc />
        public abstract string Subsystem { get; }

        /// <inheritdoc />
        public abstract CounterType Type { get; }

        /// <inheritdoc />
        public virtual bool IsEnabled => true;

        /// <inheritdoc />
        public virtual void Initialize()
        {
            Logger.LogInformation("Initializing performance counter: {CounterName}", Name);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the counter resources.
        /// </summary>
        /// <param name="disposing">True if disposing managed resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                Logger.LogInformation("Disposing performance counter: {CounterName}", Name);
            }

            _isDisposed = true;
        }

        /// <summary>
        /// Tracks a metric value to Application Insights.
        /// </summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">Metric value.</param>
        protected void TrackMetric(string name, double value)
        {
            try
            {
                TelemetryClient.GetMetric(name).TrackValue(value);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to track metric {MetricName}", name);
            }
        }

        /// <summary>
        /// Tracks a metric value with dimensions to Application Insights.
        /// </summary>
        /// <param name="name">Metric name.</param>
        /// <param name="value">Metric value.</param>
        /// <param name="dimension1Name">First dimension name.</param>
        /// <param name="dimension1Value">First dimension value.</param>
        protected void TrackMetric(string name, double value, string dimension1Name, string dimension1Value)
        {
            try
            {
                TelemetryClient.GetMetric(name, dimension1Name).TrackValue(value, dimension1Value);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to track metric {MetricName}", name);
            }
        }

        /// <summary>
        /// Ensures the counter has not been disposed.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(GetType().Name);
        }
    }
}
