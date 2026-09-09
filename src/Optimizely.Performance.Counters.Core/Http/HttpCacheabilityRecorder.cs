using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Optimizely.Performance.Counters.Core.Telemetry;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.Runtime.Http;

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Classifies outbound responses as they are sent and publishes the mix once a minute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caching is the only part of a site's performance whose effects are invisible to the site.
    /// Every other counter in this package measures work the process did; this one measures work it
    /// told somebody else not to make it do again. When that instruction is missing the request
    /// comes back, and it arrives looking exactly like ordinary traffic - so the origin sees load
    /// it has no reason to question, and the evidence that it was avoidable lives in a CDN report
    /// or a browser nobody is watching.
    /// </para>
    /// <para>
    /// Shares rather than counts, because that is what is actionable. "Four hundred responses set
    /// no cache headers" depends on how busy the minute was; "sixty percent of what this site sends
    /// says nothing about caching" is the same statement at any traffic level, comparable between
    /// two environments and across a release.
    /// </para>
    /// <para>
    /// Telemetry-agnostic and framework-agnostic, like <c>LogWriteRateRecorder</c>. Nothing here
    /// knows what an <c>HttpContext</c> is; the two sinks that do - the middleware on V12 and V13,
    /// the HTTP module on V11 - reduce a response to a <see cref="ResponseCacheSummary"/> before
    /// they call in.
    /// </para>
    /// </remarks>
    public sealed class HttpCacheabilityRecorder : PeriodicMetricReporter
    {
        private readonly IMetricTracker _metrics;
        private readonly HttpCacheabilityOptions _options;
        private readonly ILogger? _logger;

        private long _responses;
        private long _public;
        private long _private;
        private long _revalidate;
        private long _noStore;
        private long _noDirective;
        private long _validators;
        private long _conflicts;

        private int _consecutiveFailures;
        private int _disabled;

        private long _logWindowStartTicks;
        private int _logsInWindow;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpCacheabilityRecorder"/> class.
        /// </summary>
        /// <param name="metrics">Where the shares are published.</param>
        /// <param name="options">Options. Null takes the defaults.</param>
        /// <param name="logger">Log sink. May be null.</param>
        /// <remarks>
        /// The logger is the plain <see cref="ILogger"/> rather than the generic one, because the
        /// only thing that constructs this is <see cref="HttpCacheabilityMonitor"/> and what it has
        /// in hand is the initialization module's own logger. Asking for the generic form would mean
        /// the monitor could not forward what it was given, and the conflict finding - the one
        /// output of this type that is a sentence rather than a number - would never be written.
        /// </remarks>
        public HttpCacheabilityRecorder(
            IMetricTracker metrics,
            HttpCacheabilityOptions? options = null,
            ILogger? logger = null)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _options = options ?? new HttpCacheabilityOptions();
            _logger = logger;
            _logWindowStartTicks = DateTime.UtcNow.Ticks;
            _disabled = _options.Enabled ? 0 : 1;
        }

        /// <summary>
        /// Gets whether the recorder is off, either because configuration disabled it or because it
        /// switched itself off after repeated failures.
        /// </summary>
        /// <remarks>
        /// Read by the sinks before they classify anything, so switching off removes the header
        /// reads as well as the counters.
        /// </remarks>
        public bool IsDisabled => Volatile.Read(ref _disabled) != 0;

        /// <summary>
        /// Records one outbound response.
        /// </summary>
        /// <param name="summary">How cacheable the response is.</param>
        /// <param name="path">
        /// The request path, used only when a shared-cache conflict is logged. May be null.
        /// </param>
        /// <remarks>
        /// <para>
        /// Called on the thread that is about to write the response headers, so on a busy site this
        /// is every request thread. Two to four interlocked increments and, for the responses that
        /// stated a lifetime, one metric write. No allocation, and in particular nothing is done
        /// with <paramref name="path"/> unless a conflict is being logged - the caller is expected
        /// to hand over a string it already has rather than build one.
        /// </para>
        /// <para>
        /// The path is passed without its query string, and the sinks are written to keep it that
        /// way. A query string carries search terms, tokens and identifiers; a log line naming the
        /// route is enough to find the code, and the rest is somebody's data.
        /// </para>
        /// </remarks>
        public void Record(ResponseCacheSummary summary, string? path = null)
        {
            if (IsDisabled)
            {
                return;
            }

            try
            {
                Interlocked.Increment(ref _responses);

                switch (summary.Cacheability)
                {
                    case ResponseCacheability.Public:
                        Interlocked.Increment(ref _public);
                        break;

                    case ResponseCacheability.Private:
                        Interlocked.Increment(ref _private);
                        break;

                    case ResponseCacheability.Revalidate:
                        Interlocked.Increment(ref _revalidate);
                        break;

                    case ResponseCacheability.NoStore:
                        Interlocked.Increment(ref _noStore);
                        break;

                    default:
                        Interlocked.Increment(ref _noDirective);
                        break;
                }

                if (summary.HasValidator)
                {
                    Interlocked.Increment(ref _validators);
                }

                // Published per response rather than accumulated, so the counter's own aggregates
                // do the work: the distribution of freshness lifetimes is the interesting part, and
                // a mean the recorder computed itself would throw away the maximum. Same treatment
                // as the cache decorator's InsertTtlSeconds.
                if (summary.HasFreshness)
                {
                    _metrics.TrackMetric(Names.FreshnessSeconds, summary.FreshnessSeconds);
                }

                if (summary.SharedCacheConflict)
                {
                    Interlocked.Increment(ref _conflicts);
                    LogConflict(path);
                }

                Succeeded();
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        /// <summary>
        /// Publishes the mix for the interval just ended and starts the next one.
        /// </summary>
        /// <param name="state">Unused; required by the timer signature.</param>
        /// <remarks>
        /// The response rate is published every interval, zero included, because "this site sent
        /// nothing this minute" is a reading. The shares are not: with no responses behind them
        /// they would all report zero, and a chart showing nought percent cacheable during the
        /// quiet hours is worse than a gap. The gap is unambiguous next to a response rate sitting
        /// at zero, which is why the two are published together.
        /// </remarks>
        protected override void ReportMetrics(object? state)
        {
            try
            {
                var responses = Interlocked.Exchange(ref _responses, 0);
                var publicly = Interlocked.Exchange(ref _public, 0);
                var privately = Interlocked.Exchange(ref _private, 0);
                var revalidate = Interlocked.Exchange(ref _revalidate, 0);
                var noStore = Interlocked.Exchange(ref _noStore, 0);
                var noDirective = Interlocked.Exchange(ref _noDirective, 0);
                var validators = Interlocked.Exchange(ref _validators, 0);
                var conflicts = Interlocked.Exchange(ref _conflicts, 0);

                _metrics.TrackMetric(
                    Names.ResponsesPerSecond, responses / ReportingIntervalSeconds);

                if (responses == 0)
                {
                    return;
                }

                _metrics.TrackMetric(Names.PublicPercent, Share(publicly, responses));
                _metrics.TrackMetric(Names.PrivatePercent, Share(privately, responses));
                _metrics.TrackMetric(Names.RevalidatePercent, Share(revalidate, responses));
                _metrics.TrackMetric(Names.NoStorePercent, Share(noStore, responses));
                _metrics.TrackMetric(Names.NoDirectivePercent, Share(noDirective, responses));
                _metrics.TrackMetric(Names.ValidatorPercent, Share(validators, responses));
                _metrics.TrackMetric(
                    Names.SharedCacheConflictPercent, Share(conflicts, responses));
            }
            catch
            {
                // On a timer thread, so an escape here would take the process down. Deliberately
                // not routed through Failed: this is the publishing path, not the recording path,
                // and a tracker that is failing has nothing to do with whether the classification
                // in front of it still works.
            }
        }

        private static double Share(long part, long total) => part * 100.0 / total;

        private void LogConflict(string? path)
        {
            if (!_options.LogSharedCacheConflicts || !TryTakeLogSlot())
            {
                return;
            }

            _logger?.LogInformation(
                "Response for {Path} is marked shared-cacheable and also sets a cookie. A shared " +
                "cache will refuse to store it, so the cache headers on this route are buying " +
                "nothing and the origin will keep serving it. Either stop setting the cookie here " +
                "or mark the response private.",
                path ?? "(unknown)");
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
                        "Response cacheability instrumentation failed {Failures} times in a row and " +
                        "has been disabled for the lifetime of this process. Responses are " +
                        "unaffected; only the Optimizely.Runtime.Http counters stop. Restart the " +
                        "application to re-enable it.",
                        failures);
                }
                catch
                {
                    // Logging failed while shutting down for repeated failures. There is nowhere
                    // left to report this, and it must not reach the response.
                }
            }
        }

        /// <summary>
        /// Applies the per-minute cap on conflict logging, so that one misconfigured route serving
        /// steadily cannot fill the log with the same finding.
        /// </summary>
        private bool TryTakeLogSlot()
        {
            if (_options.SharedCacheConflictLogsPerMinute <= 0)
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

            return Interlocked.Increment(ref _logsInWindow) <= _options.SharedCacheConflictLogsPerMinute;
        }
    }
}
