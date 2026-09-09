using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Configuration;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsCache;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Turns measured cache cascades into counters and, for the rare ones big enough to matter, log
    /// entries naming the key responsible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the two layers that observe a cascade. The cache decorator delimits the operation
    /// and names it; <c>InstrumentedMemoryCache</c> one layer down counts the entries and sees the
    /// eviction reasons. Neither can report without the other, so both report through here.
    /// </para>
    /// <para>
    /// Nothing here may surface a fault into the cache operation that called it. Every public method
    /// swallows its own exceptions, and if they keep happening the recorder switches itself off for
    /// the rest of the process rather than burning CPU on an exception path during what is probably
    /// already an incident.
    /// </para>
    /// <para>
    /// Cache keys are never used as counter names. Keys are unbounded, so a name per key would mean
    /// a time series per key. Keys appear in logs only.
    /// </para>
    /// </remarks>
    public sealed class CacheCascadeRecorder
    {
        private readonly IMetricTracker _metrics;
        private readonly CacheCascadeOptions _options;
        private readonly ILogger? _logger;

        private int _consecutiveFailures;
        private int _disabled;

        private long _logWindowStartTicks;
        private int _logsInWindow;

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheCascadeRecorder"/> class.
        /// </summary>
        /// <param name="metrics">Where measurements are published.</param>
        /// <param name="options">Instrumentation options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        public CacheCascadeRecorder(
            IMetricTracker metrics,
            CacheCascadeOptions? options = null,
            ILogger<CacheCascadeRecorder>? logger = null)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options ?? new CacheCascadeOptions();
            _logger = logger;
            _logWindowStartTicks = DateTime.UtcNow.Ticks;

            // The module reads the same flag and skips decorating IMemoryCache entirely, which is
            // where the saving is. Honouring it here as well means a recorder constructed directly
            // behaves the way its options say it will, rather than depending on who built it.
            _disabled = _options.Enabled ? 0 : 1;
        }

        /// <summary>
        /// Gets whether the recorder is off, either because configuration disabled it or because
        /// it switched itself off after repeated failures.
        /// </summary>
        /// <remarks>
        /// Read by the decorators before they do any measuring, so that switching off removes the
        /// cost as well as the counters.
        /// </remarks>
        public bool IsDisabled => Volatile.Read(ref _disabled) != 0;

        /// <summary>
        /// Records a completed removal and the cascade it caused.
        /// </summary>
        /// <param name="path">How the removal was requested.</param>
        /// <param name="key">The key the caller asked to remove.</param>
        /// <param name="entriesRemoved">Entries removed in total, including <paramref name="key"/>.</param>
        /// <param name="elapsedMilliseconds">How long the operation took.</param>
        public void RecordRemoval(
            CacheRemovalPath path, string key, int entriesRemoved, double elapsedMilliseconds)
        {
            if (IsDisabled)
            {
                return;
            }

            try
            {
                _metrics.TrackMetric(FanOutCounterFor(path), entriesRemoved);

                // Only for the invalidation paths. An insert's elapsed time is mostly the write,
                // which is not what this counter is about - it exists to say how long the cache
                // was closed to readers.
                if (path != CacheRemovalPath.Insert)
                {
                    _metrics.TrackMetric(Names.RemovalDurationMs, elapsedMilliseconds);
                }

                if (_options.LogLargeRemovals
                    && _options.LargeRemovalThreshold > 0
                    && entriesRemoved >= _options.LargeRemovalThreshold
                    && TryTakeLogSlot())
                {
                    _logger?.LogWarning(
                        "Cache removal of {Key} via {Path} cascaded to {EntriesRemoved} entries in " +
                        "{ElapsedMs:F1} ms. The cache was held under its write lock for that time, so " +
                        "no thread could read from it. This key sits above a large dependency subtree; " +
                        "consider narrowing what depends on it.",
                        key,
                        path,
                        entriesRemoved,
                        elapsedMilliseconds);
                }

                Succeeded();
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        /// <summary>
        /// Records that an entry was evicted, against the counter for the reason given.
        /// </summary>
        /// <param name="counterName">
        /// One of the eviction counters on <see cref="CounterNames.CmsCache"/>. Chosen by the
        /// caller, which is the layer that has the reason code.
        /// </param>
        public void RecordEviction(string counterName)
        {
            if (IsDisabled)
            {
                return;
            }

            try
            {
                _metrics.TrackMetric(counterName, 1);
                Succeeded();
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        /// <summary>
        /// Records the lifetime requested for an entry being inserted.
        /// </summary>
        /// <param name="timeToLive">The requested lifetime. Ignored when not positive.</param>
        public void RecordInsertTimeToLive(TimeSpan timeToLive)
        {
            if (IsDisabled || timeToLive <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                _metrics.TrackMetric(Names.InsertTtlSeconds, timeToLive.TotalSeconds);
                Succeeded();
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        /// <remarks>
        /// Private, and unit tested in both directions, for the same reason as the garbage
        /// collector probe's generation mapping: a path that reported to a counter the registry does
        /// not know would chart empty, and a registered counter no path reports to would chart empty
        /// too. Neither is visible from either side alone.
        /// </remarks>
        private static string FanOutCounterFor(CacheRemovalPath path)
        {
            switch (path)
            {
                case CacheRemovalPath.Remote:
                    return Names.RemoteRemovalFanOut;
                case CacheRemovalPath.Insert:
                    return Names.InsertFanOut;
                default:
                    return Names.RemovalFanOut;
            }
        }

        private void Succeeded()
        {
            if (Volatile.Read(ref _consecutiveFailures) != 0)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
        }

        private void Failed(Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures < _options.FailureThreshold)
            {
                return;
            }

            if (Interlocked.Exchange(ref _disabled, 1) == 0)
            {
                try
                {
                    _logger?.LogError(
                        ex,
                        "Cache instrumentation failed {Failures} times in a row and has been disabled " +
                        "for the lifetime of this process. Cache behaviour is unaffected; only the " +
                        "cascade counters stop. Restart the application to re-enable it.",
                        failures);
                }
                catch
                {
                    // Logging failed while shutting down for repeated failures. There is nowhere
                    // left to report this, and it must not reach the cache.
                }
            }
        }

        /// <summary>
        /// Applies the per-minute cap on threshold logging, so that a cache collapsing over and over
        /// cannot flood the log at the moment the site can least afford it.
        /// </summary>
        private bool TryTakeLogSlot()
        {
            if (_options.LargeRemovalLogsPerMinute <= 0)
            {
                return false;
            }

            var now = DateTime.UtcNow.Ticks;
            var windowStart = Interlocked.Read(ref _logWindowStartTicks);

            // Only the thread that wins the exchange opens the new window; the others fall through
            // and compete for a slot inside it.
            if (now - windowStart >= TimeSpan.TicksPerMinute
                && Interlocked.CompareExchange(ref _logWindowStartTicks, now, windowStart) == windowStart)
            {
                Interlocked.Exchange(ref _logsInWindow, 0);
            }

            return Interlocked.Increment(ref _logsInWindow) <= _options.LargeRemovalLogsPerMinute;
        }
    }
}
