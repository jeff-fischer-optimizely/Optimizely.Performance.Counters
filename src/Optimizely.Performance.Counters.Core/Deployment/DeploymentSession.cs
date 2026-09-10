using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// One capture of what is deployed, and everything emitted because of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="DeploymentTracker"/>, which owns the timer and the process-wide
    /// single instance, on the same split as <c>SamplingProbe</c> and <c>RuntimeProbes</c>: the
    /// thing that decides what to emit is drivable directly, so a test can assert on the events
    /// without racing a timer, and a host that wants a capture at a moment of its own choosing can
    /// ask for one.
    /// </para>
    /// <para>
    /// Three tiers, in decreasing order of how much has to work for them to be useful. The manifest
    /// is one row carrying the fingerprint and needs nothing but a sink. The inventory is one row
    /// per assembly and needs only the scan. The change rows need a previous manifest, which needs
    /// durable state, which is the part that is allowed to be missing - and on the container
    /// topology this was built against, usually is. That ordering is deliberate: the question the
    /// operator actually asks - "what changed" - is answerable from the manifest tier alone, by
    /// querying two fingerprints and diffing their inventories, with no local state involved.
    /// </para>
    /// </remarks>
    public sealed class DeploymentSession : IDisposable
    {
        private readonly DeploymentOptions _options;
        private readonly DeploymentEnvironment _environment;
        private readonly DeploymentStateStore _store;
        private readonly IDeploymentEventSink _sink;
        private readonly ILogger? _logger;
        private readonly IServiceProvider? _serviceProvider;
        private readonly DeploymentFingerprintProxy? _stamp;
        private readonly bool _emitInventory;
        private readonly bool _emitInventoryEveryCapture;

        private int _capturing;
        private int _sequence;
        private DeploymentManifest? _current;
        private DateTimeOffset _lastInventoryUtc = DateTimeOffset.MinValue;

        /// <summary>
        /// Prepares a session: finds somewhere to keep state, finds a sink, and attaches the
        /// telemetry stamp. Emits nothing until <see cref="Capture"/> is called.
        /// </summary>
        /// <param name="options">What to report. Null takes the defaults.</param>
        /// <param name="optimizelyVersion">The Optimizely major, as text.</param>
        /// <param name="serviceProvider">The built container on V12 and V13; null on V11.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <param name="sink">
        /// Overrides where events go. Null in production, where Application Insights is found by
        /// reflection and the log is the fallback.
        /// </param>
        public DeploymentSession(
            DeploymentOptions? options,
            string optimizelyVersion,
            IServiceProvider? serviceProvider = null,
            ILogger? logger = null,
            IDeploymentEventSink? sink = null)
        {
            _options = options ?? new DeploymentOptions();
            _logger = logger;
            _serviceProvider = serviceProvider;
            _environment = DeploymentEnvironment.Detect(optimizelyVersion);
            _store = DeploymentStateStore.Open(_options, _environment, logger);

            _sink = sink
                ?? (IDeploymentEventSink?)ApplicationInsightsEventSink.TryCreate(serviceProvider, logger)
                ?? new LoggerDeploymentEventSink(logger);

            var mode = _options.InventoryMode ?? string.Empty;
            _emitInventory = !mode.Equals("Never", StringComparison.OrdinalIgnoreCase);
            _emitInventoryEveryCapture = mode.Equals("Always", StringComparison.OrdinalIgnoreCase);

            if (_options.StampTelemetry)
            {
                _stamp = DeploymentFingerprintInitializer.TryAttach(serviceProvider, logger);
            }
        }

        /// <summary>Gets where this session keeps its state, for the tests and for the log.</summary>
        public DeploymentStateStore StateStore => _store;

        /// <summary>Gets the manifest of the last successful capture, or null before the first.</summary>
        public DeploymentManifest? Current => _current;

        /// <summary>
        /// Scans, compares against what was last seen, and emits.
        /// </summary>
        /// <returns>True if a capture ran to completion.</returns>
        /// <remarks>
        /// Every call rescans rather than reusing the first result. A bin folder rewritten under a
        /// running site is the one change a durable state store can catch that a process restart
        /// cannot, and it is invisible without a rescan. The cost is a few hundred file reads.
        /// </remarks>
        public bool Capture()
        {
            // A scan that overruns the heartbeat - a cold file cache on a slow disk - must not
            // start a second one on top of it.
            if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            {
                return false;
            }

            try
            {
                var sequence = Interlocked.Increment(ref _sequence);
                var scan = AssemblyInventoryScanner.Scan(_options);
                var manifest = scan.Manifest;

                // Before anything is emitted, so the manifest event this capture produces is itself
                // stamped with the fingerprint it is reporting.
                if (_stamp != null)
                {
                    _stamp.Fingerprint = manifest.Fingerprint;
                }

                // The store only on the first capture; after that this process is the authority on
                // what it last saw, and rereading would just risk a peer's write racing it.
                var previous = sequence == 1 ? _store.Read() : _current;
                var diff = manifest.DiffAgainst(previous);

                EmitManifest(scan, diff, sequence);

                if (diff.HasChanged)
                {
                    EmitChanges(diff);
                }

                if (ShouldEmitInventory(diff, sequence))
                {
                    EmitInventory(manifest, scan);
                    _lastInventoryUtc = manifest.CapturedUtc;
                }

                EmitDivergence(manifest);

                _current = manifest;
                _store.Write(manifest, _logger);

                return true;
            }
            catch (Exception ex)
            {
                // The caller's timer keeps running. A capture that failed once - a directory being
                // rewritten under it - usually succeeds on the next heartbeat, and one that fails
                // every time is a Debug line every fifteen minutes rather than a dead feature that
                // says nothing.
                _logger?.LogDebug(ex, "A deployment capture failed.");
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // The removal is best effort, because the telemetry configuration may already be torn
            // down at shutdown; clearing the fingerprint first is the guarantee, because an
            // initializer with nothing to stamp does nothing whether it is still registered or not.
            if (_stamp != null)
            {
                _stamp.Fingerprint = null;
                DeploymentFingerprintInitializer.Detach(_stamp, _serviceProvider);
            }
        }

        /// <remarks>
        /// Once per process start, on every change, and on the slow re-emit. Not gated on the state
        /// store: on a host with no durable state every start is a baseline, and gating there would
        /// mean the environments this was built for never report an inventory at all - which is the
        /// question it exists to answer.
        /// </remarks>
        private bool ShouldEmitInventory(DeploymentDiff diff, int sequence)
        {
            if (!_emitInventory)
            {
                return false;
            }

            return sequence == 1
                || _emitInventoryEveryCapture
                || diff.HasChanged
                || DateTimeOffset.UtcNow - _lastInventoryUtc >= ReemitInterval(_options.InventoryReemitHours);
        }

        private void EmitManifest(InventoryScan scan, DeploymentDiff diff, int sequence)
        {
            var properties = NewProperties();
            var manifest = scan.Manifest;

            _environment.Describe(properties);

            properties["Fingerprint"] = manifest.Fingerprint;
            properties["AssemblyCount"] = Text(manifest.Count);
            properties["Reason"] = sequence == 1 ? "Startup" : "Heartbeat";
            properties["Sequence"] = Text(sequence);
            properties["Transition"] = diff.IsBaseline
                ? "Baseline"
                : diff.HasChanged ? "Changed" : "Unchanged";

            // Which store won, on every row. Without it, "this environment emits no change events"
            // is a mystery rather than a documented consequence of an ephemeral disk.
            properties["StateStore"] = _store.ToString();
            properties["InventorySource"] = scan.Source.ToString();

            if (manifest.Truncated)
            {
                properties["Truncated"] = "true";
            }

            if (_options.IncludeFilePaths)
            {
                properties["ScanDirectory"] = scan.Directory;
            }

            if (diff.Previous != null)
            {
                properties["PreviousFingerprint"] = diff.Previous.Fingerprint;
                properties["Added"] = Text(diff.Added);
                properties["Changed"] = Text(diff.Changed);
                properties["Removed"] = Text(diff.Removed);
            }

            _sink.Track(DeploymentEventNames.Manifest, properties);
        }

        /// <remarks>
        /// These rows carry the fingerprint and nothing about the instance. The fingerprint is
        /// content-addressed, so the same set of files produces the same value everywhere - which
        /// means one instance reporting the list once is enough for any instance's manifest row to
        /// be joined to it. Repeating the role and instance on four hundred rows would multiply the
        /// volume by the width of the environment block to say something the manifest already said.
        /// </remarks>
        private void EmitInventory(DeploymentManifest manifest, InventoryScan scan)
        {
            foreach (var record in manifest.Assemblies)
            {
                var properties = NewProperties();

                properties["Fingerprint"] = manifest.Fingerprint;
                properties["Assembly"] = record.Name;
                properties["Version"] = record.DisplayVersion;
                properties["Managed"] = record.IsManaged ? "true" : "false";

                Add(properties, "AssemblyVersion", record.AssemblyVersion);
                Add(properties, "FileVersion", record.FileVersion);
                Add(properties, "InformationalVersion", record.InformationalVersion);

                if (record.Mvid != Guid.Empty)
                {
                    properties["Mvid"] = record.Mvid.ToString("N");
                }

                if (record.Length > 0)
                {
                    properties["SizeBytes"] = Text(record.Length);
                }

                if (_options.IncludeFilePaths)
                {
                    properties["Path"] = System.IO.Path.Combine(scan.Directory, record.Name + ".dll");
                }

                _sink.Track(DeploymentEventNames.Inventory, properties);
            }

            _logger?.LogInformation(
                "Reported {Count} deployed assemblies for fingerprint {Fingerprint}.",
                manifest.Count,
                manifest.Fingerprint);
        }

        private void EmitChanges(DeploymentDiff diff)
        {
            var emitted = 0;

            foreach (var change in diff.Changes)
            {
                if (emitted++ >= _options.MaxChangeEvents)
                {
                    _logger?.LogInformation(
                        "{Total} assemblies changed between {Previous} and {Current}; the first " +
                        "{Emitted} were reported individually and the counts on the manifest event " +
                        "cover the rest.",
                        diff.Changes.Count,
                        diff.Previous?.Fingerprint,
                        diff.Current.Fingerprint,
                        _options.MaxChangeEvents);

                    break;
                }

                var properties = NewProperties();

                _environment.Describe(properties);

                properties["Fingerprint"] = diff.Current.Fingerprint;
                properties["Change"] = change.Kind.ToString();
                properties["Assembly"] = change.Name;

                if (diff.Previous != null)
                {
                    properties["PreviousFingerprint"] = diff.Previous.Fingerprint;
                }

                Add(properties, "Version", change.Current?.DisplayVersion);
                Add(properties, "PreviousVersion", change.Previous?.DisplayVersion);
                Add(properties, "Mvid", Mvid(change.Current));
                Add(properties, "PreviousMvid", Mvid(change.Previous));

                // The case a version comparison misses entirely: same version string, different
                // bits. Called out as its own dimension so an alert can find it without reasoning
                // about two other columns.
                if (change.Kind == AssemblyChangeKind.Changed
                    && string.Equals(
                        change.Current?.DisplayVersion,
                        change.Previous?.DisplayVersion,
                        StringComparison.Ordinal))
                {
                    properties["SameVersionDifferentBinary"] = "true";
                }

                _sink.Track(DeploymentEventNames.Changed, properties);
            }

            _logger?.LogInformation(
                "The deployment changed from {Previous} to {Current}: {Added} added, {Changed} " +
                "changed, {Removed} removed.",
                diff.Previous?.Fingerprint,
                diff.Current.Fingerprint,
                diff.Added,
                diff.Changed,
                diff.Removed);
        }

        /// <remarks>
        /// Only fires where the state directory is genuinely shared, which is a minority of hosts.
        /// Everywhere else divergence is found by querying the manifest tier - one row per instance
        /// per heartbeat, grouped by fingerprint - and this is the early, local version of the same
        /// finding.
        /// </remarks>
        private void EmitDivergence(DeploymentManifest manifest)
        {
            var peers = _store.ReadPeers();

            if (peers.Count == 0)
            {
                return;
            }

            var divergent = peers
                .Where(peer => !string.Equals(peer.Value, manifest.Fingerprint, StringComparison.Ordinal))
                .ToList();

            if (divergent.Count == 0)
            {
                return;
            }

            var properties = NewProperties();

            _environment.Describe(properties);

            properties["Fingerprint"] = manifest.Fingerprint;
            properties["PeerCount"] = Text(peers.Count);
            properties["DivergentPeerCount"] = Text(divergent.Count);
            properties["PeerFingerprints"] = string.Join(
                ",",
                divergent
                    .Select(peer => peer.Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal));

            _sink.Track(DeploymentEventNames.Divergence, properties);

            _logger?.LogWarning(
                "This instance is running deployment {Fingerprint} while {Count} of {Total} peers " +
                "are running something else. A rollout in progress looks like this and resolves; " +
                "one that does not resolve is a partial swap.",
                manifest.Fingerprint,
                divergent.Count,
                peers.Count);
        }

        private static string? Mvid(AssemblyRecord? record) =>
            record == null || record.Mvid == Guid.Empty ? null : record.Mvid.ToString("N");

        private static void Add(IDictionary<string, string> properties, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                properties[key] = value!;
            }
        }

        private static Dictionary<string, string> NewProperties() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static TimeSpan ReemitInterval(int hours) =>
            TimeSpan.FromMinutes(DeploymentTracker.ClampMinutes((long)hours * 60));
    }
}
