using System;
using System.Linq;
using System.Reflection;

namespace Optimizely.Performance.Counters.VersionDetection
{
    /// <summary>
    /// Detects the Optimizely CMS version at runtime using multiple strategies.
    /// </summary>
    public static class OptimizelyVersionDetector
    {
        // Which Optimizely major this build is for, decided by the target framework.
        //
        // Not by the CMS11/CMS12/CMS13 symbols: those are defined by the CMS and Commerce projects
        // and never reach Core, so reading them here silently yielded Unknown on every build and
        // made version validation throw during container configuration on every supported site.
        // The target framework is the mapping's actual source of truth and Core has it.
        //
        // The #error is deliberate. Adding a target framework without extending this map is the
        // mistake that caused that failure, and it should break the build rather than the site.
#if NET472
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V11;
        private const string TargetFrameworkName = ".NET Framework 4.7.2";
#elif NET10_0
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V13;
        private const string TargetFrameworkName = ".NET 10";
#elif NET9_0
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V12;
        private const string TargetFrameworkName = ".NET 9";
#elif NET8_0
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V12;
        private const string TargetFrameworkName = ".NET 8";
#elif NET7_0
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V12;
        private const string TargetFrameworkName = ".NET 7";
#elif NET6_0
        private const OptimizelyVersion CompiledFor = OptimizelyVersion.V12;
        private const string TargetFrameworkName = ".NET 6";
#else
#error No Optimizely version is mapped to this target framework. Add it to OptimizelyVersionDetector, or drop it from the TargetFrameworks in every project.
#endif

        private static OptimizelyVersion? _cachedVersion;
        private static readonly object _lock = new object();

        /// <summary>
        /// Detects the Optimizely CMS version using multiple strategies.
        /// The result is cached after the first detection.
        /// </summary>
        /// <returns>The detected Optimizely version.</returns>
        public static OptimizelyVersion DetectVersion()
        {
            if (_cachedVersion.HasValue)
                return _cachedVersion.Value;

            lock (_lock)
            {
                if (_cachedVersion.HasValue)
                    return _cachedVersion.Value;

                // Strategy 1: CMS Core assembly version (most reliable)
                var version = DetectByCmsCoreVersion();
                if (version != OptimizelyVersion.Unknown)
                {
                    _cachedVersion = version;
                    return version;
                }

                // Strategy 2: Framework assembly version
                version = DetectByFrameworkVersion();
                if (version != OptimizelyVersion.Unknown)
                {
                    _cachedVersion = version;
                    return version;
                }

                // Strategy 3: Commerce assembly version
                version = DetectByCommerceVersion();
                if (version != OptimizelyVersion.Unknown)
                {
                    _cachedVersion = version;
                    return version;
                }

                // Strategy 4: ASP.NET Core presence = V12 or V13 (but we couldn't determine which)
                if (HasAspNetCoreSupport())
                {
                    // Default to V12 as the safer assumption
                    _cachedVersion = OptimizelyVersion.V12;
                    return OptimizelyVersion.V12;
                }

#if NET472
                // Strategy 5: .NET Framework = V11. Always conclusive, so there is no Unknown
                // fallback on this target framework.
                _cachedVersion = OptimizelyVersion.V11;
                return OptimizelyVersion.V11;
#else
                // Deliberately not cached. Every strategy above reads AppDomain.CurrentDomain
                // .GetAssemblies(), which lists what has loaded rather than what is installed, and
                // .NET Core loads assemblies lazily on first use. Asking before anything has touched
                // an EPiServer type is therefore an ordinary miss, not a conclusion - and caching it
                // would make that first early call permanent, so the initialization module would
                // later read Unknown and fail version validation on a site that is perfectly fine.
                return OptimizelyVersion.Unknown;
#endif
            }
        }

        /// <summary>
        /// Gets the expected Optimizely version for this compiled build.
        /// </summary>
        /// <returns>The expected version for this build's target framework.</returns>
        public static OptimizelyVersion GetExpectedVersion() => CompiledFor;

        /// <summary>
        /// Validates that the detected version matches the expected version for this build.
        /// </summary>
        /// <returns>True if versions match, false otherwise.</returns>
        public static bool ValidateVersion()
        {
            var detected = DetectVersion();
            var expected = GetExpectedVersion();
            return detected == expected;
        }

        /// <summary>
        /// Checks if running on V11.
        /// </summary>
        public static bool IsV11() => DetectVersion() == OptimizelyVersion.V11;

        /// <summary>
        /// Checks if running on V12.
        /// </summary>
        public static bool IsV12() => DetectVersion() == OptimizelyVersion.V12;

        /// <summary>
        /// Checks if running on V13.
        /// </summary>
        public static bool IsV13() => DetectVersion() == OptimizelyVersion.V13;

        /// <summary>
        /// Checks if running on V12 or later.
        /// </summary>
        public static bool IsV12OrLater() => DetectVersion() >= OptimizelyVersion.V12;

        /// <summary>
        /// Checks if running on V13 or later.
        /// </summary>
        public static bool IsV13OrLater() => DetectVersion() >= OptimizelyVersion.V13;

        /// <summary>
        /// Gets detailed version information for logging and diagnostics.
        /// </summary>
        /// <returns>A formatted string with detailed version information.</returns>
        public static string GetDetailedVersionInfo()
        {
            var cmsCore = FindAssembly("EPiServer.CMS.Core");
            var framework = FindAssembly("EPiServer.Framework");
            var commerce = FindAssembly("EPiServer.Commerce.Core");
            var hasAspNetCore = HasAspNetCoreSupport();

            return $"Detected Version: {DetectVersion()}\n" +
                   $"Expected Version: {GetExpectedVersion()}\n" +
                   $"CMS.Core: {cmsCore?.GetName().Version?.ToString() ?? "Not found"}\n" +
                   $"Framework: {framework?.GetName().Version?.ToString() ?? "Not found"}\n" +
                   $"Commerce.Core: {commerce?.GetName().Version?.ToString() ?? "Not found"}\n" +
                   $"Has ASP.NET Core: {hasAspNetCore}\n" +
                   $"Target Framework: {TargetFrameworkName}";
        }

        private static OptimizelyVersion DetectByCmsCoreVersion()
        {
            var assembly = FindAssembly("EPiServer.CMS.Core");
            return assembly != null ? MapVersionNumber(assembly.GetName().Version) : OptimizelyVersion.Unknown;
        }

        private static OptimizelyVersion DetectByFrameworkVersion()
        {
            var assembly = FindAssembly("EPiServer.Framework");
            return assembly != null ? MapVersionNumber(assembly.GetName().Version) : OptimizelyVersion.Unknown;
        }

        private static OptimizelyVersion DetectByCommerceVersion()
        {
            var assembly = FindAssembly("EPiServer.Commerce.Core");
            if (assembly == null)
                return OptimizelyVersion.Unknown;

            // A loaded assembly can legitimately carry no version; treat that as undetectable
            // rather than throwing during container configuration.
            var version = assembly.GetName().Version;
            if (version == null)
                return OptimizelyVersion.Unknown;

            return version.Major switch
            {
                13 => OptimizelyVersion.V11,
                14 => OptimizelyVersion.V12,
                15 => OptimizelyVersion.V13,
                _ => OptimizelyVersion.Unknown
            };
        }

        private static Assembly? FindAssembly(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
        }

        private static OptimizelyVersion MapVersionNumber(Version? version)
        {
            if (version == null)
                return OptimizelyVersion.Unknown;

            return version.Major switch
            {
                11 => OptimizelyVersion.V11,
                12 => OptimizelyVersion.V12,
                13 => OptimizelyVersion.V13,
                _ => OptimizelyVersion.Unknown
            };
        }

        private static bool HasAspNetCoreSupport() => FindAssembly("EPiServer.Cms.AspNetCore") != null;
    }
}
