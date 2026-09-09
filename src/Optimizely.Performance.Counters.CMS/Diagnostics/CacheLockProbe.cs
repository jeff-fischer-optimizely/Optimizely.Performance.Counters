using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;

namespace Optimizely.Performance.Counters.CMS.Diagnostics
{
    /// <summary>
    /// Samples how many threads are queued on Optimizely's cache lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optimizely's memory cache serialises every write behind one process-wide
    /// <see cref="ReaderWriterLockSlim"/>. The cache decorator already reports hit rate and
    /// invalidation rate; neither can show this, because every thread queued on the lock is about
    /// to record a hit, once it is let through. A queue here is the whole site waiting on cache
    /// invalidation while the hit rate looks excellent.
    /// </para>
    /// <para>
    /// The general contention counters cannot report it either. They count monitor contention
    /// across the process, and this is a reader/writer lock, which they do not see at all.
    /// </para>
    /// <para>
    /// This is the one part of the package that depends on Optimizely internals, so it is built to
    /// lose that dependency without consequence: if the lock cannot be found the probe logs once,
    /// reports itself unavailable and stops. Everything else keeps working, and the cache is never
    /// touched - the probe only ever reads counters off the lock, and never acquires it.
    /// </para>
    /// </remarks>
    public sealed class CacheLockProbe : SamplingProbe
    {
        private readonly IMetricTracker _metrics;
        private readonly CacheLockProbeOptions _options;
        private readonly ILogger? _logger;

        private ReaderWriterLockSlim? _cacheLock;

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheLockProbe"/> class.
        /// </summary>
        /// <param name="metrics">Where samples are published.</param>
        /// <param name="options">Probe options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        public CacheLockProbe(
            IMetricTracker metrics,
            CacheLockProbeOptions? options = null,
            ILogger<CacheLockProbe>? logger = null)
            : this(metrics, options ?? new CacheLockProbeOptions(), (ILogger?)logger)
        {
        }

        private CacheLockProbe(IMetricTracker metrics, CacheLockProbeOptions options, ILogger? logger)
            : base(
                logger,
                "Optimizely Cache Lock Probe",
                options.Enabled,
                options.SampleInterval,
                options.LogsPerMinute)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Gets whether the cache lock was found and is being sampled.
        /// </summary>
        public bool IsAvailable => Volatile.Read(ref _cacheLock) != null;

        /// <remarks>
        /// Resolves the lock once. Declining is an ordinary outcome here rather than a fault: the
        /// field being read is not part of any public API, so it is expected to lapse across an
        /// Optimizely upgrade, and the site should be told what happened without being alarmed.
        /// </remarks>
        protected override bool OnStarting()
        {
            string diagnostic;
            bool located;

            try
            {
                located = CacheLockLocator.TryGetLock(
                    CacheLockLocator.FindCacheType(), out var cacheLock, out diagnostic);

                _cacheLock = cacheLock;
            }
            catch (Exception ex)
            {
                // TryGetLock is written not to throw, but it is reflection over a type this package
                // does not own, and being wrong about that must not cost the site its startup.
                located = false;
                diagnostic = $"Locating the Optimizely cache lock threw: {ex.Message}";
            }

            if (!located)
            {
                _logger?.LogInformation(
                    "Cache lock contention will not be reported. {Diagnostic} This metric reads an " +
                    "Optimizely internal that is not part of any public API, so it is expected to " +
                    "lapse across upgrades. No other counter is affected and cache behaviour is " +
                    "unchanged.",
                    diagnostic);

                return false;
            }

            _logger?.LogInformation("{Diagnostic}", diagnostic);
            return true;
        }

        /// <inheritdoc />
        protected override void Sample()
        {
            var cacheLock = Volatile.Read(ref _cacheLock);
            if (cacheLock == null)
            {
                return;
            }

            try
            {
                // Plain property reads on a framework type. No reflection runs here, and the lock
                // is never acquired, so sampling cannot itself add contention.
                var waitingWriters = cacheLock.WaitingWriteCount;
                var waitingReaders = cacheLock.WaitingReadCount;
                var currentReaders = cacheLock.CurrentReadCount;
                var writeHeld = cacheLock.IsWriteLockHeld;

                _metrics.TrackMetric(CounterNames.CmsCache.LockWaitingWriters, waitingWriters);
                _metrics.TrackMetric(CounterNames.CmsCache.LockWaitingReaders, waitingReaders);
                _metrics.TrackMetric(CounterNames.CmsCache.LockCurrentReaders, currentReaders);

                // Emitted as 0 or 100 rather than 0 or 1, so the mean the EventCounter computes over
                // its collection interval is already the percentage the counter's name promises.
                _metrics.TrackMetric(CounterNames.CmsCache.LockWriteHeldPercent, writeHeld ? 100 : 0);

                var queued = waitingReaders + waitingWriters;

                if (_options.QueueDepthThreshold > 0 && queued >= _options.QueueDepthThreshold)
                {
                    TryLog(log => log.LogWarning(
                        "{Queued} threads are queued on the Optimizely cache lock ({Writers} waiting " +
                        "to write, {Readers} waiting to read). Writes to the cache are exclusive, so " +
                        "while this queue exists no thread can read from the cache at all. Hit rate " +
                        "will not show it - every one of those threads is about to record a hit.",
                        queued,
                        waitingWriters,
                        waitingReaders));
                }
            }
            catch (Exception ex)
            {
                // Stop rather than keep failing: the lock reference is resolved once, so a fault
                // reading it will recur on every sample until the process restarts.
                Volatile.Write(ref _cacheLock, null);

                TryLog(log => log.LogWarning(
                    ex,
                    "Sampling the Optimizely cache lock failed; the metric has been stopped for the " +
                    "lifetime of this process. Cache behaviour is unaffected."));
            }
        }
    }
}
