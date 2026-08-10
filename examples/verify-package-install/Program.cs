using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.VersionDetection;

namespace Optimizely.Performance.Counters.Examples.VerifyPackageInstall
{
    /// <summary>
    /// Checks that everything a host depends on at startup is actually reachable from the installed
    /// packages. Each check is one thing that has broken before, or that would break silently.
    /// </summary>
    /// <remarks>
    /// Exits non-zero on the first failure so this can be a build step. Nothing here needs a licence,
    /// a database or a running site.
    /// </remarks>
    internal static class Program
    {
        private static int Main()
        {
            var failures = new List<string>();

            Report("Target framework", DescribeTargetFramework());
            Report("EventSource name", CounterNames.EventSourceName);

            CheckTheVersionMapIsPopulated(failures);
            CheckTheCounterRegistryIsPopulated(failures);
            CheckTheEventSourceStartsCleanly(failures);

            // Before the runtime detection below, and not for tidiness. .NET Core loads assemblies
            // on first use, so nothing EPiServer is in the AppDomain until something asks for a type
            // out of it - which is what loading the modules does. A real site is past this point
            // long before the initialization module runs.
            CheckTheDecoratorsAreReachable(failures);
            CheckTheOptimizelyAssembliesBind(failures);

            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("PASS - the packages install and bind on this target framework.");
                return 0;
            }

            Console.WriteLine($"FAIL - {failures.Count} check(s) failed:");
            foreach (var failure in failures)
            {
                Console.WriteLine($"  - {failure}");
            }

            return 1;
        }

        /// <summary>
        /// The detector derives the Optimizely major from the target framework, and returns
        /// <see cref="OptimizelyVersion.Unknown"/> if the map has a hole in it. That is not a
        /// crash - it surfaces later as a validation failure inside container configuration, which
        /// takes the whole site down at startup.
        /// </summary>
        private static void CheckTheVersionMapIsPopulated(ICollection<string> failures)
        {
            var expected = OptimizelyVersionDetector.GetExpectedVersion();
            Report("Compiled for", expected.ToString());

            if (expected == OptimizelyVersion.Unknown)
            {
                failures.Add("OptimizelyVersionDetector does not map this target framework to a major version.");
            }
        }

        /// <summary>
        /// Reads the Optimizely version off the loaded assemblies, which only works if the EPiServer
        /// assemblies the packages depend on resolved at runtime rather than merely at compile time.
        /// </summary>
        private static void CheckTheOptimizelyAssembliesBind(ICollection<string> failures)
        {
            var detected = OptimizelyVersionDetector.DetectVersion();
            Report("Detected at runtime", detected.ToString());

            if (detected == OptimizelyVersion.Unknown)
            {
                failures.Add(
                    "No EPiServer assembly could be loaded, so the packages' Optimizely dependencies " +
                    "did not flow to this project.");
                return;
            }

            var expected = OptimizelyVersionDetector.GetExpectedVersion();
            if (detected != expected && expected != OptimizelyVersion.Unknown)
            {
                failures.Add(
                    $"This target framework resolved Optimizely {detected} but the packages are built " +
                    $"for {expected}. A site in this shape would fail version validation at startup.");
            }
        }

        /// <summary>
        /// The registry is the list Application Insights gets subscribed to. An empty one means a
        /// host wires up successfully and then collects nothing.
        /// </summary>
        private static void CheckTheCounterRegistryIsPopulated(ICollection<string> failures)
        {
            var registered = EventCounterRegistry.GetAllCounterNames().ToList();
            Report("Registered counters", registered.Count.ToString());

            if (registered.Count == 0)
            {
                failures.Add("EventCounterRegistry is empty - there would be nothing to collect.");
            }
        }

        /// <summary>
        /// An <see cref="EventSource"/> reports its own configuration failures through
        /// <see cref="EventSource.ConstructionException"/> instead of throwing, so a faulted one
        /// looks healthy to every caller and quietly publishes nothing. A duplicate event ID has
        /// already caused exactly that here once.
        /// </summary>
        private static void CheckTheEventSourceStartsCleanly(ICollection<string> failures)
        {
            var source = OptimizelyPerformanceEventSource.Instance;
            var constructionException = source.ConstructionException;

            Report("EventSource state", constructionException == null ? "healthy" : "FAULTED");

            if (constructionException != null)
            {
                failures.Add($"The EventSource failed to construct and will publish nothing: {constructionException.Message}");
            }
        }

        /// <summary>
        /// Loading the initialization modules forces the CMS and Commerce assemblies, and every
        /// EPiServer type in their signatures, to resolve. This is the check that catches a
        /// lib/ folder targeting the wrong framework or a missing transitive dependency.
        /// </summary>
        private static void CheckTheDecoratorsAreReachable(ICollection<string> failures)
        {
            LoadModule(
                "Optimizely.Performance.Counters.CMS.Initialization.CMSPerformanceCountersModule, " +
                "Optimizely.Performance.Counters.CMS",
                "CMS module",
                failures);

            LoadModule(
                "Optimizely.Performance.Counters.Commerce.Initialization.CommercePerformanceCountersModule, " +
                "Optimizely.Performance.Counters.Commerce",
                "Commerce module",
                failures);
        }

        private static void LoadModule(string assemblyQualifiedName, string label, ICollection<string> failures)
        {
            try
            {
                var moduleType = Type.GetType(assemblyQualifiedName, throwOnError: true);
                Report(label, moduleType.Assembly.GetName().Name + " loaded");
            }
            catch (Exception ex)
            {
                Report(label, "FAILED");
                failures.Add($"{label} could not be loaded: {ex.Message}");
            }
        }

        private static string DescribeTargetFramework()
        {
#if NET472
            return "net472";
#elif NET6_0
            return "net6.0";
#elif NET7_0
            return "net7.0";
#elif NET8_0
            return "net8.0";
#elif NET9_0
            return "net9.0";
#elif NET10_0
            return "net10.0";
#else
            return "unrecognised";
#endif
        }

        private static void Report(string label, string value) =>
            Console.WriteLine($"{label,-22}: {value}");
    }
}
