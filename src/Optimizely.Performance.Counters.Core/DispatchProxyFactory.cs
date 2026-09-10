using System;
using System.Linq;
using System.Reflection;

namespace Optimizely.Performance.Counters.Core
{
    /// <summary>
    /// Builds a <see cref="DispatchProxy"/> for an interface known only at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This package implements two interfaces it cannot reference at compile time - log4net's
    /// <c>IAppender</c> and Application Insights' <c>ITelemetryInitializer</c> - because both come
    /// from optional dependencies whose assembly identity tracks their package version. A
    /// <see cref="DispatchProxy"/> is how you implement an interface you only have a
    /// <see cref="Type"/> for, and reaching its generic <c>Create&lt;T, TProxy&gt;</c> when the
    /// interface is a <see cref="Type"/> means reflecting over reflection.
    /// </para>
    /// <para>
    /// Hence this, rather than <c>GetMethod("Create", ...)</c> at each site, which is what both
    /// sites did and which throws <see cref="AmbiguousMatchException"/>: the runtime has since grown
    /// a second, non-generic <c>Create(Type, Type)</c> overload, and <c>GetMethod</c> by name and
    /// binding flags cannot choose between two methods that differ only in generic arity. The
    /// non-generic overload is the obvious way to write all of this and is deliberately not used -
    /// it does not exist on .NET Framework, where the log4net path is the one that matters most.
    /// </para>
    /// </remarks>
    internal static class DispatchProxyFactory
    {
        /// <summary>
        /// Creates a proxy implementing <paramref name="interfaceType"/>, derived from
        /// <paramref name="proxyType"/>.
        /// </summary>
        /// <param name="interfaceType">The interface to implement, resolved at runtime.</param>
        /// <param name="proxyType">
        /// The <see cref="DispatchProxy"/> subclass whose <c>Invoke</c> handles the calls. Must be
        /// public: the generated proxy derives from it into a dynamic assembly.
        /// </param>
        /// <returns>The proxy, castable to both types.</returns>
        internal static object Create(Type interfaceType, Type proxyType)
        {
            var create = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method =>
                    method.Name == nameof(DispatchProxy.Create) &&
                    method.IsGenericMethodDefinition &&
                    method.GetGenericArguments().Length == 2 &&
                    method.GetParameters().Length == 0)
                .MakeGenericMethod(interfaceType, proxyType);

            return create.Invoke(null, null)!;
        }
    }
}
