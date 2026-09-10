using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Where the last manifest this instance saw is kept, if anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "semi" in semi-stateful, and it is worth being precise about how little it is
    /// trusted. Change events need a previous manifest; nothing else does. So the store is allowed
    /// to fail, be absent, be wiped between deployments or be unwritable, and the consequence in
    /// every case is that a transition is reported as a baseline instead of as a diff. The
    /// manifest and inventory tiers do not consult it at all.
    /// </para>
    /// <para>
    /// That is not a hypothetical. The DXP topology this was built against runs Linux custom
    /// containers with <c>WEBSITES_ENABLE_APP_SERVICE_STORAGE</c> unset, which means <c>/home</c> is
    /// container-local, and a deployment is a container replacement - so the previous manifest is
    /// gone at exactly the moment it would have been useful. On that topology the diff comes from
    /// querying the manifest tier, and this store only catches changes within one container's life,
    /// such as a bin folder rewritten in place. Hosts with real disks - V11 under IIS, a VM, a
    /// Windows App Service without a custom container - get the change events as well.
    /// </para>
    /// <para>
    /// Which store won is reported on every event. Without that, "this environment emits no change
    /// events" has no visible cause.
    /// </para>
    /// </remarks>
    public sealed class DeploymentStateStore
    {
        private const string FileNamePrefix = "manifest-";
        private const string FileNameSuffix = ".txt";

        // Long enough that a slow rollout does not sweep out an instance that is merely quiet,
        // short enough that a container farm does not accumulate state files for ever.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

        private readonly string? _path;

        private DeploymentStateStore(StateStoreKind kind, StateDurability durability, string? path)
        {
            Kind = kind;
            Durability = durability;
            _path = path;
        }

        /// <summary>Gets which candidate directory was used.</summary>
        public StateStoreKind Kind { get; }

        /// <summary>Gets whether what is written here is expected to outlive a deployment.</summary>
        public StateDurability Durability { get; }

        /// <summary>Gets the file this instance reads and writes, or null when there is none.</summary>
        public string? Path => _path;

        /// <summary>
        /// Finds somewhere to keep the state, in preference order, by writing to each candidate.
        /// </summary>
        /// <param name="options">Supplies the configured override, if any.</param>
        /// <param name="environment">Supplies the instance identity and hosting shape.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>A store, which may be one that keeps nothing.</returns>
        /// <remarks>
        /// Each candidate is proven by performing the write - create the directory, write a file,
        /// read it back, delete it - rather than by inspecting attributes or permissions. A
        /// read-only mount, an ACL, a full quota and App Service's read-only run-from-package each
        /// fail at a different layer, and only doing the thing catches all of them.
        /// </remarks>
        public static DeploymentStateStore Open(
            DeploymentOptions options, DeploymentEnvironment environment, ILogger? logger)
        {
            foreach (var candidate in Candidates(options, environment))
            {
                if (!Usable(candidate.Directory))
                {
                    if (candidate.Kind == StateStoreKind.Configured)
                    {
                        // Named explicitly by an operator, so silence would be wrong - but falling
                        // through is still better than failing, on the same reasoning as every
                        // other setting this package reads.
                        logger?.LogWarning(
                            "'{Section}:Deployment:StatePath' is set to '{Path}', which could not be " +
                            "written to. Falling back to the next candidate location.",
                            Configuration.InstrumentationOptions.SectionName,
                            candidate.Directory);
                    }

                    continue;
                }

                var file = System.IO.Path.Combine(
                    candidate.Directory,
                    FileNamePrefix + Sanitize(environment.InstanceId) + FileNameSuffix);

                var store = new DeploymentStateStore(candidate.Kind, candidate.Durability, file);

                store.Prune(candidate.Directory, logger);

                logger?.LogInformation(
                    "Deployment state is kept in {Path} ({Kind}, {Durability}). {Consequence}",
                    file,
                    candidate.Kind,
                    candidate.Durability,
                    candidate.Durability == StateDurability.Durable
                        ? "Assembly change events will be emitted when a deployment alters the bin folder."
                        : "This location does not survive a deployment, so transitions will be " +
                          "reported as baselines and the change must be derived by querying the " +
                          "manifest events.");

                return store;
            }

            logger?.LogInformation(
                "No writable location was found for deployment state, so every process start reports " +
                "a baseline. The deployment manifest and inventory events are unaffected; only the " +
                "per-assembly change events depend on this.");

            return new DeploymentStateStore(StateStoreKind.None, StateDurability.None, path: null);
        }

        /// <summary>
        /// Reads the manifest this instance last recorded.
        /// </summary>
        /// <returns>The manifest, or null when there is none or it could not be trusted.</returns>
        public DeploymentManifest? Read()
        {
            if (_path == null)
            {
                return null;
            }

            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                return DeploymentManifest.TryParse(File.ReadAllText(_path, Encoding.UTF8), out var manifest)
                    ? manifest
                    : null;
            }
            catch (Exception)
            {
                // Unreadable is indistinguishable from absent as far as the caller is concerned,
                // and both mean "baseline". What must never happen is returning an empty manifest.
                return null;
            }
        }

        /// <summary>
        /// Records the manifest as the one this instance has now seen.
        /// </summary>
        /// <param name="manifest">The manifest to record.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>True if it was written.</returns>
        /// <remarks>
        /// Written to a temporary file and moved into place. A process killed mid-write would
        /// otherwise leave a truncated file, and while <see cref="DeploymentManifest.TryParse"/>
        /// rejects one of those, the cheaper fix is not to create it.
        /// </remarks>
        public bool Write(DeploymentManifest manifest, ILogger? logger)
        {
            if (_path == null)
            {
                return false;
            }

            var temporary = _path + ".tmp";

            try
            {
                File.WriteAllText(temporary, manifest.Serialize(), Encoding.UTF8);

                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                File.Move(temporary, _path);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not record the deployment manifest at {Path}.", _path);

                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception)
                {
                    // Nothing further to do about a leftover temporary file.
                }

                return false;
            }
        }

        /// <summary>
        /// Reads the manifests other instances have recorded in the same directory.
        /// </summary>
        /// <returns>Fingerprint by instance id, excluding this instance and anything stale.</returns>
        /// <remarks>
        /// Only useful where the directory is genuinely shared - a Windows App Service with the
        /// storage share mounted, or a mounted volume - and empty everywhere else, which is why
        /// nothing depends on it. Where it does work it turns fleet divergence into something an
        /// instance can notice by itself rather than only in a query.
        /// </remarks>
        public IReadOnlyDictionary<string, string> ReadPeers()
        {
            var peers = new Dictionary<string, string>(StringComparer.Ordinal);

            if (_path == null)
            {
                return peers;
            }

            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);

                if (directory == null)
                {
                    return peers;
                }

                foreach (var file in Directory.GetFiles(directory, FileNamePrefix + "*" + FileNameSuffix))
                {
                    if (string.Equals(file, _path, StringComparison.OrdinalIgnoreCase)
                        || File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - StaleAfter)
                    {
                        continue;
                    }

                    if (DeploymentManifest.TryParse(File.ReadAllText(file, Encoding.UTF8), out var manifest)
                        && manifest != null)
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(file);
                        peers[name.Substring(FileNamePrefix.Length)] = manifest.Fingerprint;
                    }
                }
            }
            catch (Exception)
            {
                // A shared directory being rewritten under us. Whatever was read is still valid.
            }

            return peers;
        }

        /// <remarks>
        /// A container farm produces a new instance identity on every replacement, so without this
        /// the shared directory - where there is one - grows a file per container for ever.
        /// </remarks>
        private void Prune(string directory, ILogger? logger)
        {
            try
            {
                var cutoff = DateTime.UtcNow - StaleAfter;

                foreach (var file in Directory.GetFiles(directory, FileNamePrefix + "*" + FileNameSuffix))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not prune stale deployment state files in {Directory}.", directory);
            }
        }

        private static IEnumerable<Candidate> Candidates(
            DeploymentOptions options, DeploymentEnvironment environment)
        {
            if (!string.IsNullOrWhiteSpace(options.StatePath))
            {
                yield return new Candidate(
                    StateStoreKind.Configured, options.StatePath!.Trim(), StateDurability.Durable);
            }

            var home = Environment.GetEnvironmentVariable("HOME");

            if (environment.IsAzureAppService && !string.IsNullOrWhiteSpace(home))
            {
                // Persisted on a Windows App Service and on Linux when the storage share is
                // mounted. On a Linux custom container with the share off - the DXP default - the
                // path exists and is writable, and is thrown away with the container, so it is
                // offered as ephemeral rather than skipped.
                yield return new Candidate(
                    StateStoreKind.AppServiceHome,
                    System.IO.Path.Combine(home!, "data", "OptiCounters"),
                    environment.ContainerImage == null || environment.AppServiceStorageEnabled
                        ? StateDurability.Durable
                        : StateDurability.Ephemeral);
            }

#if NET472
            // A V11 site is an IIS application with a real, writable App_Data that a bin deployment
            // does not touch. It is the natural home on that version and nowhere else.
            var appData = Safe(() => System.Web.Hosting.HostingEnvironment.MapPath("~/App_Data"));

            if (appData != null)
            {
                yield return new Candidate(
                    StateStoreKind.AppData,
                    System.IO.Path.Combine(appData, "OptiCounters"),
                    StateDurability.Durable);
            }
#endif

            var programData = Safe(() =>
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

            if (!string.IsNullOrWhiteSpace(programData))
            {
                yield return new Candidate(
                    StateStoreKind.ProgramData,
                    System.IO.Path.Combine(programData!, "Optimizely", "OptiCounters"),
                    StateDurability.Durable);
            }

            var temp = Safe(System.IO.Path.GetTempPath);

            if (!string.IsNullOrWhiteSpace(temp))
            {
                // Last, and honestly labelled. Temp is per-instance and survives a process restart
                // but not an instance replacement, so it catches a bin folder rewritten under a
                // running site and nothing else.
                yield return new Candidate(
                    StateStoreKind.Temp,
                    System.IO.Path.Combine(temp!, "OptiCounters"),
                    StateDurability.Ephemeral);
            }
        }

        private static bool Usable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);

                var probe = System.IO.Path.Combine(
                    directory,
                    "probe-" + Guid.NewGuid().ToString("N") + ".tmp");

                File.WriteAllText(probe, "opticounters", Encoding.UTF8);
                var written = File.ReadAllText(probe, Encoding.UTF8);
                File.Delete(probe);

                return written == "opticounters";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Sanitize(string value)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);

            foreach (var c in value)
            {
                builder.Append(invalid.Contains(c) ? '_' : c);
            }

            var text = builder.ToString();

            // Long enough to stay unique, short enough for any file system. WEBSITE_INSTANCE_ID is
            // 64 hex characters and only the first few are ever needed to tell two apart, but
            // truncating hard would risk a collision on a large farm, so this keeps most of it.
            return text.Length > 48 ? text.Substring(0, 48) : text;
        }

        private static string? Safe(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private readonly struct Candidate
        {
            internal Candidate(StateStoreKind kind, string directory, StateDurability durability)
            {
                Kind = kind;
                Directory = directory;
                Durability = durability;
            }

            internal StateStoreKind Kind { get; }

            internal string Directory { get; }

            internal StateDurability Durability { get; }
        }

        /// <summary>
        /// Renders the store for an event property.
        /// </summary>
        /// <returns>The kind and durability as one token.</returns>
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0}/{1}", Kind, Durability);
    }

    /// <summary>Which candidate directory the state was written to.</summary>
    public enum StateStoreKind
    {
        /// <summary>Nowhere. Every start reports a baseline.</summary>
        None,

        /// <summary>The directory named by configuration.</summary>
        Configured,

        /// <summary>App Service's <c>/home</c> share.</summary>
        AppServiceHome,

        /// <summary>A V11 site's <c>App_Data</c>.</summary>
        AppData,

        /// <summary>The machine's common application data directory.</summary>
        ProgramData,

        /// <summary>The temporary directory.</summary>
        Temp
    }

    /// <summary>Whether the state is expected to outlive a deployment.</summary>
    public enum StateDurability
    {
        /// <summary>Nothing is kept.</summary>
        None,

        /// <summary>Kept, but discarded when the instance is replaced - which a deployment may do.</summary>
        Ephemeral,

        /// <summary>Expected to survive a deployment.</summary>
        Durable
    }
}
