using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// An <see cref="ILogger"/> that keeps what it was told, so a test can assert on it.
    /// <para>
    /// Most of this codebase reports failure by logging rather than by throwing - deliberately, so
    /// that an optional integration going missing cannot take a site down. The cost is that a
    /// broken integration and a working one both return normally, and the log is the only place
    /// they differ. That makes the log an assertable output rather than a side effect.
    /// </para>
    /// </summary>
    internal sealed class RecordingLogger : ILogger
    {
        internal List<Entry> Entries { get; } = new List<Entry>();

        /// <summary>Entries at <see cref="LogLevel.Error"/> or worse.</summary>
        internal IEnumerable<Entry> Failures =>
            Entries.Where(entry => entry.Level >= LogLevel.Error);

        // Explicit for the same reason DeferredLogger's is: the notnull constraint on this member
        // arrived in Microsoft.Extensions.Logging.Abstractions 7.0.0, so declaring it either way
        // warns on one half of the target frameworks.
        IDisposable ILogger.BeginScope<TState>(TState state) => throw new NotSupportedException();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, state, exception, formatter(state, exception)));

        /// <summary>
        /// Everything recorded, one entry per line, for use in an assertion message.
        /// </summary>
        internal string Transcript() =>
            Entries.Count == 0
                ? "(nothing was logged)"
                : string.Join(
                    Environment.NewLine,
                    Entries.Select(entry =>
                        "  [" + entry.Level + "] " + entry.Message +
                        (entry.Exception == null ? string.Empty : " -> " + entry.Exception)));

        internal sealed class Entry
        {
            internal Entry(LogLevel level, object? state, Exception? exception, string message)
            {
                Level = level;
                State = state;
                Exception = exception;
                Message = message;
            }

            internal LogLevel Level { get; }

            internal object? State { get; }

            internal Exception? Exception { get; }

            internal string Message { get; }
        }
    }
}
