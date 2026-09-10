using System;
using System.Linq;
using Optimizely.Performance.Counters.Core.Deployment;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Covers the fingerprint and the diff.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the join key for the whole feature: it appears on every telemetry item as
    /// a custom dimension, on every manifest event, and on every inventory row. If it were unstable
    /// - different on two instances of the same deployment, or different across a restart - every
    /// query built on it would be silently wrong rather than visibly broken, which is why so much of
    /// what follows is about what must <em>not</em> change it.
    /// </remarks>
    public class DeploymentManifestTests
    {
        [Fact]
        public void The_same_files_in_a_different_order_fingerprint_the_same()
        {
            // Two instances scanning the same deployment get their directory listing back in
            // whatever order the file system feels like. If that reached the fingerprint, a
            // perfectly consistent fleet would report as diverged.
            var records = new[] { Record("Alpha"), Record("Beta"), Record("Gamma") };

            var forwards = DeploymentManifest.Create(records, DateTimeOffset.UtcNow);
            // Enumerable.Reverse by name, because records.Reverse() on an array binds to
            // MemoryExtensions.Reverse - which reverses in place and returns void.
            var backwards = DeploymentManifest.Create(
                Enumerable.Reverse(records), DateTimeOffset.UtcNow);

            Assert.Equal(forwards.Fingerprint, backwards.Fingerprint);
        }

        [Fact]
        public void The_capture_time_does_not_reach_the_fingerprint()
        {
            // Otherwise every heartbeat would report a new deployment.
            var records = new[] { Record("Alpha") };

            Assert.Equal(
                DeploymentManifest.Create(records, DateTimeOffset.UtcNow).Fingerprint,
                DeploymentManifest.Create(records, DateTimeOffset.UtcNow.AddDays(-40)).Fingerprint);
        }

        [Fact]
        public void A_rebuild_of_the_same_version_changes_the_fingerprint()
        {
            // The case a version comparison cannot see, and the reason the MVID is read at all.
            var before = DeploymentManifest.Create(new[] { Record("Alpha", version: "1.0.0.0") }, Now);
            var after = DeploymentManifest.Create(new[] { Record("Alpha", version: "1.0.0.0") }, Now);

            Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        }

        [Fact]
        public void A_version_string_bumped_without_a_rebuild_does_not_change_the_fingerprint()
        {
            // Contrived, and it is the other half of the same rule: the fingerprint is about the
            // bits, and only the bits. Anything else and a rebuild that only touched an attribute
            // would report as a deployment.
            var mvid = Guid.NewGuid();

            var before = DeploymentManifest.Create(new[] { Record("Alpha", mvid: mvid, version: "1.0.0.0") }, Now);
            var after = DeploymentManifest.Create(new[] { Record("Alpha", mvid: mvid, version: "9.9.9.9") }, Now);

            Assert.Equal(before.Fingerprint, after.Fingerprint);
        }

        [Fact]
        public void A_name_differing_only_in_case_does_not_change_the_fingerprint()
        {
            // The same deployment is developed on Windows and runs on Linux. If case reached the
            // key, the two would never agree.
            var mvid = Guid.NewGuid();

            Assert.Equal(
                DeploymentManifest.Create(new[] { Record("Alpha", mvid: mvid) }, Now).Fingerprint,
                DeploymentManifest.Create(new[] { Record("ALPHA", mvid: mvid) }, Now).Fingerprint);
        }

        [Fact]
        public void The_fingerprint_is_sixteen_lower_case_hex_characters()
        {
            var fingerprint = DeploymentManifest.Create(new[] { Record("Alpha") }, Now).Fingerprint;

            Assert.Equal(16, fingerprint.Length);
            Assert.All(fingerprint, c => Assert.True(
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                $"'{c}' is not a lower-case hex digit."));
        }

        [Fact]
        public void No_previous_manifest_is_a_baseline_rather_than_an_empty_one()
        {
            // The single most consequential branch in the feature. On the container topology this
            // was built for, most starts have no previous manifest, and treating that as "it used
            // to have nothing" would report every assembly in the deployment as newly added on
            // every cold start of every container.
            var current = DeploymentManifest.Create(new[] { Record("Alpha"), Record("Beta") }, Now);

            var diff = current.DiffAgainst(null);

            Assert.True(diff.IsBaseline);
            Assert.False(diff.HasChanged);
            Assert.Empty(diff.Changes);
            Assert.Equal(0, diff.Added);
        }

        [Fact]
        public void A_restart_that_finds_the_deployment_unchanged_reports_no_change()
        {
            var records = new[] { Record("Alpha"), Record("Beta") };
            var previous = DeploymentManifest.Create(records, Now);
            var current = DeploymentManifest.Create(records, Now);

            var diff = current.DiffAgainst(previous);

            Assert.False(diff.IsBaseline);
            Assert.False(diff.HasChanged);
            Assert.Empty(diff.Changes);
        }

        [Fact]
        public void The_diff_names_what_was_added_changed_and_removed()
        {
            var kept = Record("Kept");
            var goingAway = Record("Removed");
            var beforeChange = Record("Changed", version: "1.0.0.0");

            var previous = DeploymentManifest.Create(new[] { kept, goingAway, beforeChange }, Now);

            var afterChange = Record("Changed", version: "2.0.0.0");
            var arriving = Record("Added");

            var current = DeploymentManifest.Create(new[] { kept, afterChange, arriving }, Now);

            var diff = current.DiffAgainst(previous);

            Assert.True(diff.HasChanged);
            Assert.Equal(1, diff.Added);
            Assert.Equal(1, diff.Changed);
            Assert.Equal(1, diff.Removed);

            var added = diff.Changes.Single(change => change.Kind == AssemblyChangeKind.Added);
            Assert.Equal("Added", added.Name);
            Assert.Null(added.Previous);

            var removed = diff.Changes.Single(change => change.Kind == AssemblyChangeKind.Removed);
            Assert.Equal("Removed", removed.Name);
            Assert.Null(removed.Current);

            var changed = diff.Changes.Single(change => change.Kind == AssemblyChangeKind.Changed);
            Assert.Equal("2.0.0.0", changed.Current!.DisplayVersion);
            Assert.Equal("1.0.0.0", changed.Previous!.DisplayVersion);
        }

        [Fact]
        public void A_rollback_returns_to_the_fingerprint_it_had_before()
        {
            // Not an accident of the implementation - it is the reason for content-addressing. A
            // rollback has to read as a return to a known deployment rather than as a third one.
            var first = DeploymentManifest.Create(new[] { Record("Alpha", mvid: KnownA) }, Now);
            var second = DeploymentManifest.Create(new[] { Record("Alpha", mvid: KnownB) }, Now);
            var rolledBack = DeploymentManifest.Create(new[] { Record("Alpha", mvid: KnownA) }, Now);

            Assert.NotEqual(first.Fingerprint, second.Fingerprint);
            Assert.Equal(first.Fingerprint, rolledBack.Fingerprint);
        }

        [Fact]
        public void A_native_library_without_an_mvid_falls_back_to_its_size_and_version()
        {
            var before = new AssemblyRecord("native", null, "1.0.0.0", null, Guid.Empty, 1024, false);
            var same = new AssemblyRecord("native", null, "1.0.0.0", null, Guid.Empty, 1024, false);
            var resized = new AssemblyRecord("native", null, "1.0.0.0", null, Guid.Empty, 2048, false);

            Assert.Equal(before.Identity, same.Identity);
            Assert.NotEqual(before.Identity, resized.Identity);
        }

        [Fact]
        public void A_manifest_survives_the_round_trip_through_the_state_file()
        {
            var original = DeploymentManifest.Create(
                new[]
                {
                    Record("Alpha", version: "1.2.3.4"),
                    new AssemblyRecord("native", null, "2.0", "2.0+abcdef", Guid.Empty, 4096, false),
                },
                new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));

            Assert.True(DeploymentManifest.TryParse(original.Serialize(), out var parsed));

            Assert.Equal(original.Fingerprint, parsed!.Fingerprint);
            Assert.Equal(original.Count, parsed.Count);
            Assert.Equal(original.CapturedUtc, parsed.CapturedUtc);
            Assert.Equal(
                original.Assemblies.Select(record => record.Identity),
                parsed.Assemblies.Select(record => record.Identity));
        }

        [Fact]
        public void A_manifest_truncated_mid_write_is_rejected_rather_than_read_short()
        {
            // Every line it does contain is well formed, so nothing about parsing a line catches
            // this. Read as a smaller deployment it would report the missing half as removed - a
            // hundred false change events from one badly timed process kill.
            var original = DeploymentManifest.Create(
                Enumerable.Range(0, 20).Select(i => Record("Assembly" + i)), Now);

            var lines = original.Serialize().Split('\n');
            var truncated = string.Join("\n", lines.Take(lines.Length / 2));

            Assert.False(DeploymentManifest.TryParse(truncated, out var parsed));
            Assert.Null(parsed);
        }

        [Fact]
        public void A_manifest_whose_contents_were_edited_is_rejected()
        {
            // The fingerprint is recomputed from what was read rather than trusted, so a state file
            // that has been tampered with or half-overwritten does not become a false baseline to
            // diff against.
            var original = DeploymentManifest.Create(new[] { Record("Alpha"), Record("Beta") }, Now);
            var edited = original.Serialize().Replace("Beta|", "Gamma|");

            Assert.False(DeploymentManifest.TryParse(edited, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("not a manifest at all")]
        [InlineData("opticounters-deployment/999\nfingerprint=x\ncaptured=y\ncount=0\n")]
        public void Anything_unrecognised_is_rejected(string? text)
        {
            Assert.False(DeploymentManifest.TryParse(text, out var parsed));
            Assert.Null(parsed);
        }

        private static readonly Guid KnownA = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid KnownB = new Guid("22222222-2222-2222-2222-222222222222");

        private static DateTimeOffset Now => DateTimeOffset.UtcNow;

        private static AssemblyRecord Record(
            string name, Guid? mvid = null, string version = "1.0.0.0") =>
            new AssemblyRecord(
                name,
                version,
                version,
                version,
                mvid ?? Guid.NewGuid(),
                length: 1024,
                isManaged: true);
    }
}
