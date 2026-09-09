#if NET472
using System;
using System.Runtime.CompilerServices;
using System.Web;
using Optimizely.Performance.Counters.Core.Http;

// Registers the module without the site editing web.config. PreApplicationStartMethod runs before
// the application starts, which is the only point at which a module can still be added to the
// pipeline programmatically - and the whole point of this package is that installing the NuGet
// package is the installation. Nothing in examples/V11/web.config.snippet.xml is required, and this
// is why.
[assembly: PreApplicationStartMethod(
    typeof(HttpCacheabilityModule), nameof(HttpCacheabilityModule.RegisterModule))]

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Reads the caching headers off every response this site sends, on V11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The System.Web counterpart of <c>HttpCacheabilityMiddleware</c>, and it measures at the same
    /// moment for the same reason: <c>AddOnSendingHeaders</c> fires as the headers are about to go
    /// on the wire, after everything that was going to set one has. Reading <c>Response.Headers</c>
    /// at <c>EndRequest</c> would be too early in a way that is easy to miss - System.Web does not
    /// materialise <c>Cache-Control</c> from <c>Response.Cache</c> until it generates the headers,
    /// so a page that configured its caching through <c>HttpCachePolicy</c>, which on V11 is most
    /// of them, would be counted as having said nothing at all.
    /// </para>
    /// <para>
    /// Inert until <see cref="HttpCacheabilityMonitor"/> publishes a recorder. ASP.NET builds its
    /// module pipeline before Optimizely initialization has run, so for the first part of the
    /// application's life there is nothing to report to; the module checks and does nothing, which
    /// also means an operator turning the feature off in configuration gets a module that costs one
    /// volatile read per request and stops there.
    /// </para>
    /// </remarks>
    public sealed class HttpCacheabilityModule : IHttpModule
    {
        private static readonly Action<HttpContext> SendingHeaders = Measure;

        /// <summary>
        /// Adds this module to the pipeline. Called by ASP.NET before application start.
        /// </summary>
        /// <remarks>
        /// Failure here is swallowed on purpose. This runs during application start-up, before the
        /// site has a log or an error page, and an exception escaping would stop the application
        /// from starting at all. A site with no cacheability counters is a gap in a chart; a site
        /// that will not start is an outage.
        /// </remarks>
        public static void RegisterModule()
        {
            try
            {
                Register();
            }
            catch (Exception)
            {
                // Nowhere to report it - see above.
            }
        }

        /// <summary>
        /// Subscribes to the request pipeline.
        /// </summary>
        /// <param name="context">The application this module instance belongs to.</param>
        public void Init(HttpApplication context)
        {
            if (context == null)
            {
                return;
            }

            // Classic mode has no managed response header collection: both Response.Headers and
            // AddOnSendingHeaders throw PlatformNotSupportedException there. Nothing can be
            // measured on such a site, so nothing is subscribed rather than an exception being
            // caught per request.
            if (!HttpRuntime.UsingIntegratedPipeline)
            {
                return;
            }

            context.BeginRequest += OnBeginRequest;
        }

        /// <summary>
        /// Releases resources. There are none; the module holds no per-application state.
        /// </summary>
        public void Dispose()
        {
        }

        /// <remarks>
        /// Not inlined, and called from inside a try, so that a missing
        /// <c>Microsoft.Web.Infrastructure</c> presents as a caught exception rather than as a
        /// type load failure while <see cref="RegisterModule"/> is being compiled.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Register() =>
            Microsoft.Web.Infrastructure.DynamicModuleHelper.DynamicModuleUtility.RegisterModule(
                typeof(HttpCacheabilityModule));

        private static void OnBeginRequest(object sender, EventArgs e)
        {
            var recorder = HttpCacheabilityMonitor.Current;

            if (recorder == null || recorder.IsDisabled)
            {
                return;
            }

            try
            {
                (sender as HttpApplication)?.Context?.Response.AddOnSendingHeaders(SendingHeaders);
            }
            catch (Exception)
            {
                // Reaching Response can throw on a request that has already been ended - a module
                // ahead of this one completing the request, for instance. Not worth failing over.
            }
        }

        private static void Measure(HttpContext context)
        {
            var recorder = HttpCacheabilityMonitor.Current;

            if (recorder == null)
            {
                return;
            }

            try
            {
                var response = context.Response;
                var headers = response.Headers;

                var summary = ResponseCacheSummary.Describe(
                    headers["Cache-Control"],
                    headers["Expires"],
                    headers["ETag"] != null || headers["Last-Modified"] != null,
                    // Both, because the two ways of setting a cookie converge later than this. A
                    // cookie added to Response.Cookies becomes a Set-Cookie header while the
                    // response headers are generated, which is the operation this callback is
                    // hooked into rather than one that has finished.
                    headers["Set-Cookie"] != null || response.Cookies.Count > 0,
                    DateTimeOffset.UtcNow);

                // Path, never the raw URL: Request.Path stops before the query string, which is
                // the property that makes it safe to put in a log line.
                recorder.Record(summary, context.Request.Path);
            }
            catch (Exception)
            {
                // The response is being written. Nothing measured here is worth failing a request
                // that had already succeeded.
            }
        }
    }
}
#endif
