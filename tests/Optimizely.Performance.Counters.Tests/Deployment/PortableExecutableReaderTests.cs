using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Optimizely.Performance.Counters.Core.Deployment;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Covers the hand-rolled PE reader.
    /// </summary>
    /// <remarks>
    /// The MVID is the whole reason the deployment fingerprint can tell a rebuild from a reship, so
    /// a reader that returned a plausible-but-wrong GUID would be worse than one that returned
    /// nothing: the fingerprint would still look stable and would still be meaningless. Every test
    /// here checks the parsed value against what the runtime says about the same file, rather than
    /// against a constant, because that is the only comparison that catches a misread offset.
    /// </remarks>
    public class PortableExecutableReaderTests
    {
        [Fact]
        public void The_mvid_matches_what_the_runtime_reports_for_the_same_file()
        {
            var assembly = typeof(DeploymentManifest).Assembly;
            var identity = PortableExecutableReader.TryRead(assembly.Location);

            Assert.NotNull(identity);
            Assert.True(identity!.Value.IsManaged);
            Assert.Equal(assembly.ManifestModule.ModuleVersionId, identity.Value.Mvid);
        }

        [Fact]
        public void Every_managed_assembly_beside_the_test_reads_back_correctly()
        {
            // A single file could pass by luck - a 32-bit image, one section, no #- stream. The
            // output directory holds xunit, the Extensions packages and the product assemblies,
            // built by different toolchains, which is a wider sample than anything hand-written.
            var loaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .Where(assembly => !string.IsNullOrEmpty(SafeLocation(assembly)))
                .Where(assembly => File.Exists(SafeLocation(assembly)))
                .GroupBy(assembly => SafeLocation(assembly)!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            Assert.NotEmpty(loaded);

            foreach (var assembly in loaded)
            {
                var path = SafeLocation(assembly)!;
                var identity = PortableExecutableReader.TryRead(path);

                Assert.True(identity != null, $"{path} read as not a PE image.");
                Assert.True(identity!.Value.IsManaged, $"{path} read as unmanaged.");
                Assert.True(
                    identity.Value.Mvid == assembly.ManifestModule.ModuleVersionId,
                    $"{path} read as MVID {identity.Value.Mvid}, runtime says " +
                    $"{assembly.ManifestModule.ModuleVersionId}.");
            }
        }

        [Fact]
        public void A_file_that_is_not_a_pe_image_reads_as_nothing()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");

            try
            {
                File.WriteAllText(path, "this is not an executable");

                // Null rather than an empty identity: the scanner drops these entirely, because a
                // .dll that is not a library - a resource blob, or a file caught mid-copy - is not
                // a deployed assembly and reporting it as one with a blank MVID would make the
                // fingerprint depend on whether a copy happened to be in progress.
                Assert.Null(PortableExecutableReader.TryRead(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_truncated_image_reads_as_nothing_rather_than_throwing()
        {
            // The header says one thing and the file stops before it. Every offset the reader
            // follows past that point is off the end, and none of them may throw: the scanner runs
            // over a live directory during a deployment, where half-written files are normal.
            var source = File.ReadAllBytes(typeof(DeploymentManifest).Assembly.Location);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");

            try
            {
                File.WriteAllBytes(path, source.Take(source.Length / 4).ToArray());

                var identity = PortableExecutableReader.TryRead(path);

                // Either answer is acceptable - it is still a PE header - as long as it is not an
                // exception and not a fabricated MVID.
                if (identity != null)
                {
                    Assert.Equal(Guid.Empty, identity.Value.Mvid);
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_missing_file_reads_as_nothing()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");

            Assert.Null(PortableExecutableReader.TryRead(path));
        }

        private static string? SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (Exception)
            {
                // A single-file or in-memory assembly has none.
                return null;
            }
        }
    }
}
