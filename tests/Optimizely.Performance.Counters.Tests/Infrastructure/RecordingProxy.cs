using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// A single call captured by <see cref="RecordingProxy"/>.
    /// </summary>
    public sealed class Invocation
    {
        public Invocation(MethodInfo method, object?[] arguments)
        {
            Method = method;
            Arguments = arguments;
        }

        public MethodInfo Method { get; }

        public object?[] Arguments { get; }

        public override string ToString() => $"{Method.Name}({string.Join(", ", Arguments)})";
    }

    /// <summary>
    /// Stands in for the Optimizely implementation a decorator wraps. Records every call and
    /// returns a harmless default, so a test can assert that a decorator forwarded the exact
    /// member it was given with the exact arguments it was given.
    /// <para>
    /// Built on <see cref="DispatchProxy"/> rather than a mocking library so it works unchanged
    /// on net472 and needs no per-interface setup - the decorators expose more than sixty members
    /// across four interfaces, and any one of them could be mis-forwarded.
    /// </para>
    /// </summary>
    public class RecordingProxy : DispatchProxy
    {
        private readonly List<Invocation> _invocations = new List<Invocation>();

        /// <summary>Calls received, in order.</summary>
        public IReadOnlyList<Invocation> Invocations => _invocations;

        /// <summary>
        /// Creates a proxy implementing <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Interface to implement.</typeparam>
        /// <returns>The proxy, and the same object typed as the recorder.</returns>
        public static (T Instance, RecordingProxy Recorder) Create<T>()
        {
            var instance = DispatchProxy.Create<T, RecordingProxy>();
            return (instance, (RecordingProxy)(object)instance!);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod == null)
            {
                throw new InvalidOperationException("DispatchProxy invoked without a target method.");
            }

            _invocations.Add(new Invocation(targetMethod, args ?? Array.Empty<object?>()));
            return DefaultFor(targetMethod.ReturnType);
        }

        /// <summary>
        /// A return value the caller can safely ignore. Tasks must be completed rather than null,
        /// because the async decorator members await what they are handed.
        /// </summary>
        private static object? DefaultFor(Type returnType)
        {
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
                return fromResult.Invoke(null, new[] { DefaultFor(resultType) });
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                // An empty array rather than null, because the CMS and Commerce implementations
                // return materialized collections. The decorators only count items they can count
                // for free, so returning something lazy here would silently skip the item-count
                // counters and make them look untested.
                return Array.CreateInstance(returnType.GetGenericArguments()[0], 0);
            }

            return DefaultOf(returnType);
        }

        private static object? DefaultOf(Type type) =>
            type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
