using System;
using System.Globalization;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// One file in the deployment directory, as far as it could be identified.
    /// </summary>
    /// <remarks>
    /// Immutable and comparable by content: two records are the same deployed file if
    /// <see cref="Identity"/> matches, which is what both the fingerprint and the diff are built
    /// on.
    /// </remarks>
    public sealed class AssemblyRecord
    {
        /// <summary>
        /// Creates a record.
        /// </summary>
        /// <param name="name">File name without its extension, which for a managed assembly is its simple name.</param>
        /// <param name="assemblyVersion">The <c>AssemblyVersion</c>, or null if it could not be read.</param>
        /// <param name="fileVersion">The Win32 file version, or null.</param>
        /// <param name="informationalVersion">The informational version, which carries the commit on a SourceLink build, or null.</param>
        /// <param name="mvid">The module version id, or <see cref="Guid.Empty"/> when unavailable.</param>
        /// <param name="length">File length in bytes.</param>
        /// <param name="isManaged">Whether the file carries a CLI header.</param>
        public AssemblyRecord(
            string name,
            string? assemblyVersion,
            string? fileVersion,
            string? informationalVersion,
            Guid mvid,
            long length,
            bool isManaged)
        {
            Name = name;
            AssemblyVersion = assemblyVersion;
            FileVersion = fileVersion;
            InformationalVersion = informationalVersion;
            Mvid = mvid;
            Length = length;
            IsManaged = isManaged;
        }

        /// <summary>Gets the file name without its extension.</summary>
        public string Name { get; }

        /// <summary>Gets the <c>AssemblyVersion</c>, or null.</summary>
        public string? AssemblyVersion { get; }

        /// <summary>Gets the Win32 file version, or null.</summary>
        public string? FileVersion { get; }

        /// <summary>Gets the informational version, or null.</summary>
        public string? InformationalVersion { get; }

        /// <summary>Gets the module version id, or <see cref="Guid.Empty"/>.</summary>
        public Guid Mvid { get; }

        /// <summary>Gets the file length in bytes.</summary>
        public long Length { get; }

        /// <summary>Gets whether the file carries a CLI header.</summary>
        public bool IsManaged { get; }

        /// <summary>
        /// Gets the key this file is matched on across deployments, which is its name folded to
        /// lower case.
        /// </summary>
        /// <remarks>
        /// Folded because a file renamed only in case is not a deployment change worth reporting,
        /// and because the same deployment scanned on Windows and on Linux should fingerprint
        /// identically - the DXP topology runs Linux containers while the same code is developed
        /// and smoke-tested on Windows.
        /// </remarks>
        public string Key => Name.ToLowerInvariant();

        /// <summary>
        /// Gets the content identity of this file: what has to be equal for two deployments to be
        /// carrying the same binary.
        /// </summary>
        /// <remarks>
        /// The MVID leads, because it is the only field that distinguishes a rebuild from a reship.
        /// Where there is none - a native DLL, or metadata this could not walk - length and file
        /// version stand in. That is weaker: a native library rebuilt to the same size with the
        /// same version reads as unchanged. It is also the best available without hashing every
        /// byte of every file on every start, which is a cost this is not worth.
        /// </remarks>
        public string Identity =>
            Mvid != Guid.Empty
                ? Mvid.ToString("N")
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "n:{0}:{1}",
                    FileVersion ?? "?",
                    Length);

        /// <summary>
        /// Gets the version to show a human, which is the most specific one that was readable.
        /// </summary>
        public string DisplayVersion =>
            AssemblyVersion ?? FileVersion ?? InformationalVersion ?? "unknown";
    }
}
