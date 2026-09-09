using System;
using System.Reflection;
using Optimizely.Performance.Counters.Core.Diagnostics;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Runs a probe's start and sample steps synchronously, without its thread or its interval.
    /// <para>
    /// A probe samples on a dedicated background thread at an interval measured in seconds, which is
    /// right for production and untestable as-is. Reaching for the two protected seams reflectively
    /// keeps the production types free of a test-only entry point; the lookups fail loudly if either
    /// is renamed. Same approach as <see cref="MetricFlush"/>, for the same reason.
    /// </para>
    /// </summary>
    public static class ProbeDriver
    {
        private const string StartMethod = "OnStarting";
        private const string SampleMethod = "Sample";

        /// <summary>
        /// Runs the probe's start step and reports whether it agreed to run.
        /// </summary>
        public static bool Begin(SamplingProbe probe) => (bool)Invoke(probe, StartMethod)!;

        /// <summary>
        /// Takes one sample on the calling thread.
        /// </summary>
        public static void SampleOnce(SamplingProbe probe) => Invoke(probe, SampleMethod);

        private static object? Invoke(SamplingProbe probe, string name)
        {
            var method = probe.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new InvalidOperationException(
                    $"{probe.GetType().Name} has no protected {name} method. " +
                    "If the probe lifecycle was renamed, update ProbeDriver to match.");
            }

            try
            {
                return method.Invoke(probe, Array.Empty<object>());
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // Unwrap, so a failing probe reports its own exception rather than a reflection one.
                throw ex.InnerException;
            }
        }
    }
}
