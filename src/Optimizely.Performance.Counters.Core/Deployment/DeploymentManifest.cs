using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// The set of files found in a deployment directory, and the fingerprint that identifies it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fingerprint is content-addressed: the same files produce the same value on every
    /// instance, in every environment, at any time. That is what lets the inventory rows be
    /// recorded once and joined to from anywhere - a query asking which DLLs an instance is running
    /// needs the list to have been reported by somebody, once, rather than by that instance
    /// recently.
    /// </para>
    /// <para>
    /// It also means a rollback is visibly a rollback. A fingerprint that was live, was superseded
    /// and then reappears is the same value again, not a new one, so the reappearance is
    /// detectable rather than looking like a third deployment.
    /// </para>
    /// </remarks>
    public sealed class DeploymentManifest
    {
        private const string FormatHeader = "opticounters-deployment/1";

        /// <summary>
        /// How much of the SHA-256 is kept. Sixteen hex characters is 64 bits: collision-free for
        /// any plausible number of distinct deployments, and short enough to stamp onto every
        /// telemetry item without the width mattering.
        /// </summary>
        private const int FingerprintLength = 16;

        private DeploymentManifest(
            IReadOnlyList<AssemblyRecord> assemblies,
            string fingerprint,
            DateTimeOffset capturedUtc,
            bool truncated)
        {
            Assemblies = assemblies;
            Fingerprint = fingerprint;
            CapturedUtc = capturedUtc;
            Truncated = truncated;
        }

        /// <summary>Gets the files, ordered by <see cref="AssemblyRecord.Key"/>.</summary>
        public IReadOnlyList<AssemblyRecord> Assemblies { get; }

        /// <summary>Gets the fingerprint of this set, as 16 lower-case hex characters.</summary>
        public string Fingerprint { get; }

        /// <summary>Gets when the scan behind this manifest ran.</summary>
        public DateTimeOffset CapturedUtc { get; }

        /// <summary>Gets whether the scan stopped at the configured cap.</summary>
        public bool Truncated { get; }

        /// <summary>Gets the number of files recorded.</summary>
        public int Count => Assemblies.Count;

        /// <summary>
        /// Builds a manifest from a set of records, ordering them and computing the fingerprint.
        /// </summary>
        /// <param name="assemblies">The records, in any order.</param>
        /// <param name="capturedUtc">When the scan ran.</param>
        /// <param name="truncated">Whether the scan hit its cap.</param>
        /// <returns>The manifest.</returns>
        public static DeploymentManifest Create(
            IEnumerable<AssemblyRecord> assemblies, DateTimeOffset capturedUtc, bool truncated = false)
        {
            var ordered = (assemblies ?? Enumerable.Empty<AssemblyRecord>())
                .OrderBy(record => record.Key, StringComparer.Ordinal)
                .ToList();

            return new DeploymentManifest(ordered, ComputeFingerprint(ordered), capturedUtc, truncated);
        }

        /// <remarks>
        /// Ordinal sort over the folded key, then name and identity only. Deliberately not the file
        /// version or the informational version: including them would make the fingerprint change
        /// when a rebuild bumps a version string without changing what runs, and the MVID already
        /// covers the case where the bits differ. Deliberately not the file length either, except
        /// where it is standing in for a missing MVID, for the same reason.
        /// </remarks>
        private static string ComputeFingerprint(IReadOnlyList<AssemblyRecord> ordered)
        {
            var builder = new StringBuilder();

            foreach (var record in ordered)
            {
                builder.Append(record.Key).Append('|').Append(record.Identity).Append('\n');
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(FingerprintLength);

                for (var i = 0; i < FingerprintLength / 2; i++)
                {
                    text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }

                return text.ToString();
            }
        }

        /// <summary>
        /// Compares this manifest against the one before it.
        /// </summary>
        /// <param name="previous">The previous manifest, or null when none was known.</param>
        /// <returns>The differences, or a baseline result when <paramref name="previous"/> is null.</returns>
        /// <remarks>
        /// A null previous is a baseline, and is reported as one. It is emphatically not an empty
        /// previous: treating "we have never seen this instance before" as "it used to have no
        /// assemblies" would report every file in the deployment as newly added, on every cold
        /// start of every container - which on the DXP container topology is most starts.
        /// </remarks>
        public DeploymentDiff DiffAgainst(DeploymentManifest? previous)
        {
            if (previous == null)
            {
                return DeploymentDiff.Baseline(this);
            }

            var before = previous.Assemblies.ToDictionary(record => record.Key, StringComparer.Ordinal);
            var after = Assemblies.ToDictionary(record => record.Key, StringComparer.Ordinal);

            var changes = new List<AssemblyChange>();

            foreach (var record in Assemblies)
            {
                if (!before.TryGetValue(record.Key, out var was))
                {
                    changes.Add(new AssemblyChange(AssemblyChangeKind.Added, record, null));
                }
                else if (!string.Equals(was.Identity, record.Identity, StringComparison.Ordinal))
                {
                    changes.Add(new AssemblyChange(AssemblyChangeKind.Changed, record, was));
                }
            }

            foreach (var record in previous.Assemblies)
            {
                if (!after.ContainsKey(record.Key))
                {
                    changes.Add(new AssemblyChange(AssemblyChangeKind.Removed, null, record));
                }
            }

            return new DeploymentDiff(this, previous, changes);
        }

        /// <summary>
        /// Renders the manifest for the state file.
        /// </summary>
        /// <returns>The serialized form, which <see cref="TryParse"/> reads back.</returns>
        /// <remarks>
        /// A line format rather than JSON. net472 has no <c>System.Text.Json</c>, Core references no
        /// serializer, and adding one for a file this shape would be a package dependency on every
        /// consumer for four fields per line.
        /// </remarks>
        public string Serialize()
        {
            var builder = new StringBuilder();

            builder.Append(FormatHeader).Append('\n');
            builder.Append("fingerprint=").Append(Fingerprint).Append('\n');
            builder.Append("captured=").Append(CapturedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("count=").Append(Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var record in Assemblies)
            {
                builder
                    .Append(Clean(record.Name)).Append('|')
                    .Append(Clean(record.AssemblyVersion)).Append('|')
                    .Append(Clean(record.FileVersion)).Append('|')
                    .Append(Clean(record.InformationalVersion)).Append('|')
                    .Append(record.Mvid == Guid.Empty ? string.Empty : record.Mvid.ToString("N")).Append('|')
                    .Append(record.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(record.IsManaged ? '1' : '0')
                    .Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads back what <see cref="Serialize"/> wrote.
        /// </summary>
        /// <param name="text">The file contents, or null.</param>
        /// <param name="manifest">The parsed manifest, when this returns true.</param>
        /// <returns>True if the text was a manifest this version understands.</returns>
        /// <remarks>
        /// Anything unrecognised returns false, and every caller treats false as "no previous
        /// manifest" - a baseline. A partially written or corrupt file must not be read as a
        /// smaller deployment, which would report the missing half as removed.
        /// </remarks>
        public static bool TryParse(string? text, out DeploymentManifest? manifest)
        {
            manifest = null;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var lines = text!.Split('\n');

            if (lines.Length < 4 || lines[0].Trim() != FormatHeader)
            {
                return false;
            }

            var declaredFingerprint = Value(lines[1], "fingerprint");
            var declaredCount = Value(lines[3], "count");

            if (declaredFingerprint == null
                || declaredCount == null
                || !int.TryParse(declaredCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return false;
            }

            var captured = DateTimeOffset.MinValue;

            if (Value(lines[2], "captured") is string capturedText)
            {
                DateTimeOffset.TryParse(
                    capturedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out captured);
            }

            var records = new List<AssemblyRecord>();

            for (var i = 4; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                if (line.Length == 0)
                {
                    continue;
                }

                var parts = line.Split('|');

                if (parts.Length != 7)
                {
                    return false;
                }

                Guid.TryParseExact(parts[4], "N", out var mvid);
                long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length);

                records.Add(new AssemblyRecord(
                    parts[0],
                    Nullable(parts[1]),
                    Nullable(parts[2]),
                    Nullable(parts[3]),
                    mvid,
                    length,
                    parts[6] == "1"));
            }

            // The count and the fingerprint are both checked against what was actually read. A file
            // truncated mid-write can still parse - every line it does contain is well formed - and
            // this is what catches it. Recomputing rather than trusting the stored value also means
            // a change to how the fingerprint is derived invalidates old state instead of silently
            // comparing values computed two different ways.
            if (records.Count != count)
            {
                return false;
            }

            var parsed = Create(records, captured);

            if (!string.Equals(parsed.Fingerprint, declaredFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            manifest = parsed;
            return true;
        }

        private static string? Value(string line, string key)
        {
            var prefix = key + "=";

            return line.StartsWith(prefix, StringComparison.Ordinal)
                ? line.Substring(prefix.Length).Trim()
                : null;
        }

        private static string? Nullable(string value) => value.Length == 0 ? null : value;

        /// <remarks>
        /// The separator and the line break are the only characters that could break the format.
        /// Neither occurs in a file name or a version string in practice; replacing rather than
        /// escaping keeps the parser trivial, and the substitution is visible if it ever happens.
        /// </remarks>
        private static string Clean(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value!
                .Replace('|', '_')
                .Replace('\n', '_')
                .Replace('\r', '_');
        }
    }

    /// <summary>What happened to one file between two deployments.</summary>
    public enum AssemblyChangeKind
    {
        /// <summary>Present now, absent before.</summary>
        Added,

        /// <summary>Present in both, different binary.</summary>
        Changed,

        /// <summary>Absent now, present before.</summary>
        Removed
    }

    /// <summary>One file's difference between two manifests.</summary>
    public sealed class AssemblyChange
    {
        /// <summary>Creates a change.</summary>
        /// <param name="kind">What happened.</param>
        /// <param name="current">The record now, or null when removed.</param>
        /// <param name="previous">The record before, or null when added.</param>
        public AssemblyChange(AssemblyChangeKind kind, AssemblyRecord? current, AssemblyRecord? previous)
        {
            Kind = kind;
            Current = current;
            Previous = previous;
        }

        /// <summary>Gets what happened.</summary>
        public AssemblyChangeKind Kind { get; }

        /// <summary>Gets the record now, or null when removed.</summary>
        public AssemblyRecord? Current { get; }

        /// <summary>Gets the record before, or null when added.</summary>
        public AssemblyRecord? Previous { get; }

        /// <summary>Gets the file name, whichever side it came from.</summary>
        public string Name => Current?.Name ?? Previous?.Name ?? "unknown";
    }

    /// <summary>The difference between two manifests, or a baseline when there was only one.</summary>
    public sealed class DeploymentDiff
    {
        internal DeploymentDiff(
            DeploymentManifest current,
            DeploymentManifest? previous,
            IReadOnlyList<AssemblyChange> changes)
        {
            Current = current;
            Previous = previous;
            Changes = changes;
        }

        /// <summary>Gets the manifest just captured.</summary>
        public DeploymentManifest Current { get; }

        /// <summary>Gets the manifest before it, or null for a baseline.</summary>
        public DeploymentManifest? Previous { get; }

        /// <summary>Gets the per-file differences, empty for a baseline.</summary>
        public IReadOnlyList<AssemblyChange> Changes { get; }

        /// <summary>Gets whether there was no previous manifest to compare against.</summary>
        public bool IsBaseline => Previous == null;

        /// <summary>
        /// Gets whether the fingerprint moved. False for a baseline, and false for a restart that
        /// found the deployment exactly as it left it.
        /// </summary>
        public bool HasChanged =>
            Previous != null
            && !string.Equals(Current.Fingerprint, Previous.Fingerprint, StringComparison.Ordinal);

        /// <summary>Gets the number of files added.</summary>
        public int Added => Changes.Count(change => change.Kind == AssemblyChangeKind.Added);

        /// <summary>Gets the number of files whose binary changed.</summary>
        public int Changed => Changes.Count(change => change.Kind == AssemblyChangeKind.Changed);

        /// <summary>Gets the number of files removed.</summary>
        public int Removed => Changes.Count(change => change.Kind == AssemblyChangeKind.Removed);

        internal static DeploymentDiff Baseline(DeploymentManifest current) =>
            new DeploymentDiff(current, null, new List<AssemblyChange>());
    }
}
