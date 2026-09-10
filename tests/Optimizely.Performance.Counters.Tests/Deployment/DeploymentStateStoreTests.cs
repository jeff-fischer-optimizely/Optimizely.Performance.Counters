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
    /// Covers the "semi" in semi-stateful.
    /// </summary>
    /// <remarks>
    /// The store is allowed to fail in every way a disk can, and the required consequence is always
    /// the same: a transition reported as a baseline rather than as a diff. What must never happen
    /// is a partial read becoming a false previous manifest, because that turns one bad file into a
    /// hundred false change events.
    /// </remarks>
    public class DeploymentStateStoreTests : IDisposable
    {
        private readonly string _directory;

        public DeploymentStateStoreTests()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "opticounters-state-" + Guid.NewGuid().ToString("N"));
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
        public void A_configured_path_is_used_and_created()
        {
            var store = Open(new DeploymentOptions { StatePath = _directory });

            Assert.Equal(StateStoreKind.Configured, store.Kind);
            Assert.Equal(StateDurability.Durable, store.Durability);
            Assert.True(Directory.Exists(_directory));
            Assert.StartsWith(_directory, store.Path!, StringComparison.Ordinal);
        }

        [Fact]
        public void A_manifest_written_here_is_read_back()
        {
            var store = Open(new DeploymentOptions { StatePath = _directory });
            var manifest = Manifest("Alpha", "Beta");

            Assert.True(store.Write(manifest, logger: null));

            var read = store.Read();

            Assert.NotNull(read);
            Assert.Equal(manifest.Fingerprint, read!.Fingerprint);
        }

        [Fact]
        public void Nothing_written_reads_as_nothing()
        {
            Assert.Null(Open(new DeploymentOptions { StatePath = _directory }).Read());
        }

        [Fact]
        public void A_corrupt_state_file_reads_as_nothing_rather_than_as_an_empty_deployment()
        {
            var store = Open(new DeploymentOptions { StatePath = _directory });

            store.Write(Manifest("Alpha", "Beta"), logger: null);
            File.WriteAllText(store.Path!, "opticounters-deployment/1\ngarbage\n", Encoding.UTF8);

            // Null, so the caller diffs against nothing and reports a baseline. An empty manifest
            // here would report every assembly in the deployment as newly added.
            Assert.Null(store.Read());
        }

        [Fact]
        public void An_unusable_configured_path_is_warned_about_and_fallen_past()
        {
            // A file where a directory was expected: CreateDirectory fails, and so would every
            // other route. Which failure it is does not matter - the point is that a setting an
            // operator got wrong must not be the reason a site fails to start.
            var file = Path.Combine(Path.GetTempPath(), "opticounters-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "not a directory");

            var logger = new RecordingLogger();

            try
            {
                var store = DeploymentStateStore.Open(
                    new DeploymentOptions { StatePath = Path.Combine(file, "state") },
                    DeploymentEnvironment.Detect("V12"),
                    logger);

                Assert.NotEqual(StateStoreKind.Configured, store.Kind);
                Assert.Contains(
                    logger.Entries,
                    entry => entry.Message.Contains("StatePath"));
                Assert.Empty(logger.Failures);
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void The_probe_finds_somewhere_without_being_told_where()
        {
            // No StatePath, so this walks the real candidate order and proves each one by writing
            // to it. It is also the only test that exercises that walk, which is the part an
            // operator never configures and always depends on.
            var store = Open(new DeploymentOptions());

            Assert.NotEqual(StateStoreKind.Configured, store.Kind);

            // Both halves on every event: which directory won, and whether what is written there is
            // expected to outlive a deployment. Without the second, "this environment emits no
            // change events" has no visible cause.
            Assert.Contains("/", store.ToString(), StringComparison.Ordinal);
            Assert.Equal(store.Kind + "/" + store.Durability, store.ToString());
        }

        [Fact]
        public void The_peers_in_a_shared_directory_are_read_and_this_instance_is_not()
        {
            var store = Open(new DeploymentOptions { StatePath = _directory });
            var mine = Manifest("Alpha");

            store.Write(mine, logger: null);

            var theirs = Manifest("Alpha", "Beta");
            File.WriteAllText(
                Path.Combine(_directory, "manifest-someone-else.txt"), theirs.Serialize(), Encoding.UTF8);

            var peers = store.ReadPeers();

            Assert.Equal("someone-else", peers.Keys.Single());
            Assert.Equal(theirs.Fingerprint, peers["someone-else"]);
        }

        [Fact]
        public void A_corrupt_peer_file_is_skipped_rather_than_failing_the_read()
        {
            var store = Open(new DeploymentOptions { StatePath = _directory });

            store.Write(Manifest("Alpha"), logger: null);

            var good = Manifest("Alpha", "Beta");
            File.WriteAllText(Path.Combine(_directory, "manifest-good.txt"), good.Serialize(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(_directory, "manifest-bad.txt"), "half a fi", Encoding.UTF8);

            var peers = store.ReadPeers();

            Assert.Equal("good", peers.Keys.Single());
        }

        [Fact]
        public void A_write_leaves_no_temporary_file_behind()
        {
            // Written to a .tmp and moved into place, so a process killed mid-write cannot leave a
            // truncated manifest. TryParse rejects one of those, but the cheaper fix is not to
            // create it - and a .tmp left lying around would mean the move never happened.
            var store = Open(new DeploymentOptions { StatePath = _directory });

            store.Write(Manifest("Alpha"), logger: null);
            store.Write(Manifest("Alpha", "Beta"), logger: null);

            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
            Assert.Equal(Manifest("Alpha", "Beta").Fingerprint, store.Read()!.Fingerprint);
        }

        private static DeploymentStateStore Open(DeploymentOptions options) =>
            DeploymentStateStore.Open(options, DeploymentEnvironment.Detect("V12"), logger: null);

        private static DeploymentManifest Manifest(params string[] names) =>
            DeploymentManifest.Create(
                names.Select(name => new AssemblyRecord(
                    name,
                    "1.0.0.0",
                    "1.0.0.0",
                    null,
                    Deterministic(name),
                    1024,
                    true)),
                DateTimeOffset.UtcNow);

        /// <remarks>
        /// Deterministic so two manifests built from the same names in two tests compare equal, and
        /// so a failure is reproducible rather than depending on a fresh GUID.
        /// </remarks>
        private static Guid Deterministic(string name)
        {
            var bytes = new byte[16];

            for (var i = 0; i < name.Length && i < 16; i++)
            {
                bytes[i] = (byte)name[i];
            }

            return new Guid(bytes);
        }
    }
}
