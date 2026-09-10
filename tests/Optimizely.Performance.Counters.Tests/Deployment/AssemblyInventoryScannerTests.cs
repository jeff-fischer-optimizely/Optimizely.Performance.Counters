using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Optimizely.Performance.Counters.Core.Deployment;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Covers the scan of the deployment directory.
    /// </summary>
    /// <remarks>
    /// The test output directory is a reasonable stand-in for a deployment: it holds the product
    /// assemblies, the Optimizely packages, xunit and a pile of Microsoft.Extensions libraries, all
    /// built by different toolchains. What it is not is a site's bin folder, so the tests here
    /// assert on properties that must hold of any directory rather than on a particular count.
    /// </remarks>
    public class AssemblyInventoryScannerTests
    {
        [Fact]
        public void The_scan_finds_the_assembly_it_is_running_from()
        {
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions());

            var self = scan.Manifest.Assemblies.SingleOrDefault(
                record => record.Key == "optimizely.performance.counters.core");

            Assert.True(self != null, $"Scanned {scan.Directory} and did not find Core.");
            Assert.True(self!.IsManaged);
            Assert.Equal(typeof(DeploymentManifest).Assembly.ManifestModule.ModuleVersionId, self.Mvid);
        }

        [Fact]
        public void The_scan_reads_the_deployment_directory_rather_than_the_loaded_set()
        {
            // The distinction the whole class exists for. .NET loads lazily, so the loaded set at
            // any moment is a subset that depends on what the process happened to do first - two
            // instances of one deployment would fingerprint differently.
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions());

            Assert.Equal(InventorySource.DeploymentDirectory, scan.Source);

            // Not a count comparison: a test host loads plenty from outside its own output
            // directory - the runtime, the test framework, the reference assemblies - so more
            // assemblies are loaded here than there are files to scan. What separates the two
            // sources is which files are reported, not how many.
            var loaded = new HashSet<string>(
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic)
                    .Select(assembly => assembly.GetName().Name ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

            var unloaded = Directory
                .GetFiles(scan.Directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !loaded.Contains(name))
                .ToList();

            Assert.True(
                unloaded.Count > 0,
                $"Every file in {scan.Directory} is already loaded, so this test cannot tell the " +
                "two sources apart.");

            var scanned = new HashSet<string>(
                scan.Manifest.Assemblies.Select(record => record.Key), StringComparer.OrdinalIgnoreCase);

            Assert.All(unloaded, name => Assert.Contains(name, scanned));
        }

        [Fact]
        public void Every_record_carries_something_that_identifies_the_file()
        {
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions());

            Assert.NotEmpty(scan.Manifest.Assemblies);
            Assert.All(scan.Manifest.Assemblies, record => Assert.False(
                string.IsNullOrWhiteSpace(record.Identity),
                $"{record.Name} has no identity, so a change to it could never be detected."));
        }

        [Fact]
        public void The_records_are_ordered_and_unique_by_key()
        {
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions());
            var keys = scan.Manifest.Assemblies.Select(record => record.Key).ToList();

            Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
            Assert.Equal(keys.Distinct(StringComparer.Ordinal).Count(), keys.Count);
        }

        [Fact]
        public void Only_the_top_level_is_scanned()
        {
            // A publish output has runtimes/ beneath it holding the same native libraries once per
            // architecture, of which at most one is ever loaded. Recursing would report several
            // copies of each as deployed.
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions());
            var nested = Directory.GetDirectories(scan.Directory).ToList();

            if (nested.Count == 0)
            {
                return;
            }

            var topLevel = Directory
                .GetFiles(scan.Directory, "*.dll", SearchOption.TopDirectoryOnly)
                .Length;

            Assert.True(
                scan.Manifest.Count <= topLevel,
                $"Reported {scan.Manifest.Count} assemblies from a directory holding {topLevel} " +
                "at the top level, so the scan recursed.");
        }

        [Fact]
        public void System_assemblies_can_be_excluded()
        {
            var withThem = AssemblyInventoryScanner.Scan(new DeploymentOptions());
            var withoutThem = AssemblyInventoryScanner.Scan(
                new DeploymentOptions { IncludeSystemAssemblies = false });

            Assert.True(withoutThem.Manifest.Count < withThem.Manifest.Count);
            Assert.DoesNotContain(
                withoutThem.Manifest.Assemblies,
                record => record.Key.StartsWith("system.", StringComparison.Ordinal));

            // And excluding them changes the fingerprint, which is why the setting has to be stable
            // across a fleet: two instances configured differently would look like two deployments.
            Assert.NotEqual(withThem.Manifest.Fingerprint, withoutThem.Manifest.Fingerprint);
        }

        [Fact]
        public void The_cap_is_reported_as_a_truncation_rather_than_passed_off_as_complete()
        {
            var scan = AssemblyInventoryScanner.Scan(new DeploymentOptions { MaxAssemblies = 3 });

            // Three from the directory, or the loaded-assembly fallback if three managed files was
            // read as an implausible deployment - which it is. Either way it must say so rather
            // than report three files as the whole deployment.
            Assert.True(
                scan.Manifest.Truncated || scan.Source == InventorySource.LoadedAssemblies,
                "A capped scan reported as a complete inventory.");
        }

        [Fact]
        public void Two_scans_of_an_unchanged_directory_fingerprint_the_same()
        {
            // The property everything else rests on. If a scan were not reproducible, every
            // heartbeat would report a deployment.
            Assert.Equal(
                AssemblyInventoryScanner.Scan(new DeploymentOptions()).Manifest.Fingerprint,
                AssemblyInventoryScanner.Scan(new DeploymentOptions()).Manifest.Fingerprint);
        }

        [Fact]
        public void The_deployment_directory_exists()
        {
            Assert.True(Directory.Exists(AssemblyInventoryScanner.DeploymentDirectory()));
        }
    }
}
