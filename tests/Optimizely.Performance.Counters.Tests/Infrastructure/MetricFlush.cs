using System;
using System.Reflection;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Triggers the timer-driven metric flush on a decorator without waiting sixty seconds for it.
    /// <para>
    /// The flush is private and driven by a <c>System.Threading.Timer</c>, which is right for
    /// production and untestable as-is. Reaching for it reflectively keeps the production type
    /// free of a test-only seam; the lookup fails loudly if the method is ever renamed.
    /// </para>
    /// </summary>
    public static class MetricFlush
    {
        private const string FlushMethod = "ReportMetrics";

        /// <summary>
        /// Runs the decorator's reporting callback once, synchronously.
        /// </summary>
        public static void Run(object decorator)
        {
            var method = decorator.GetType().GetMethod(
                FlushMethod,
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new InvalidOperationException(
                    $"{decorator.GetType().Name} has no private {FlushMethod} method. " +
                    "If the flush was renamed, update MetricFlush to match.");
            }

            method.Invoke(decorator, new object?[] { null });
        }
    }
}
