using System;
using System.IO;
using System.Linq;
using System.Text;
using Optimizely.Performance.Counters.Core.Deployment;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Covers what a capture emits.
    /// </summary>
    /// <remarks>
    /// The event names and dimension names asserted here are the query surface. Every saved query,
    /// workbook and alert built on this feature spells them out, so a rename is a breaking change to
    /// something no compiler is watching - which is what makes these assertions worth their
    /// verbosity.
    /// </remarks>
    public class DeploymentSessionTests : IDisposable
    {
        private readonly string _directory;
        private readonly RecordingDeploymentEventSink _sink = new RecordingDeploymentEventSink();
        private readonly RecordingLogger _logger = new RecordingLogger();

        public DeploymentSessionTests()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "opticounters-session-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        [Fact]
        public void The_first_capture_reports_a_baseline_manifest()
        {
            using var session = NewSession();

            Assert.True(session.Capture());

            var manifest = _sink.Single(DeploymentEventNames.Manifest);

            Assert.Equal("OptiCounters.DeploymentManifest", manifest.Name);
            Assert.Equal("Baseline", manifest["Transition"]);
            Assert.Equal("Startup", manifest["Reason"]);
            Assert.Equal("1", manifest["Sequence"]);
            Assert.Equal(session.Current!.Fingerprint, manifest["Fingerprint"]);
            Assert.Null(manifest["PreviousFingerprint"]);
        }

        [Fact]
        public void The_manifest_names_the_instance_so_a_query_can_group_by_it()
        {
            // The reason the fleet-divergence query is possible at all: without a per-instance
            // identity on the row, "are all six instances running the same bits" cannot be asked.
            using var session = NewSession();

            session.Capture();

            var manifest = _sink.Single(DeploymentEventNames.Manifest);

            Assert.False(string.IsNullOrWhiteSpace(manifest["InstanceId"]));
            Assert.False(string.IsNullOrWhiteSpace(manifest["RoleName"]));
            Assert.False(string.IsNullOrWhiteSpace(manifest["MachineName"]));
            Assert.False(string.IsNullOrWhiteSpace(manifest["Runtime"]));
            Assert.Equal("V12", manifest["OptimizelyVersion"]);
        }

        [Fact]
        public void The_manifest_says_which_state_store_won()
        {
            using var session = NewSession();

            session.Capture();

            // On every row, because it is the explanation for the absence of change events. An
            // environment reporting Ephemeral or None is telling the operator why the diff has to
            // come from the query rather than from the instance.
            Assert.Equal(
                session.StateStore.ToString(),
                _sink.Single(DeploymentEventNames.Manifest)["StateStore"]);
        }

        [Fact]
        public void The_first_capture_reports_the_whole_inventory()
        {
            using var session = NewSession();

            session.Capture();

            var inventory = _sink.Named(DeploymentEventNames.Inventory);

            Assert.Equal(session.Current!.Count, inventory.Count);
            Assert.All(inventory, row => Assert.Equal(session.Current.Fingerprint, row["Fingerprint"]));
            Assert.Contains(inventory, row => row["Assembly"] == "Optimizely.Performance.Counters.Core");
        }

        [Fact]
        public void An_inventory_row_carries_the_fields_a_dll_question_is_answered_from()
        {
            using var session = NewSession();

            session.Capture();

            var row = _sink
                .Named(DeploymentEventNames.Inventory)
                .Single(recorded => recorded["Assembly"] == "Optimizely.Performance.Counters.Core");

            Assert.Equal("true", row["Managed"]);
            Assert.False(string.IsNullOrWhiteSpace(row["Version"]));
            Assert.Equal(
                typeof(DeploymentManifest).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
                row["Mvid"]);

            // Deliberately absent. The rows are joined to the manifest on the fingerprint, which is
            // content-addressed, so repeating the instance on several hundred rows would multiply
            // the volume to say what one row already said.
            Assert.Null(row["InstanceId"]);
        }

        [Fact]
        public void File_paths_are_left_out_unless_asked_for()
        {
            using (var session = NewSession())
            {
                session.Capture();

                Assert.All(
                    _sink.Named(DeploymentEventNames.Inventory),
                    row => Assert.Null(row["Path"]));

                Assert.Null(_sink.Single(DeploymentEventNames.Manifest)["ScanDirectory"]);
            }

            _sink.Clear();

            using var verbose = NewSession(options => options.IncludeFilePaths = true);

            verbose.Capture();

            Assert.All(
                _sink.Named(DeploymentEventNames.Inventory),
                row => Assert.False(string.IsNullOrWhiteSpace(row["Path"])));
        }

        [Fact]
        public void A_second_capture_of_an_unchanged_deployment_is_a_heartbeat_and_nothing_else()
        {
            using var session = NewSession();

            session.Capture();
            _sink.Clear();

            session.Capture();

            var manifest = _sink.Single(DeploymentEventNames.Manifest);

            Assert.Equal("Heartbeat", manifest["Reason"]);
            Assert.Equal("Unchanged", manifest["Transition"]);
            Assert.Equal("2", manifest["Sequence"]);

            // The heartbeat is one row. Repeating several hundred inventory rows every fifteen
            // minutes would be most of this feature's cost for none of its value.
            Assert.Empty(_sink.Named(DeploymentEventNames.Inventory));
            Assert.Empty(_sink.Named(DeploymentEventNames.Changed));
        }

        [Fact]
        public void The_inventory_can_be_turned_off_without_losing_the_manifest()
        {
            using var session = NewSession(options => options.InventoryMode = "Never");

            session.Capture();

            Assert.Empty(_sink.Named(DeploymentEventNames.Inventory));
            Assert.Single(_sink.Named(DeploymentEventNames.Manifest));
        }

        [Fact]
        public void Always_repeats_the_inventory_on_every_capture()
        {
            using var session = NewSession(options => options.InventoryMode = "Always");

            session.Capture();
            _sink.Clear();
            session.Capture();

            Assert.NotEmpty(_sink.Named(DeploymentEventNames.Inventory));
        }

        [Fact]
        public void An_unrecognised_inventory_mode_behaves_as_the_default()
        {
            using var session = NewSession(options => options.InventoryMode = "sometimes");

            session.Capture();
            var first = _sink.Named(DeploymentEventNames.Inventory).Count;

            _sink.Clear();
            session.Capture();

            Assert.NotEqual(0, first);
            Assert.Empty(_sink.Named(DeploymentEventNames.Inventory));
        }

        [Fact]
        public void A_deployment_that_changed_since_the_last_start_is_reported_per_assembly()
        {
            // The durable-state path: a previous manifest is on disk and differs from what is
            // deployed now. This is what a V11 site under IIS, a VM or a Windows App Service gets,
            // and what a Linux custom container with no persistent storage does not.
            var store = DeploymentStateStore.Open(
                Options(), DeploymentEnvironment.Detect("V12"), logger: null);

            var real = AssemblyInventoryScanner.Scan(new DeploymentOptions()).Manifest;

            // The same deployment, less one assembly and with one binary swapped: an added, a
            // removed and a changed, against a set that is otherwise genuinely on disk.
            var pretend = real.Assemblies
                .Skip(1)
                .Select(record => record.Key == real.Assemblies[1].Key
                    ? new AssemblyRecord(
                        record.Name,
                        record.AssemblyVersion,
                        record.FileVersion,
                        record.InformationalVersion,
                        Guid.NewGuid(),
                        record.Length,
                        record.IsManaged)
                    : record)
                .Concat(new[]
                {
                    new AssemblyRecord("Ghost", "1.0.0.0", "1.0.0.0", null, Guid.NewGuid(), 1, true),
                });

            store.Write(DeploymentManifest.Create(pretend, DateTimeOffset.UtcNow), logger: null);

            using var session = NewSession();

            session.Capture();

            var manifest = _sink.Single(DeploymentEventNames.Manifest);

            Assert.Equal("Changed", manifest["Transition"]);
            Assert.False(string.IsNullOrWhiteSpace(manifest["PreviousFingerprint"]));
            Assert.Equal("1", manifest["Removed"]);

            var changes = _sink.Named(DeploymentEventNames.Changed);

            Assert.Equal("OptiCounters.AssemblyChanged", changes[0].Name);

            var removed = changes.Single(change => change["Assembly"] == "Ghost");
            Assert.Equal("Removed", removed["Change"]);
            Assert.Equal("1.0.0.0", removed["PreviousVersion"]);
            Assert.Null(removed["Version"]);

            Assert.Contains(changes, change => change["Change"] == "Added");
            Assert.Contains(changes, change => change["Change"] == "Changed");

            // A changed row names the instance, unlike an inventory row: a partial swap is exactly
            // the case where one instance changed and another did not.
            Assert.All(changes, change => Assert.False(string.IsNullOrWhiteSpace(change["InstanceId"])));

            // The whole reason for reading the MVID. A rebuild that did not move the version string
            // is invisible to a version comparison and is called out here as its own dimension.
            var rebuilt = changes.Single(
                change => change["Change"] == "Changed" && change["Assembly"] == real.Assemblies[1].Name);

            Assert.Equal("true", rebuilt["SameVersionDifferentBinary"]);
            Assert.NotEqual(rebuilt["Mvid"], rebuilt["PreviousMvid"]);
        }

        [Fact]
        public void A_changed_deployment_reports_its_inventory_again()
        {
            // Otherwise the new fingerprint would appear on every request and on every manifest row
            // with nothing anywhere saying what it consists of.
            var store = DeploymentStateStore.Open(
                Options(), DeploymentEnvironment.Detect("V12"), logger: null);

            store.Write(
                DeploymentManifest.Create(
                    new[] { new AssemblyRecord("Ghost", "1.0", "1.0", null, Guid.NewGuid(), 1, true) },
                    DateTimeOffset.UtcNow),
                logger: null);

            using var session = NewSession();

            session.Capture();

            Assert.Equal(session.Current!.Count, _sink.Named(DeploymentEventNames.Inventory).Count);
        }

        [Fact]
        public void The_per_assembly_change_rows_are_capped_but_the_counts_are_not()
        {
            var store = DeploymentStateStore.Open(
                Options(), DeploymentEnvironment.Detect("V12"), logger: null);

            // Nothing in common with what is on disk, so every file is an add and every ghost a
            // removal - the framework-upgrade shape, where listing them all says nothing the counts
            // do not already say.
            store.Write(
                DeploymentManifest.Create(
                    Enumerable.Range(0, 50).Select(i => new AssemblyRecord(
                        "Ghost" + i, "1.0", "1.0", null, Guid.NewGuid(), 1, true)),
                    DateTimeOffset.UtcNow),
                logger: null);

            using var session = NewSession(options => options.MaxChangeEvents = 5);

            session.Capture();

            Assert.Equal(5, _sink.Named(DeploymentEventNames.Changed).Count);

            var manifest = _sink.Single(DeploymentEventNames.Manifest);

            Assert.Equal("50", manifest["Removed"]);
            Assert.Equal(session.Current!.Count.ToString(), manifest["Added"]);
        }

        [Fact]
        public void A_peer_on_a_different_deployment_is_reported_as_divergence()
        {
            using var session = NewSession();

            session.Capture();

            Assert.Empty(_sink.Named(DeploymentEventNames.Divergence));

            File.WriteAllText(
                Path.Combine(_directory, "manifest-other-instance.txt"),
                DeploymentManifest.Create(
                    new[] { new AssemblyRecord("Ghost", "1.0", "1.0", null, Guid.NewGuid(), 1, true) },
                    DateTimeOffset.UtcNow).Serialize(),
                Encoding.UTF8);

            _sink.Clear();
            session.Capture();

            var divergence = _sink.Single(DeploymentEventNames.Divergence);

            Assert.Equal("OptiCounters.FleetDivergence", divergence.Name);
            Assert.Equal("1", divergence["PeerCount"]);
            Assert.Equal("1", divergence["DivergentPeerCount"]);
            Assert.Equal(session.Current!.Fingerprint, divergence["Fingerprint"]);
            Assert.NotEqual(divergence["Fingerprint"], divergence["PeerFingerprints"]);

            // Worth a warning in the log as well as an event: a rollout in progress looks like this
            // and resolves, and one that does not resolve is a partial swap.
            Assert.Contains(_logger.Entries, entry => entry.Message.Contains("partial swap"));
        }

        [Fact]
        public void A_peer_on_the_same_deployment_is_not_reported()
        {
            using var session = NewSession();

            session.Capture();

            File.WriteAllText(
                Path.Combine(_directory, "manifest-other-instance.txt"),
                session.Current!.Serialize(),
                Encoding.UTF8);

            _sink.Clear();
            session.Capture();

            Assert.Empty(_sink.Named(DeploymentEventNames.Divergence));
        }

        [Fact]
        public void Every_event_name_is_prefixed_so_one_query_finds_all_of_them()
        {
            using var session = NewSession();

            session.Capture();

            Assert.NotEmpty(_sink.Events);
            Assert.All(_sink.Events, recorded => Assert.StartsWith(
                DeploymentEventNames.Prefix, recorded.Name, StringComparison.Ordinal));
        }

        [Fact]
        public void A_capture_logs_nothing_at_error_level()
        {
            // The standing rule for everything in this package: a diagnostic that cannot do its job
            // says so quietly and does not become the incident.
            using var session = NewSession();

            session.Capture();
            session.Capture();

            Assert.True(!_logger.Failures.Any(), _logger.Transcript());
        }

        /// <remarks>
        /// The telemetry stamp is off here and covered by
        /// <see cref="DeploymentFingerprintInitializerTests"/> instead. Leaving it on would have
        /// every session in this class reach for <c>TelemetryConfiguration.Active</c> - a
        /// process-wide static that spins up a real channel - to test something none of these
        /// assertions are about.
        /// </remarks>
        private DeploymentOptions Options() =>
            new DeploymentOptions { StatePath = _directory, StampTelemetry = false };

        private DeploymentSession NewSession(Action<DeploymentOptions>? configure = null)
        {
            var options = Options();

            configure?.Invoke(options);

            return new DeploymentSession(options, "V12", serviceProvider: null, _logger, _sink);
        }
    }
}
