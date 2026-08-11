#if CMS13
using System;
using System.Threading;
using System.Threading.Tasks;
using EPiServer.Events;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CmsEvents;

namespace Optimizely.Performance.Counters.CMS.Decorators
{
    /// <summary>
    /// Decorator for IEventPublisher that instruments event publishing.
    /// Tracks: publish rate, broadcast rate, broadcast failure rate, average delivery time.
    /// <para>
    /// CMS 13 only. IEventPublisher does not exist in CMS 11 or 12 - those versions raise
    /// events through the static <c>Event</c> class, which cannot be decorated.
    /// </para>
    /// <para>
    /// Rates are accumulated in interlocked counters and flushed on a timer rather than emitted
    /// per call, since event publishing is a hot path during content operations.
    /// </para>
    /// </summary>
    public class InstrumentedEventPublisher : PeriodicMetricReporter, IEventPublisher
    {
        private readonly IEventPublisher _inner;
        private readonly IMetricTracker _metricTracker;
        private readonly ILogger<InstrumentedEventPublisher> _logger;

        private long _localEvents;
        private long _remoteEvents;
        private long _remoteEventFailures;
        private long _totalRemoteEventTicks;

        /// <summary>
        /// Wraps the publisher resolved by the container and starts the metric flush timer.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        public InstrumentedEventPublisher(
            IEventPublisher inner,
            IMetricTracker metricTracker,
            ILogger<InstrumentedEventPublisher> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _metricTracker = metricTracker ?? throw new ArgumentNullException(nameof(metricTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task PublishAsync<T>(T eventData, EventPublishingOptions options, CancellationToken cancellationToken)
            where T : IEventData
        {
            var timer = OperationTimer.Start();
            var broadcast = options?.Broadcast ?? false;
            try
            {
                // options is read defensively above, but is forwarded verbatim - substituting a
                // default here would silently change publishing behaviour.
                await _inner.PublishAsync(eventData, options!, cancellationToken).ConfigureAwait(false);
                RecordSuccess(broadcast, timer.ElapsedMilliseconds);
            }
            catch
            {
                RecordFailure(broadcast);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task PublishAsync<T>(T eventData, CancellationToken cancellationToken)
            where T : IEventData
        {
            var timer = OperationTimer.Start();
            try
            {
                await _inner.PublishAsync(eventData, cancellationToken).ConfigureAwait(false);
                RecordSuccess(broadcast: false, timer.ElapsedMilliseconds);
            }
            catch
            {
                RecordFailure(broadcast: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task PublishAsync(Type eventType, object eventData, EventPublishingOptions options, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            var broadcast = options?.Broadcast ?? false;
            try
            {
                // See the generic overload above: forwarded verbatim.
                await _inner.PublishAsync(eventType, eventData, options!, cancellationToken).ConfigureAwait(false);
                RecordSuccess(broadcast, timer.ElapsedMilliseconds);
            }
            catch
            {
                RecordFailure(broadcast);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task PublishAsync(Type eventType, object eventData, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            try
            {
                await _inner.PublishAsync(eventType, eventData, cancellationToken).ConfigureAwait(false);
                RecordSuccess(broadcast: false, timer.ElapsedMilliseconds);
            }
            catch
            {
                RecordFailure(broadcast: false);
                throw;
            }
        }

        #region Instrumentation

        private void RecordSuccess(bool broadcast, double elapsedMs)
        {
            if (broadcast)
            {
                Interlocked.Increment(ref _remoteEvents);
                // Accumulated in microseconds as a long so the running total stays exact under
                // Interlocked.Add; sub-millisecond publishes would otherwise round away to zero.
                Interlocked.Add(ref _totalRemoteEventTicks, (long)(elapsedMs * 1000.0));
            }
            else
            {
                Interlocked.Increment(ref _localEvents);
            }
        }

        private void RecordFailure(bool broadcast)
        {
            if (broadcast)
            {
                Interlocked.Increment(ref _remoteEventFailures);
            }
            else
            {
                Interlocked.Increment(ref _localEvents);
            }
        }

        /// <inheritdoc />
        protected override void ReportMetrics(object? state)
        {
            try
            {
                var localEvents = Interlocked.Exchange(ref _localEvents, 0);
                var remoteEvents = Interlocked.Exchange(ref _remoteEvents, 0);
                var remoteFailures = Interlocked.Exchange(ref _remoteEventFailures, 0);
                var totalRemoteMicroseconds = Interlocked.Exchange(ref _totalRemoteEventTicks, 0);

                var totalEvents = localEvents + remoteEvents;

                var eventsPerSecond = totalEvents / ReportingIntervalSeconds;
                var remoteEventsPerSecond = remoteEvents / ReportingIntervalSeconds;
                var remoteFailuresPerSecond = remoteFailures / ReportingIntervalSeconds;

                _metricTracker.TrackMetric(Names.EventsPerSecond, eventsPerSecond);
                _metricTracker.TrackMetric(Names.RemoteEventsPerSecond, remoteEventsPerSecond);
                _metricTracker.TrackMetric(Names.RemoteEventFailuresPerSecond, remoteFailuresPerSecond);

                // remoteEvents doubles as the divisor. It is incremented in exactly the same branch
                // that adds to _totalRemoteEventTicks and nowhere else - a failed broadcast lands
                // on _remoteEventFailures instead - so a second counter tracking the same number
                // was one more atomic per publish for nothing.
                if (remoteEvents > 0)
                {
                    var avgDeliveryTimeMs = totalRemoteMicroseconds / 1000.0 / remoteEvents;
                    _metricTracker.TrackMetric(Names.AverageRemoteEventDeliveryTimeMs, avgDeliveryTimeMs);
                }

                _logger.LogDebug(
                    "Event metrics - Total: {Total}/sec, Remote: {Remote}/sec, Failures: {Failures}/sec",
                    eventsPerSecond, remoteEventsPerSecond, remoteFailuresPerSecond);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting event metrics");
            }
        }

        #endregion
    }
}
#endif
