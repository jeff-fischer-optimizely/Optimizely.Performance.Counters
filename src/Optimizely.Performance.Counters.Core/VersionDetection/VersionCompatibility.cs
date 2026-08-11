using System;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.VersionDetection
{
    /// <summary>
    /// Checks that the package a site installed matches the Optimizely version it is running on.
    /// <para>
    /// A mismatch here is fatal rather than degraded: the decorators are compiled against one
    /// interface shape, and loading them against another surfaces as a MissingMethodException
    /// somewhere deep in a request. Failing at startup with an explanation is far cheaper to
    /// diagnose.
    /// </para>
    /// </summary>
    public static class VersionCompatibility
    {
        private const string SupportedConfigurations =
            "Supported configurations:\n" +
            "  - V11 (CMS 11 / Commerce 13) requires .NET Framework 4.7.2\n" +
            "  - V12 (CMS 12 / Commerce 14) requires .NET 6 through .NET 9\n" +
            "  - V13 (CMS 13 / Commerce 15) requires .NET 10\n" +
            "See README.md for details.";

        /// <summary>
        /// Throws if the detected version is unknown or is not the one this package was built for.
        /// </summary>
        /// <param name="detected">Version found in the running application.</param>
        /// <param name="expected">Version this package was compiled against.</param>
        /// <param name="packageName">Package reporting the problem, for example "CMS".</param>
        /// <param name="requiredAssembly">
        /// The Optimizely package whose absence explains an undetectable version, for example
        /// "EPiServer.CMS.Core".
        /// </param>
        /// <param name="logger">Log sink; may be null during container configuration.</param>
        /// <exception cref="InvalidOperationException">The versions do not match.</exception>
        public static void Validate(
            OptimizelyVersion detected,
            OptimizelyVersion expected,
            string packageName,
            string requiredAssembly,
            ILogger? logger)
        {
            if (detected == OptimizelyVersion.Unknown)
            {
                throw Fail($"CRITICAL: Unable to detect the Optimizely version. Ensure {requiredAssembly} is installed.", logger);
            }

            if (detected != expected)
            {
                throw Fail(
                    $"CRITICAL: {packageName} package compiled for Optimizely {expected} but detected {detected}.\n" +
                    SupportedConfigurations,
                    logger);
            }

            logger?.LogInformation("Version validation successful: {Version}", detected);
        }

        private static InvalidOperationException Fail(string message, ILogger? logger)
        {
            // The detail dump goes in the exception message, not only through the logger. The
            // callers are initialization modules, and what they log during ConfigureContainer is
            // buffered until Initialize - which never runs if this throws. The exception is
            // therefore the only thing that reaches the operator, and "detected V12, expected V13"
            // without the assembly versions behind it is the half of the answer that does not help.
            var detail = message + "\n\n" + OptimizelyVersionDetector.GetDetailedVersionInfo();

            logger?.LogError("{Message}", detail);
            return new InvalidOperationException(detail);
        }
    }
}
