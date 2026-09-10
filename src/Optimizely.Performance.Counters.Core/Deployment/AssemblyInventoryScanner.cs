using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Reads the deployed assembly set off disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The deployment directory rather than <c>AppDomain.CurrentDomain.GetAssemblies()</c>, and the
    /// difference is not a detail. .NET loads assemblies lazily, so the loaded set at startup is
    /// both partial and dependent on what the process happened to do first - two instances of the
    /// same deployment would fingerprint differently, and the same instance would fingerprint
    /// differently across two restarts, with nothing having been deployed. The whole feature would
    /// be noise. <c>OptimizelyVersionDetector</c> documents the same trap from the other side.
    /// </para>
    /// <para>
    /// The directory is also the honest answer to the question being asked. "Which DLLs are running
    /// in this environment" means the ones that were shipped, not the subset that has been touched
    /// since the process started.
    /// </para>
    /// </remarks>
    public static class AssemblyInventoryScanner
    {
        // Below this many managed files, the directory is assumed not to be a deployment directory
        // at all - single-file publish being the case that produces it - and the loaded set is used
        // instead. Any real Optimizely site has hundreds.
        private const int ImplausiblySmall = 5;

        private static readonly string[] SystemPrefixes =
        {
            "System.",
            "Microsoft.",
            "netstandard",
            "mscorlib",
            "WindowsBase",
            "Accessibility",
        };

        /// <summary>
        /// Scans the deployment directory.
        /// </summary>
        /// <param name="options">What to include and how much of it.</param>
        /// <returns>The manifest, and where it came from.</returns>
        public static InventoryScan Scan(DeploymentOptions options)
        {
            var directory = DeploymentDirectory();
            var truncated = false;
            var records = new List<AssemblyRecord>();
            var source = InventorySource.DeploymentDirectory;

            try
            {
                foreach (var path in Files(directory))
                {
                    if (records.Count >= options.MaxAssemblies)
                    {
                        truncated = true;
                        break;
                    }

                    var record = Describe(path);

                    if (record != null && Include(record, options))
                    {
                        records.Add(record);
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable directory is not a reason to fail. What was gathered before the
                // failure still fingerprints, and the fallback below covers the case where that is
                // nothing useful.
            }

            if (records.Count(record => record.IsManaged) < ImplausiblySmall)
            {
                var loaded = FromLoadedAssemblies(options).ToList();

                if (loaded.Count > records.Count)
                {
                    records = loaded;
                    source = InventorySource.LoadedAssemblies;
                    truncated = false;
                }
            }

            return new InventoryScan(
                DeploymentManifest.Create(records, DateTimeOffset.UtcNow, truncated),
                source,
                directory);
        }

        /// <summary>
        /// Gets the directory the scan reads.
        /// </summary>
        /// <returns>The full path, which may not exist.</returns>
        public static string DeploymentDirectory()
        {
            var baseDirectory = AppContext.BaseDirectory ?? Directory.GetCurrentDirectory();

#if NET472
            // ASP.NET puts the site root in BaseDirectory and the assemblies in bin beneath it.
            // Scanning the root would find almost nothing, and following the loaded assemblies'
            // Location instead would land in Temporary ASP.NET Files, because the runtime shadow
            // copies - so neither of the obvious answers is the deployment.
            var bin = Path.Combine(baseDirectory, "bin");

            if (Directory.Exists(bin))
            {
                return bin;
            }
#endif

            return baseDirectory;
        }

        /// <remarks>
        /// Top level only. A publish output has <c>runtimes/</c> beneath it holding the same native
        /// libraries once per architecture, of which at most one is ever loaded; recursing would
        /// report several copies of every one of them as deployed and make the count meaningless.
        /// </remarks>
        private static IEnumerable<string> Files(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return Enumerable.Empty<string>();
            }

            return Directory
                .GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static AssemblyRecord? Describe(string path)
        {
            var identity = PortableExecutableReader.TryRead(path);

            if (identity == null)
            {
                // Not a PE image. A .dll that is not a library at all - a resource blob, or a file
                // mid-copy - and nothing to report about it.
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            long length;

            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                length = 0;
            }

            return new AssemblyRecord(
                name,
                identity.Value.IsManaged ? ManagedVersion(path) : null,
                FileVersion(path),
                InformationalVersion(path),
                identity.Value.Mvid,
                length,
                identity.Value.IsManaged);
        }

        /// <remarks>
        /// <c>AssemblyName.GetAssemblyName</c> reads the metadata without loading the assembly into
        /// the process - which matters, because loading three hundred assemblies to describe them
        /// would double the site's working set and run every module initializer they contain.
        /// </remarks>
        private static string? ManagedVersion(string path)
        {
            try
            {
                return AssemblyName.GetAssemblyName(path).Version?.ToString();
            }
            catch (Exception)
            {
                // A managed module that is not an assembly - a netmodule, or a mixed-mode library
                // whose manifest is elsewhere. It still has an MVID, which is the field that
                // matters.
                return null;
            }
        }

        private static string? FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(info.FileVersion) ? null : info.FileVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <remarks>
        /// The Win32 product version, which is where the SDK writes
        /// <c>AssemblyInformationalVersion</c> - so on a SourceLink build this carries the commit
        /// hash, and a deployment can be traced to a source revision without anything else being
        /// wired up.
        /// </remarks>
        private static string? InformationalVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrEmpty(info.ProductVersion) ? null : info.ProductVersion;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <remarks>
        /// The fallback, for a single-file or trimmed publish where there is no directory of DLLs to
        /// read. Its weaknesses are the ones described on this class - partial, and dependent on
        /// what has loaded - so it is used only when the directory scan found nothing plausible,
        /// and the manifest records that it was used.
        /// </remarks>
        private static IEnumerable<AssemblyRecord> FromLoadedAssemblies(DeploymentOptions options)
        {
            Assembly[] assemblies;

            try
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception)
            {
                yield break;
            }

            foreach (var assembly in assemblies)
            {
                AssemblyRecord? record;

                try
                {
                    if (assembly.IsDynamic)
                    {
                        continue;
                    }

                    var name = assembly.GetName();

                    record = new AssemblyRecord(
                        name.Name ?? "unknown",
                        name.Version?.ToString(),
                        assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version,
                        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                        assembly.ManifestModule.ModuleVersionId,
                        length: 0,
                        isManaged: true);
                }
                catch (Exception)
                {
                    continue;
                }

                if (Include(record, options))
                {
                    yield return record;
                }
            }
        }

        private static bool Include(AssemblyRecord record, DeploymentOptions options) =>
            options.IncludeSystemAssemblies || !IsSystem(record.Name);

        private static bool IsSystem(string name) =>
            SystemPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where a manifest's contents came from.</summary>
    public enum InventorySource
    {
        /// <summary>Read from the files in the deployment directory. The normal case.</summary>
        DeploymentDirectory,

        /// <summary>
        /// Read from the assemblies loaded in the process, because the directory held no plausible
        /// deployment. Partial by nature.
        /// </summary>
        LoadedAssemblies
    }

    /// <summary>The result of one scan.</summary>
    public sealed class InventoryScan
    {
        internal InventoryScan(DeploymentManifest manifest, InventorySource source, string directory)
        {
            Manifest = manifest;
            Source = source;
            Directory = directory;
        }

        /// <summary>Gets the manifest.</summary>
        public DeploymentManifest Manifest { get; }

        /// <summary>Gets where the contents came from.</summary>
        public InventorySource Source { get; }

        /// <summary>Gets the directory that was scanned.</summary>
        public string Directory { get; }
    }
}
