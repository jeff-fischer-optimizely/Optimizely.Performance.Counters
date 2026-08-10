namespace Optimizely.Performance.Counters.VersionDetection
{
    /// <summary>
    /// Represents the detected Optimizely CMS version.
    /// </summary>
    public enum OptimizelyVersion
    {
        /// <summary>
        /// Unable to detect Optimizely version.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Optimizely CMS V11 (.NET Framework 4.7.2)
        /// </summary>
        V11 = 11,

        /// <summary>
        /// Optimizely CMS V12 (.NET 6)
        /// </summary>
        V12 = 12,

        /// <summary>
        /// Optimizely CMS V13 (.NET 8+)
        /// </summary>
        V13 = 13
    }
}
