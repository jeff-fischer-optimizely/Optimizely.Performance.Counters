using System;
using System.Threading;
using EPiServer.Framework.Cache;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsCache;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Decorator for ISynchronizedObjectInstanceCache that instruments cache operations.
    /// Tracks: hit rate, miss rate, invalidation rate, total operations.
    /// <para>
    /// Cache reads are far too frequent to emit a counter per call, so hits and misses are
    /// accumulated in interlocked counters and flushed on a timer. That keeps the per-call cost
    /// to a single <see cref="Interlocked.Increment(ref long)"/>.
    /// </para>
    /// <para>
    /// CMS 13 moved these interfaces into the EPiServer.Cache package, dropped <c>Clear()</c>
    /// from IObjectInstanceCache and added a <c>Logger</c> property. Both differences are
    /// handled below.
    /// </para>
    /// </summary>
    public class InstrumentedSynchronizedObjectInstanceCache : PeriodicMetricReporter, ISynchronizedObjectInstanceCache
    {
        private readonly ISynchronizedObjectInstanceCache _inner;
        private readonly IMetricTracker _metricTracker;
        private readonly ILogger<InstrumentedSynchronizedObjectInstanceCache> _logger;

        private long _cacheHits;
        private long _cacheMisses;
        private long _invalidations;

        /// <summary>
        /// Wraps the cache resolved by the container and starts the metric flush timer.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        public InstrumentedSynchronizedObjectInstanceCache(
            ISynchronizedObjectInstanceCache inner,
            IMetricTracker metricTracker,
            ILogger<InstrumentedSynchronizedObjectInstanceCache> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _metricTracker = metricTracker ?? throw new ArgumentNullException(nameof(metricTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region IObjectInstanceCache

        /// <inheritdoc />
        public object Get(string key)
        {
            var value = _inner.Get(key);

            if (value != null)
            {
                Interlocked.Increment(ref _cacheHits);
            }
            else
            {
                Interlocked.Increment(ref _cacheMisses);
            }

            // A miss is null by design; the interface is not annotated for it.
            return value!;
        }

        /// <inheritdoc />
        public void Insert(string key, object value, CacheEvictionPolicy evictionPolicy) =>
            _inner.Insert(key, value, evictionPolicy);

        /// <inheritdoc />
        public void Remove(string key)
        {
            _inner.Remove(key);
            Interlocked.Increment(ref _invalidations);
        }

#if CMS13
        /// <inheritdoc />
        public ILogger<IObjectInstanceCache> Logger => _inner.Logger;
#else
        /// <inheritdoc />
        [Obsolete("Mirrors the obsolete IObjectInstanceCache.Clear member.")]
        public void Clear()
        {
            _inner.Clear();
            // A clear is a bulk invalidation. Counting it as one keeps the rate honest about
            // how often invalidation happens, which is what the counter is for.
            Interlocked.Increment(ref _invalidations);
        }
#endif

        #endregion

        #region ISynchronizedObjectInstanceCache

        /// <inheritdoc />
        [Obsolete("Mirrors the obsolete ISynchronizedObjectInstanceCache.SynchronizationFailedStrategy member.")]
        public FailureRecoveryAction SynchronizationFailedStrategy
        {
            get => _inner.SynchronizationFailedStrategy;
            set => _inner.SynchronizationFailedStrategy = value;
        }

        /// <inheritdoc />
        [Obsolete("Mirrors the obsolete ISynchronizedObjectInstanceCache.ObjectInstanceCache member.")]
        public IObjectInstanceCache ObjectInstanceCache => _inner.ObjectInstanceCache;

        /// <inheritdoc />
        public void RemoveLocal(string key)
        {
            _inner.RemoveLocal(key);
            Interlocked.Increment(ref _invalidations);
        }

        /// <inheritdoc />
        public void RemoveRemote(string key)
        {
            _inner.RemoveRemote(key);
            Interlocked.Increment(ref _invalidations);
        }

        #endregion

        #region Instrumentation

        /// <inheritdoc />
        protected override void ReportMetrics(object? state)
        {
            try
            {
                var hits = Interlocked.Exchange(ref _cacheHits, 0);
                var misses = Interlocked.Exchange(ref _cacheMisses, 0);
                var invalidations = Interlocked.Exchange(ref _invalidations, 0);

                var total = hits + misses;
                var hitRate = total > 0 ? (double)hits / total * 100.0 : 0.0;

                if (total > 0)
                {
                    _metricTracker.TrackMetric(Names.HitRate, hitRate);
                    _metricTracker.TrackMetric(Names.MissRate, 100.0 - hitRate);
                }

                var invalidationsPerSecond = invalidations / ReportingIntervalSeconds;
                _metricTracker.TrackMetric(Names.InvalidationsPerSecond, invalidationsPerSecond);
                _metricTracker.TrackMetric(Names.Operations, total);

                _logger.LogDebug(
                    "Cache metrics - Hits: {Hits}, Misses: {Misses}, Hit Rate: {HitRate:F2}%, Invalidations/sec: {InvalidationsPerSec:F2}",
                    hits, misses, hitRate, invalidationsPerSecond);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting cache metrics");
            }
        }

        #endregion
    }
}
