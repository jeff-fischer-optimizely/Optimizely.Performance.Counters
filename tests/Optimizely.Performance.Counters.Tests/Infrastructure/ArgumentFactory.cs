using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// Produces stand-in arguments so an arbitrary interface member can be invoked reflectively.
    /// <para>
    /// The values carry no meaning beyond being distinguishable - the forwarding tests assert that
    /// whatever went into the decorator came out the other side unchanged, so what matters is that
    /// each argument is non-default where possible.
    /// </para>
    /// </summary>
    public static class ArgumentFactory
    {
        /// <summary>
        /// Builds an argument list for <paramref name="method"/>.
        /// </summary>
        public static object?[] For(MethodInfo method) =>
            method.GetParameters().Select((p, index) => Create(p.ParameterType, index)).ToArray();

        private static object? Create(Type type, int index)
        {
            if (type.IsByRef)
            {
                // out/ref parameters arrive as T&. Reflection wants a placeholder of T, which it
                // overwrites on the way out - IContentLoader.TryGet is the case that matters here.
                type = type.GetElementType()!;
            }

            if (type == typeof(string))
            {
                return $"arg{index}";
            }

            if (type == typeof(CultureInfo))
            {
                return CultureInfo.InvariantCulture;
            }

            if (type == typeof(CancellationToken))
            {
                return CancellationToken.None;
            }

            if (type == typeof(Guid))
            {
                // Deterministic rather than random, so a failure message reads the same on a rerun.
                return new Guid($"00000000-0000-0000-0000-{index:D12}");
            }

            if (type == typeof(int))
            {
                return index + 1;
            }

            if (type == typeof(bool))
            {
                return true;
            }

            if (type == typeof(Type))
            {
                return typeof(string);
            }

            if (type.IsEnum)
            {
                // The first declared value, which for the flags enums here is the neutral one.
                return Enum.GetValues(type).Cast<object>().First();
            }

            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }

            // Reference types: a real instance if one can be had for free, otherwise null. Null is
            // an acceptable argument here because the decorators forward without dereferencing.
            try
            {
                return type.GetConstructor(Type.EmptyTypes) != null
                    ? Activator.CreateInstance(type)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
