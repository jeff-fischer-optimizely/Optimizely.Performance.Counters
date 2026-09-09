#if !NET472
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Optimizely.Performance.Counters.Core.Http;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.Runtime.Http;

namespace Optimizely.Performance.Counters.Tests.Http
{
    /// <summary>
    /// The V12 and V13 sink: the middleware that turns a finished response into a
    /// <see cref="ResponseCacheSummary"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here is about the seam rather than the arithmetic - which four headers are read,
    /// when they are read, and what happens when nothing is listening. The classification is covered
    /// by <see cref="ResponseCacheSummaryTests"/> and the counters by
    /// <see cref="HttpCacheabilityTests"/>.
    /// </para>
    /// <para>
    /// A header name misspelt by one character is the failure this exists for. It cannot fail a
    /// request, cannot fail a build, and presents as a site whose responses apparently carry no
    /// validators at all.
    /// </para>
    /// </remarks>
    [Collection(HttpCacheabilityCollection.Name)]
    public class HttpCacheabilityMiddlewareTests : IDisposable
    {
        public void Dispose() => HttpCacheabilityMonitor.Stop();

        [Fact]
        public async Task Headers_set_by_the_rest_of_the_pipeline_are_the_ones_measured()
        {
            // The whole reason for measuring at OnStarting. Anything downstream - response caching,
            // static files, an action filter, the site's own code - may set these at any point up to
            // the moment the response is written, so a middleware that read the headers after
            // awaiting next would be reading them before the last writer had finished.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            var context = await Run(response =>
            {
                response.Headers["Cache-Control"] = "public, max-age=600";
                response.Headers["ETag"] = "\"abc\"";
            });

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.True(context.Response.HasStarted);
            Assert.Equal(100d, ValueOf(tracker, Names.PublicPercent));
            Assert.Equal(100d, ValueOf(tracker, Names.ValidatorPercent));
            Assert.Equal(600d, ValueOf(tracker, Names.FreshnessSeconds));
        }

        [Fact]
        public async Task A_response_with_no_caching_headers_is_still_counted()
        {
            // Not skipped. "This site says nothing about caching" is the finding, and a middleware
            // that only measured responses which had already said something could never report it.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            await Run(_ => { });

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(100d, ValueOf(tracker, Names.NoDirectivePercent));
        }

        [Fact]
        public async Task Last_modified_counts_as_a_validator_as_much_as_an_etag()
        {
            // Either one lets a client send a conditional request, which is the thing being counted.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            await Run(response =>
                response.Headers["Last-Modified"] = "Wed, 21 Oct 2015 07:28:00 GMT");

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(100d, ValueOf(tracker, Names.ValidatorPercent));
        }

        [Fact]
        public async Task Expires_is_read_when_that_is_all_the_response_sets()
        {
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            await Run(response =>
                response.Headers["Expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("R"));

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(100d, ValueOf(tracker, Names.PublicPercent));
        }

        [Fact]
        public async Task A_cookie_on_a_shared_cacheable_response_is_found_through_the_real_headers()
        {
            // Set-Cookie reaches the header collection by a different route than the rest - through
            // Response.Cookies - so this is the one case where reading the header dictionary could
            // plausibly miss something the response really did send.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            await Run(response =>
            {
                response.Headers["Cache-Control"] = "public, max-age=600";
                response.Cookies.Append("session", "value");
            });

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(100d, ValueOf(tracker, Names.SharedCacheConflictPercent));
        }

        [Fact]
        public async Task A_repeated_header_is_read_as_the_whole_of_what_was_sent()
        {
            // Legal, and rare, because Cache-Control is a comma-separated list precisely so it does
            // not have to repeat. Taking only the first value would read this as shared-cacheable.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            await Run(response =>
                response.Headers["Cache-Control"] = new[] { "public", "no-store" });

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(100d, ValueOf(tracker, Names.NoStorePercent));
        }

        [Fact]
        public async Task Nothing_is_measured_before_the_monitor_publishes_a_recorder()
        {
            // The normal state during startup. The middleware is built when the host builds its
            // pipeline, which is before any Optimizely initialization module has run, so it has to
            // sit inert rather than assume somebody is listening.
            var context = await Run(response =>
                response.Headers["Cache-Control"] = "public, max-age=600");

            Assert.Null(HttpCacheabilityMonitor.Current);
            Assert.True(context.Response.HasStarted);
        }

        [Fact]
        public async Task Nothing_is_measured_once_the_recorder_has_switched_itself_off()
        {
            // Checked before the callback is even registered, so a recorder that gave up removes the
            // header reads from the request path as well as the counters.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(
                tracker, new HttpCacheabilityOptions { Enabled = false });

            await Run(response => response.Headers["Cache-Control"] = "public, max-age=600");

            Assert.Empty(tracker.Metrics);
        }

        [Fact]
        public async Task The_rest_of_the_pipeline_runs_whether_or_not_anything_is_listening()
        {
            var reached = 0;

            await Run(_ => { }, onNext: () => reached++);

            HttpCacheabilityMonitor.Start(new RecordingMetricTracker());
            await Run(_ => { }, onNext: () => reached++);

            Assert.Equal(2, reached);
        }

        [Fact]
        public async Task A_response_that_never_starts_is_never_measured()
        {
            // A request the host abandons - a client that disconnected, a pipeline branch that threw
            // after this frame returned. OnStarting is the guarantee that a response really went out.
            var tracker = new RecordingMetricTracker();
            HttpCacheabilityMonitor.Start(tracker);

            var context = NewContext();
            var middleware = new HttpCacheabilityMiddleware(_ => Task.CompletedTask);
            await middleware.InvokeAsync(context);

            MetricFlush.Run(HttpCacheabilityMonitor.Current!);

            Assert.Equal(0d, ValueOf(tracker, Names.ResponsesPerSecond));
        }

        [Fact]
        public void The_startup_filter_puts_the_measurement_in_front_of_the_site()
        {
            // OnStarting callbacks run in reverse registration order, so being the first middleware
            // registered is what makes this the last callback to run and therefore the one that sees
            // the finished header set. A filter that appended instead of prepending would still
            // work, quietly, on the headers of whatever ran before it.
            var order = new List<string>();
            var filter = new HttpCacheabilityStartupFilter();

            var configure = filter.Configure(_ => order.Add("site"));

            configure(new RecordingApplicationBuilder(() => order.Add("measurement")));

            Assert.Equal(new[] { "measurement", "site" }, order);
        }

        [Fact]
        public void The_startup_filter_will_not_silently_drop_the_site_s_own_configuration()
        {
            Assert.Throws<ArgumentNullException>(
                () => new HttpCacheabilityStartupFilter().Configure(null!));
        }

        [Fact]
        public void A_middleware_with_nothing_to_hand_on_to_is_refused_at_construction()
        {
            Assert.Throws<ArgumentNullException>(() => new HttpCacheabilityMiddleware(null!));
        }

        /// <summary>
        /// Runs one request through the middleware, lets <paramref name="setHeaders"/> stand in for
        /// the rest of the pipeline, and then starts the response so the measurement fires.
        /// </summary>
        private static async Task<HttpContext> Run(
            Action<HttpResponse> setHeaders, Action? onNext = null)
        {
            var context = NewContext();

            var middleware = new HttpCacheabilityMiddleware(ctx =>
            {
                onNext?.Invoke();
                setHeaders(ctx.Response);
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(context);

            // Stands in for the server writing the response, which is the only thing that runs the
            // OnStarting callbacks and therefore the only thing that measures anything.
            await ((StartableResponseFeature)context.Features.Get<IHttpResponseFeature>()!)
                .FireOnStarting();

            return context;
        }

        private static HttpContext NewContext()
        {
            var context = new DefaultHttpContext();

            // The stock response feature ignores OnStarting entirely, which would make every test
            // here pass by measuring nothing. This one behaves the way a real server's does.
            context.Features.Set<IHttpResponseFeature>(new StartableResponseFeature());
            context.Request.Path = "/en/products";
            context.Request.QueryString = new QueryString("?q=secret");

            return context;
        }

        private static double ValueOf(RecordingMetricTracker tracker, string name) =>
            tracker.Metrics.Last(metric => metric.Name == name).Value;

        /// <summary>
        /// The smallest response feature that actually fires its <c>OnStarting</c> callbacks, which
        /// is the behaviour the middleware is built on and the one <c>DefaultHttpContext</c> leaves
        /// out.
        /// </summary>
        private sealed class StartableResponseFeature : IHttpResponseFeature
        {
            private readonly List<(Func<object, Task> Callback, object State)> _onStarting =
                new List<(Func<object, Task>, object)>();

            public int StatusCode { get; set; } = 200;

            public string? ReasonPhrase { get; set; }

            public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

            public Stream Body { get; set; } = Stream.Null;

            public bool HasStarted { get; private set; }

            public void OnStarting(Func<object, Task> callback, object state) =>
                _onStarting.Add((callback, state));

            public void OnCompleted(Func<object, Task> callback, object state)
            {
            }

            /// <remarks>
            /// Reverse order, as a real server runs them: the callback registered first is the one
            /// that sees the finished headers, and the middleware's whole registration strategy
            /// depends on that.
            /// </remarks>
            internal Task FireOnStarting()
            {
                HasStarted = true;

                for (var i = _onStarting.Count - 1; i >= 0; i--)
                {
                    var (callback, state) = _onStarting[i];
                    callback(state).GetAwaiter().GetResult();
                }

                _onStarting.Clear();

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Enough of an <c>IApplicationBuilder</c> to record that <c>UseMiddleware</c> was called,
        /// without building a pipeline that would need a server to run.
        /// </summary>
        private sealed class RecordingApplicationBuilder : Microsoft.AspNetCore.Builder.IApplicationBuilder
        {
            private readonly Action _onUse;

            internal RecordingApplicationBuilder(Action onUse) => _onUse = onUse;

            public IServiceProvider ApplicationServices { get; set; } = null!;

            public IFeatureCollection ServerFeatures { get; } = new FeatureCollection();

            public IDictionary<string, object?> Properties { get; } =
                new Dictionary<string, object?>();

            public Microsoft.AspNetCore.Builder.IApplicationBuilder Use(
                Func<RequestDelegate, RequestDelegate> middleware)
            {
                _onUse();
                return this;
            }

            public Microsoft.AspNetCore.Builder.IApplicationBuilder New() => this;

            public RequestDelegate Build() => _ => Task.CompletedTask;
        }
    }
}
#endif
