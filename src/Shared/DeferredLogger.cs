using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Shared
{
    /// <summary>
    /// Buffers what an initialization module logs during <c>ConfigureContainer</c> and replays it
    /// once a real logger exists.
    /// <para>
    /// A module configures the container before the container can be resolved from, so there is no
    /// logger to be had at the point the interesting things happen - version detection, telemetry
    /// discovery, the decorator list. The previous answer was
    /// <c>context.Services.BuildServiceProvider()</c>: a second, throwaway container built from a
    /// half-configured collection, solely to pull one logger out of it. That copies every
    /// descriptor the host has registered so far - thousands, on an Optimizely site - and then
    /// instantiates a duplicate <c>ILoggerFactory</c> and every logger provider registered with it,
    /// none of which is ever disposed. With an Application Insights logger provider in the mix that
    /// means a second TelemetryConfiguration and its channel. It also ran once per module, so a
    /// site with both packages did it twice.
    /// </para>
    /// <para>
    /// This buffers instead, and the module flushes in <c>Initialize</c>, where the container is
    /// built and the host's own configured logger is available. Log levels are honoured because the
    /// level travels with each entry and is applied by the real logger on replay, and structured
    /// properties survive because the original state object is replayed rather than a formatted
    /// string.
    /// </para>
    /// </summary>
    /// <typeparam name="TCategory">The module doing the logging, used as the log category.</typeparam>
    internal sealed class DeferredLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly object _lock = new object();

        /// <summary>
        /// Everything, unfiltered. The real logger applies the host's filters on replay, and a
        /// module emits perhaps a dozen entries at startup, so buffering below the configured level
        /// costs nothing worth measuring and dropping early would be irreversible.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <summary>
        /// Scopes are not buffered. Nothing in either module opens one.
        /// </summary>
        /// <remarks>
        /// Explicit, because the constraint on this member is not stable across the versions of
        /// Microsoft.Extensions.Logging.Abstractions the six target frameworks pull in: 6.0.0 does
        /// not constrain TState, 7.0.0 and later add <c>notnull</c>. An ordinary implementation
        /// would warn on one half of the matrix whichever way it was declared. An explicit one
        /// restates no constraints and satisfies both.
        /// </remarks>
        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter == null)
            {
                return;
            }

            lock (_lock)
            {
                _entries.Add(new Entry(
                    logLevel,
                    eventId,
                    state,
                    exception,
                    (s, e) => formatter((TState)s!, e)));
            }
        }

        /// <summary>
        /// Replays the buffer to <paramref name="logger"/> and empties it.
        /// </summary>
        /// <param name="logger">The real logger, resolved from the built container.</param>
        internal void FlushTo(ILogger logger)
        {
            List<Entry> entries;
            lock (_lock)
            {
                entries = new List<Entry>(_entries);
                _entries.Clear();
            }

            foreach (var entry in entries)
            {
                // The original state object, not a pre-formatted string: providers recognise
                // FormattedLogValues by its IReadOnlyList<KeyValuePair<string, object>> face, and
                // that is what turns {VersionInfo} into a queryable property rather than text.
                logger.Log(entry.Level, entry.EventId, entry.State, entry.Exception, entry.Formatter);
            }
        }

        private sealed class Entry
        {
            internal Entry(
                LogLevel level,
                EventId eventId,
                object? state,
                Exception? exception,
                Func<object?, Exception?, string> formatter)
            {
                Level = level;
                EventId = eventId;
                State = state;
                Exception = exception;
                Formatter = formatter;
            }

            internal LogLevel Level { get; }

            internal EventId EventId { get; }

            internal object? State { get; }

            internal Exception? Exception { get; }

            internal Func<object?, Exception?, string> Formatter { get; }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new NullScope();

            private NullScope()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
