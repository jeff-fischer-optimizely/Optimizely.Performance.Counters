#if !NET472
using System;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Diagnostics
{
    /// <summary>
    /// The V12 and V13 sink for the log write rate counters: a logging provider that writes
    /// nowhere and counts everything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Microsoft.Extensions.Logging</c> is the chokepoint on these versions. Optimizely's own
    /// <c>EPiServer.Logging.LogManager</c> forwards to it, the framework and the site's own code
    /// use it directly, and every configured sink - console, file, Serilog, Application Insights -
    /// hangs off the same fan-out. A provider registered here therefore sees the same events every
    /// real sink sees, which is what makes the number a write rate rather than an estimate.
    /// </para>
    /// <para>
    /// <see cref="ILogger.IsEnabled"/> returns false, always, and that is load-bearing rather than
    /// lazy. <c>Logger.Log</c> consults the configured filter and not the provider's
    /// <c>IsEnabled</c>, so returning false costs nothing in what is counted - but
    /// <c>Logger.IsEnabled</c> is the OR across providers, and a provider answering true would
    /// make <c>logger.IsEnabled(LogLevel.Trace)</c> true site-wide. Every <c>if (log.IsEnabled)</c>
    /// guard in Optimizely and in the site would then start building messages that no sink writes.
    /// A counter that made the site do the work it was counting would be worse than no counter.
    /// </para>
    /// <para>
    /// What is counted is therefore what the host's filter rules let through to a provider with no
    /// rules of its own - the default minimum level, usually Information. That is deliberately the
    /// same thing an unconfigured sink would write. An operator who wants a different denominator
    /// can set one against the provider alias, for example
    /// <c>"Logging": { "OptimizelyLogWriteRate": { "LogLevel": { "Default": "Debug" } } }</c>.
    /// </para>
    /// </remarks>
    [ProviderAlias("OptimizelyLogWriteRate")]
    public sealed class LogWriteRateLoggerProvider : ILoggerProvider
    {
        // One logger for every category. It holds no state and does not name itself in what it
        // publishes, so a per-category instance would be an allocation per category for nothing.
        private static readonly CountingLogger Shared = new CountingLogger();

        /// <summary>
        /// Returns the counting logger. The category is not used.
        /// </summary>
        /// <param name="categoryName">Ignored.</param>
        /// <returns>The shared counting logger.</returns>
        public ILogger CreateLogger(string categoryName) => Shared;

        /// <summary>
        /// Nothing to release. The recorder is owned by <see cref="LogWriteRateMonitor"/>, which
        /// outlives the container this provider is registered in.
        /// </summary>
        public void Dispose()
        {
        }

        private sealed class CountingLogger : ILogger
        {
            // Explicit, because Microsoft.Extensions.Logging.Abstractions constrains TState to
            // notnull from 7.0 onwards and does not before it. An implicit implementation has to
            // spell the constraint out and would then be wrong on one side of that line; an
            // explicit one inherits whichever constraint the referenced abstraction declares.
            IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

            /// <remarks>
            /// See the note on the provider. This is false on purpose and must stay false.
            /// </remarks>
            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // The formatter is deliberately never called. Rendering the message is most of
                // what a log write costs, and doing it a second time to count it would double the
                // cost of logging on a site that is already logging too much.
                LogWriteRateMonitor.Current?.Record(Severity(logLevel));
            }

            private static LogWriteSeverity Severity(LogLevel level)
            {
                switch (level)
                {
                    case LogLevel.Critical:
                    case LogLevel.Error:
                        return LogWriteSeverity.Error;

                    case LogLevel.Warning:
                        return LogWriteSeverity.Warning;

                    default:
                        return LogWriteSeverity.Normal;
                }
            }
        }

        /// <remarks>
        /// A shared no-op rather than null. Returning null is legal against the nullable signature,
        /// but scopes are begun by callers we do not control and a singleton that does nothing is
        /// cheaper than finding out which of them handles a null.
        /// </remarks>
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
#endif
