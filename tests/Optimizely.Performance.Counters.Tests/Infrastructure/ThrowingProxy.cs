using System;
using System.Reflection;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Marker exception thrown by <see cref="ThrowingProxy"/>, so a test can assert it saw the
    /// caller's own failure rather than an incidental one from the instrumentation.
    /// </summary>
    public sealed class InnerFailureException : Exception
    {
        public InnerFailureException(string member)
            : base($"The wrapped implementation failed in {member}.")
        {
        }
    }

    /// <summary>
    /// Stands in for an Optimizely implementation that fails. Used to check that a decorator
    /// records the failure and then rethrows - swallowing the exception would turn an outage into
    /// silently wrong content.
    /// </summary>
    public class ThrowingProxy : DispatchProxy
    {
        /// <summary>
        /// Creates a proxy implementing <typeparamref name="T"/> whose every member throws.
        /// </summary>
        public static T Create<T>() => DispatchProxy.Create<T, ThrowingProxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InnerFailureException(targetMethod?.Name ?? "<unknown>");
    }
}
