#if !NET472
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Reads the caching headers off every response this host sends, on V12 and V13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measures at <c>OnStarting</c> rather than on the way back out through the pipeline, because
    /// the headers are not final until then. Anything that sets caching headers - the response
    /// caching middleware, static files, output caching, an action filter, the site's own code -
    /// may do so at any point up to the moment the response is written, and a middleware that read
    /// <c>Response.Headers</c> after awaiting <c>next</c> would be reading them before the last
    /// writer had finished. It would also miss every response written by something that never
    /// returns through this frame at all.
    /// </para>
    /// <para>
    /// Registered first in the pipeline, which is what makes that work: <c>OnStarting</c> callbacks
    /// run in reverse registration order, so being the earliest to register makes this the last to
    /// run and therefore the one that sees the finished header set.
    /// </para>
    /// <para>
    /// Every response is classified, including redirects, 304s and errors. What a site says about
    /// caching its failures is part of how cacheable the site is, and excluding them would quietly
    /// change the denominator every share is computed against.
    /// </para>
    /// </remarks>
    public sealed class HttpCacheabilityMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly Func<object, Task> _onStarting;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpCacheabilityMiddleware"/> class.
        /// </summary>
        /// <param name="next">The rest of the pipeline.</param>
        public HttpCacheabilityMiddleware(RequestDelegate next)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));

            // Built once, in the constructor, and the middleware is constructed once for the
            // lifetime of the pipeline. A lambda written inline at the OnStarting call site would
            // close over nothing but still allocate a delegate per request, on a path whose whole
            // justification is that it costs almost nothing.
            _onStarting = Measure;
        }

        /// <summary>
        /// Arranges for the response to be classified as it is sent, then hands on.
        /// </summary>
        /// <param name="context">The request being handled.</param>
        /// <returns>The rest of the pipeline's work.</returns>
        public Task InvokeAsync(HttpContext context)
        {
            var recorder = HttpCacheabilityMonitor.Current;

            if (recorder != null && !recorder.IsDisabled)
            {
                context.Response.OnStarting(_onStarting, context);
            }

            return _next(context);
        }

        private static Task Measure(object state)
        {
            var recorder = HttpCacheabilityMonitor.Current;

            if (recorder == null)
            {
                return Task.CompletedTask;
            }

            try
            {
                var context = (HttpContext)state;
                var headers = context.Response.Headers;

                // Spelled out rather than taken from Microsoft.Net.Http.Headers, so that this and
                // the V11 module read the same four names from the same four literals. A constant
                // from a header-names package would be one more assembly in the graph for no
                // protection: these are fixed by the HTTP specification, not by a library version.
                var summary = ResponseCacheSummary.Describe(
                    First(headers["Cache-Control"]),
                    First(headers["Expires"]),
                    headers.ContainsKey("ETag") || headers.ContainsKey("Last-Modified"),
                    headers.ContainsKey("Set-Cookie"));

                // The path, never the query string. It reaches the log only when a shared-cache
                // conflict is reported, and a route is enough to find the code that set the
                // headers; a query string is somebody's search terms.
                recorder.Record(summary, context.Request.Path.Value);
            }
            catch (Exception)
            {
                // This runs while the response is being written. Nothing measured here is worth
                // failing a request that had already succeeded, and the recorder counts its own
                // failures and switches itself off if they persist.
            }

            return Task.CompletedTask;
        }

        /// <remarks>
        /// A header sent more than once is joined only in that case, which is the rare one -
        /// <c>Cache-Control</c> is a comma-separated list precisely so it does not have to repeat.
        /// The single-value path, which is every ordinary response, returns the string the header
        /// dictionary is already holding.
        /// </remarks>
        private static string? First(StringValues values) =>
            values.Count == 0 ? null
            : values.Count == 1 ? values[0]
            : values.ToString();
    }

    /// <summary>
    /// Puts <see cref="HttpCacheabilityMiddleware"/> at the front of the request pipeline without
    /// the site having to add a line to its own startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>IStartupFilter</c> because there is nowhere else to do it from. This package installs
    /// itself through an Optimizely initialization module, which runs while the container is being
    /// configured and is finished long before anything builds an <c>IApplicationBuilder</c>. A
    /// filter is the framework's own answer to that: registered as a service during configuration,
    /// invoked later with the pipeline in hand.
    /// </para>
    /// <para>
    /// Registered through <c>TryAddEnumerable</c> by <see cref="HttpCacheabilityRegistration"/>,
    /// so a site with both the CMS and the Commerce package installed gets one filter and measures
    /// each response once.
    /// </para>
    /// </remarks>
    public sealed class HttpCacheabilityStartupFilter : IStartupFilter
    {
        /// <summary>
        /// Wraps the site's own pipeline configuration, prepending the measurement.
        /// </summary>
        /// <param name="next">The next configuration step.</param>
        /// <returns>Configuration that measures first, then does what the site asked for.</returns>
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            return builder =>
            {
                builder.UseMiddleware<HttpCacheabilityMiddleware>();
                next(builder);
            };
        }
    }
}
#endif
