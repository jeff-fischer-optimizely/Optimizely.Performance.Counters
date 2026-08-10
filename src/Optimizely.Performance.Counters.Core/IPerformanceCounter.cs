using System;

namespace Optimizely.Performance.Counters.Infrastructure
{
    /// <summary>
    /// Represents a performance counter that can be tracked and reported.
    /// </summary>
    public interface IPerformanceCounter
    {
        /// <summary>
        /// Gets the name of the counter (e.g., "Optimizely.CMS.Content.LoadsPerSecond").
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the category of the counter (CMS, Commerce, Infrastructure).
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Gets the subsystem this counter belongs to (Content, Cache, Orders, etc.).
        /// </summary>
        string Subsystem { get; }

        /// <summary>
        /// Gets the counter type (Rate, Latency, Gauge, Percentage).
        /// </summary>
        CounterType Type { get; }

        /// <summary>
        /// Gets whether this counter is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Initializes the counter. Called once during application startup.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Disposes the counter. Called during application shutdown.
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// Type of performance counter.
    /// </summary>
    public enum CounterType
    {
        /// <summary>
        /// Rate counter (operations per second/hour).
        /// </summary>
        Rate,

        /// <summary>
        /// Latency counter (average/P95/P99 time in milliseconds).
        /// </summary>
        Latency,

        /// <summary>
        /// Gauge counter (current value/count/depth).
        /// </summary>
        Gauge,

        /// <summary>
        /// Percentage counter (hit rate, success rate, etc.).
        /// </summary>
        Percentage
    }
}
