using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Optimizely.Performance.Counters.CMS.Diagnostics
{
    /// <summary>
    /// Locates the reader/writer lock that Optimizely's memory cache serialises on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock is a private static field with no accessor, so there is no supported way to reach
    /// it. Reflection is used deliberately and is confined entirely to this type: the field holds a
    /// <see cref="ReaderWriterLockSlim"/>, which is a framework type, so once the field has been
    /// read once the caller holds an ordinary strongly typed reference and every subsequent sample
    /// is a plain property read. The reflection cost and the reflection risk are both paid exactly
    /// once, at startup.
    /// </para>
    /// <para>
    /// Everything here is written on the assumption that it will eventually stop working.
    /// Optimizely has already moved this type once - on 12 it is a public class in
    /// <c>EPiServer.Framework</c>, on 13 an internal one in <c>EPiServer.Cache</c> - and renamed the
    /// field from <c>cacheLock</c> to <c>CacheLock</c> in the process. So the search is by shape
    /// rather than by name wherever it can be, no failure is fatal, and every failure reports a
    /// reason precise enough to fix.
    /// </para>
    /// <para>
    /// Compiled for every target framework rather than the .NET Core ones only. Whether CMS 11
    /// happens to hold its cache lock in the same shape is not something this type needs to know in
    /// advance: it either finds a field of the right type or reports precisely why it did not.
    /// </para>
    /// </remarks>
    public static class CacheLockLocator
    {
        /// <summary>
        /// Full name of the cache type holding the lock. Unchanged between Optimizely 12 and 13
        /// even though the assembly containing it changed.
        /// </summary>
        public const string CacheTypeName = "EPiServer.Framework.Cache.Internal.MemoryObjectInstanceCache";

        // Tried in order before falling back to scanning. EPiServer.Cache first because that is
        // where the type lives on 13, and a site on 13 should not pay for a failed probe of an
        // assembly it does not load.
        private static readonly string[] CandidateAssemblies =
        {
            "EPiServer.Cache",
            "EPiServer.Framework"
        };

        // Only consulted when matching by shape is ambiguous.
        private static readonly string[] CandidateFieldNames = { "CacheLock", "cacheLock" };

        /// <summary>
        /// Finds the Optimizely memory cache type in the assemblies loaded into this process.
        /// </summary>
        /// <returns>The type, or <c>null</c> if it is not present.</returns>
        /// <remarks>
        /// The scan is a fallback for the case where Optimizely moves the type again. It only looks
        /// at assemblies whose simple name begins with <c>EPiServer</c>, and only runs when both
        /// direct lookups miss.
        /// </remarks>
        public static Type? FindCacheType()
        {
            foreach (var assembly in CandidateAssemblies)
            {
                try
                {
                    var type = Type.GetType($"{CacheTypeName}, {assembly}", throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // A failed load of one candidate says nothing about the others.
                }
            }

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic
                        || assembly.GetName().Name?.StartsWith("EPiServer", StringComparison.Ordinal) != true)
                    {
                        continue;
                    }

                    try
                    {
                        var type = assembly.GetType(CacheTypeName, throwOnError: false);
                        if (type != null)
                        {
                            return type;
                        }
                    }
                    catch
                    {
                        // Types in this assembly could not be inspected. Keep looking.
                    }
                }
            }
            catch
            {
                // Enumerating the app domain failed. There is nothing further to try.
            }

            return null;
        }

        /// <summary>
        /// Reads the static cache lock out of <paramref name="cacheType"/>.
        /// </summary>
        /// <param name="cacheType">The cache type, or <c>null</c> if it was not found.</param>
        /// <param name="cacheLock">The lock, when this returns <c>true</c>.</param>
        /// <param name="diagnostic">
        /// A description of what happened, suitable for logging. Set on success as well as failure,
        /// so a site can confirm what was actually found rather than inferring it.
        /// </param>
        /// <returns><c>true</c> if the lock was found.</returns>
        /// <remarks>
        /// Matching is by field type first and field name only as a tiebreak. The type of the field
        /// is the thing that carries meaning; its name is an implementation detail that has already
        /// changed once. A rename therefore costs nothing, and only the addition of a second static
        /// <see cref="ReaderWriterLockSlim"/> field would make the search ambiguous - at which point
        /// the known names are used to resolve it, and if they do not, the probe declines to guess.
        /// </remarks>
        public static bool TryGetLock(
            Type? cacheType,
            out ReaderWriterLockSlim? cacheLock,
            out string diagnostic)
        {
            cacheLock = null;

            if (cacheType == null)
            {
                diagnostic =
                    $"The Optimizely cache type '{CacheTypeName}' was not found in any loaded assembly.";
                return false;
            }

            FieldInfo[] candidates;

            try
            {
                candidates = cacheType
                    .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(f => typeof(ReaderWriterLockSlim).IsAssignableFrom(f.FieldType))
                    .ToArray();
            }
            catch (Exception ex)
            {
                diagnostic = $"Could not enumerate the fields of '{cacheType.FullName}': {ex.Message}";
                return false;
            }

            if (candidates.Length == 0)
            {
                diagnostic =
                    $"'{cacheType.FullName}' has no static ReaderWriterLockSlim field. The cache no " +
                    "longer serialises on a lock of that shape.";
                return false;
            }

            var field = candidates.Length == 1
                ? candidates[0]
                : candidates.FirstOrDefault(f => CandidateFieldNames.Contains(f.Name, StringComparer.Ordinal));

            if (field == null)
            {
                diagnostic =
                    $"'{cacheType.FullName}' has {candidates.Length} static ReaderWriterLockSlim fields " +
                    $"({string.Join(", ", candidates.Select(f => f.Name))}) and none is named " +
                    $"{string.Join(" or ", CandidateFieldNames)}. Declining to guess which one guards " +
                    "the cache.";
                return false;
            }

            object? value;

            try
            {
                value = field.GetValue(null);
            }
            catch (Exception ex)
            {
                // A type initializer that throws surfaces here, as does a field the host's trust
                // level will not let us read.
                diagnostic =
                    $"Reading '{cacheType.FullName}.{field.Name}' failed: " +
                    $"{ex.GetBaseException().Message}";
                return false;
            }

            if (value is not ReaderWriterLockSlim resolved)
            {
                diagnostic =
                    value == null
                        ? $"'{cacheType.FullName}.{field.Name}' is null. The cache has not been " +
                          "initialised, or the lock is created on demand."
                        : $"'{cacheType.FullName}.{field.Name}' holds a {value.GetType().Name}, not a " +
                          "ReaderWriterLockSlim.";
                return false;
            }

            cacheLock = resolved;
            diagnostic = $"Reading cache lock contention from '{cacheType.FullName}.{field.Name}'.";
            return true;
        }
    }
}
