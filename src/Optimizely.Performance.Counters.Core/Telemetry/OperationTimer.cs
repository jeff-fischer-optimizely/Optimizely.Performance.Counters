using System.Diagnostics;

namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Allocation-free replacement for <see cref="Stopwatch"/> on instrumentation hot paths.
    /// <para>
    /// <c>Stopwatch.StartNew()</c> allocates a class instance on every call. Decorators wrap
    /// operations such as <c>IContentLoader.Get</c> that can run thousands of times per second,
    /// so that allocation shows up directly as Gen0 pressure caused by the counters themselves.
    /// </para>
    /// <para>
    /// This is a readonly struct holding a single <see cref="long"/> timestamp. Used as a local
    /// variable it never leaves the stack and allocates nothing. Do not box it, store it in a
    /// field of a reference type, or capture it in a lambda - that reintroduces the allocation.
    /// </para>
    /// </summary>
    public readonly struct OperationTimer
    {
        /// <summary>
        /// Conversion factor from raw timestamp ticks to milliseconds.
        /// <see cref="Stopwatch.Frequency"/> is fixed for the process lifetime, so this division
        /// is done once at type initialization rather than on every measurement.
        /// </summary>
        private static readonly double TimestampToMilliseconds = 1000.0 / Stopwatch.Frequency;

        private readonly long _startTimestamp;

        private OperationTimer(long startTimestamp)
        {
            _startTimestamp = startTimestamp;
        }

        /// <summary>
        /// The current raw high-resolution timestamp. Not a wall-clock time and not comparable
        /// across processes - only differences between two reads are meaningful.
        /// </summary>
        private static long CurrentTime => Stopwatch.GetTimestamp();

        /// <summary>
        /// Captures the current high-resolution timestamp. No allocation.
        /// </summary>
        public static OperationTimer Start() => new OperationTimer(CurrentTime);

        /// <summary>
        /// Milliseconds elapsed since <see cref="Start"/>. Safe to read more than once.
        /// </summary>
        public double ElapsedMilliseconds => (CurrentTime - _startTimestamp) * TimestampToMilliseconds;
    }
}
