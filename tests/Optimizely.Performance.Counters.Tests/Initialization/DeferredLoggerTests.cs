using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Shared;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Initialization
{
    /// <summary>
    /// What the initialization modules log during <c>ConfigureContainer</c> has to survive being
    /// buffered and replayed, because that is the only reason the modules no longer build a
    /// throwaway service provider to get a logger. docs/SMOKE_TEST.md tells an operator to read
    /// four specific lines out of the startup log, so losing or flattening them is a regression
    /// even though nothing stops working.
    /// </summary>
    public class DeferredLoggerTests
    {
        [Fact]
        public void Buffers_until_flushed()
        {
            ILogger logger = new DeferredLogger<DeferredLoggerTests>();
            var sink = new RecordingLogger();

            logger.LogInformation("first");
            logger.LogInformation("second");

            Assert.Empty(sink.Entries);

            ((DeferredLogger<DeferredLoggerTests>)logger).FlushTo(sink);

            Assert.Equal(new[] { "first", "second" }, sink.Entries.Select(e => e.Message));
        }

        [Fact]
        public void Replays_levels_and_exceptions()
        {
            var deferred = new DeferredLogger<DeferredLoggerTests>();
            var sink = new RecordingLogger();
            var boom = new InvalidOperationException("boom");

            ((ILogger)deferred).LogDebug("quiet");
            ((ILogger)deferred).LogError(boom, "loud");

            deferred.FlushTo(sink);

            Assert.Equal(new[] { LogLevel.Debug, LogLevel.Error }, sink.Entries.Select(e => e.Level));
            Assert.Null(sink.Entries[0].Exception);
            Assert.Same(boom, sink.Entries[1].Exception);
        }

        /// <summary>
        /// The point of replaying the original state object rather than a formatted string: a
        /// provider such as Application Insights reads the named values off it, and flattening the
        /// message would turn a queryable property into text.
        /// </summary>
        [Fact]
        public void Preserves_structured_properties()
        {
            var deferred = new DeferredLogger<DeferredLoggerTests>();
            var sink = new RecordingLogger();

            ((ILogger)deferred).LogInformation("Detected {Version} on {Framework}", "V13", "net10.0");

            deferred.FlushTo(sink);

            var entry = Assert.Single(sink.Entries);
            Assert.Equal("Detected V13 on net10.0", entry.Message);

            var properties = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(entry.State);
            Assert.Contains(properties, p => p.Key == "Version" && Equals(p.Value, "V13"));
            Assert.Contains(properties, p => p.Key == "Framework" && Equals(p.Value, "net10.0"));
        }

        [Fact]
        public void Flushing_twice_does_not_repeat_the_buffer()
        {
            var deferred = new DeferredLogger<DeferredLoggerTests>();
            var sink = new RecordingLogger();

            ((ILogger)deferred).LogInformation("once");

            deferred.FlushTo(sink);
            deferred.FlushTo(sink);

            Assert.Single(sink.Entries);
        }

        private sealed class RecordingLogger : ILogger
        {
            internal List<Entry> Entries { get; } = new List<Entry>();

            // Explicit for the same reason DeferredLogger's is: the notnull constraint on this
            // member arrived in Microsoft.Extensions.Logging.Abstractions 7.0.0, so declaring it
            // either way warns on one half of the target frameworks.
            IDisposable ILogger.BeginScope<TState>(TState state) => throw new NotSupportedException();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Entries.Add(new Entry(logLevel, state, exception, formatter(state, exception)));

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
}
