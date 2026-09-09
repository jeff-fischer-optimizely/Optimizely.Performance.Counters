#if !NET472
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Http
{
    /// <summary>
    /// Registers the response cacheability measurement with an ASP.NET Core host, on V12 and V13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in Core rather than in the CMS and Commerce packages so that there is exactly one
    /// <see cref="HttpCacheabilityStartupFilter"/> type in the process. <c>TryAddEnumerable</c>
    /// de-duplicates by implementation type, so one type means a site with both packages installed
    /// registers one filter and classifies each response once. Two copies of the same source
    /// compiled into two assemblies would be two types, and would count everything twice.
    /// </para>
    /// <para>
    /// Also why the CMS and Commerce projects call in here rather than doing this themselves:
    /// neither of them references ASP.NET Core, and neither needs to.
    /// </para>
    /// </remarks>
    public static class HttpCacheabilityRegistration
    {
        /// <summary>
        /// Adds the startup filter that installs the measurement middleware.
        /// </summary>
        /// <param name="services">The container being configured.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>True if the registration went in.</returns>
        /// <remarks>
        /// Tolerates a host with no ASP.NET Core at all. Core takes a framework reference on
        /// <c>Microsoft.AspNetCore.App</c> but does not flow it to consumers, so a non-web host
        /// that uses Optimizely's initialization system - a scheduled job runner, an import tool -
        /// resolves nothing for it and fails to load the types below. That is not an error worth
        /// stopping for: such a host sends no responses, so there was nothing here to measure.
        /// </remarks>
        public static bool Register(IServiceCollection services, ILogger? logger = null)
        {
            if (services == null)
            {
                return false;
            }

            try
            {
                AddStartupFilter(services);
                return true;
            }
            catch (System.Exception ex)
            {
                logger?.LogDebug(
                    ex,
                    "Response cacheability measurement was not registered: this host has no " +
                    "ASP.NET Core request pipeline. The Optimizely.Runtime.Http counters will stay " +
                    "empty; nothing else is affected.");

                return false;
            }
        }

        /// <remarks>
        /// Separated and not inlined so that the assembly load happens here, inside the caller's
        /// try block, rather than when <see cref="Register"/> itself is compiled. The runtime
        /// resolves a method's type references as it JITs that method, so a body referring to
        /// <c>IStartupFilter</c> would throw before the first line of the enclosing <c>try</c> ran.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddStartupFilter(IServiceCollection services) =>
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStartupFilter, HttpCacheabilityStartupFilter>());
    }
}
#endif
