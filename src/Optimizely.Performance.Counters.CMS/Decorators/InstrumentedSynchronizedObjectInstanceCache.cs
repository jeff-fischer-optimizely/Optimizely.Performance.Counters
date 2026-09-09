using System;
using System.Diagnostics;
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
    /// <para>
    /// Given a <see cref="CacheCascadeRecorder"/> it also measures the dependency cascade behind
    /// every write and invalidation. That is a different question from the rates above and the rates
    /// cannot answer it: a caller sees one <c>Remove</c> while the cache discards everything that
    /// depended on the key. This layer delimits the operation and names it;
    /// <c>InstrumentedMemoryCache</c> one layer down counts the entries - named rather than
    /// crefed, because that type is not compiled into the V11 build.
    /// </para>
    /// </summary>
    public class InstrumentedSynchronizedObjectInstanceCache : PeriodicMetricReporter, ISynchronizedObjectInstanceCache
    {
        private readonly ISynchronizedObjectInstanceCache _inner;
        private readonly IMetricTracker _metricTracker;
        private readonly ILogger<InstrumentedSynchronizedObjectInstanceCache> _logger;

        // Null when nothing is measuring cascades, which is the case on CMS 11 and whenever the
        // module could not install the memory cache layer. Every cascade path checks it first, so
        // absence costs nothing rather than reporting zeroes.
        private readonly CacheCascadeRecorder? _cascade;

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
            : this(inner, metricTracker, logger, cascade: null)
        {
        }

        /// <summary>
        /// As above, and additionally measures the dependency cascade behind each write and
        /// invalidation.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        /// <param name="cascade">
        /// Where cascades are reported. Null to measure only the rates above - which is what happens
        /// when the memory cache one layer down could not be decorated, since without it there is
        /// nothing counting the entries and every cascade would read as zero.
        /// </param>
        public InstrumentedSynchronizedObjectInstanceCache(
            ISynchronizedObjectInstanceCache inner,
            IMetricTracker metricTracker,
            ILogger<InstrumentedSynchronizedObjectInstanceCache> logger,
            CacheCascadeRecorder? cascade)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _metricTracker = metricTracker ?? throw new ArgumentNullException(nameof(metricTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cascade = cascade;
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
        /// <remarks>
        /// Inserting is not only a write. Optimizely removes the existing entry first, and that
        /// removal cascades, so replacing a key that other entries depend on discards the whole
        /// subtree beneath it - a common and thoroughly unobvious source of cache churn, which is
        /// why inserts are measured alongside invalidations.
        /// </remarks>
        public void Insert(string key, object value, CacheEvictionPolicy evictionPolicy)
        {
            _cascade?.RecordInsertTimeToLive(evictionPolicy?.Expiration ?? TimeSpan.Zero);

            // Every insert removes the previous entry first, so an insert that took nothing else
            // with it still counts as one removal. Reporting those would put a counter write on the
            // hottest path in the cache to say nothing, hence the minimum of two.
            Measure(
                CacheRemovalPath.Insert,
                key,
                () => _inner.Insert(key, value, evictionPolicy),
                minimumToReport: 2);
        }

        /// <inheritdoc />
        public void Remove(string key)
        {
            Measure(CacheRemovalPath.Local, key, () => _inner.Remove(key));
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
            Measure(CacheRemovalPath.Local, key, () => _inner.RemoveLocal(key));
            Interlocked.Increment(ref _invalidations);
        }

        /// <inheritdoc />
        public void RemoveRemote(string key)
        {
            Measure(CacheRemovalPath.Remote, key, () => _inner.RemoveRemote(key));
            Interlocked.Increment(ref _invalidations);
        }

        #endregion

        #region Instrumentation

        /// <summary>
        /// Runs a cache operation with cascade counting active, then reports what it cost.
        /// </summary>
        /// <remarks>
        /// The scope is closed in a <c>finally</c>, so an exception from the cache cannot leave the
        /// thread's counter stuck open, and reporting happens after the operation has finished, so
        /// that no instrumentation work runs while the cache's write lock is held.
        /// </remarks>
        /// <param name="path">How the removal was requested.</param>
        /// <param name="key">The key the caller asked for.</param>
        /// <param name="operate">The underlying cache call.</param>
        /// <param name="minimumToReport">
        /// The smallest cascade worth reporting. Invalidations report from one, because the count of
        /// single-entry removals is the denominator of the amplification ratio.
        /// </param>
        private void Measure(CacheRemovalPath path, string key, Action operate, int minimumToReport = 1)
        {
            var cascade = _cascade;

            if (cascade == null || cascade.IsDisabled)
            {
                operate();
                return;
            }

            var isOutermost = CacheRemovalScope.Begin();
            var start = Stopwatch.GetTimestamp();
            int removed;

            try
            {
                operate();
            }
            finally
            {
                removed = CacheRemovalScope.End(isOutermost);
            }

            if (isOutermost && removed >= minimumToReport)
            {
                cascade.RecordRemoval(
                    path,
                    key,
                    removed,
                    (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
            }
        }

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
