using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Optimizely.Performance.Counters.Core.Configuration;
using Optimizely.Performance.Counters.Core.Deployment;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Deployment
{
    /// <summary>
    /// Whether the settings under <c>Optimizely:Instrumentation:Deployment</c> reach the options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The binder walks the options tree by reflection, so a new node arrives already bound and
    /// there is nothing to register - which is exactly why it is worth a test. It handles
    /// <c>bool</c>, <c>int</c> and <c>string</c> and nothing else, and a property of any other type
    /// is not a compile error: it is silently unreachable from configuration, on every host, with
    /// its default frozen in.
    /// </para>
    /// <para>
    /// <see cref="InstrumentationConfiguration.Bind"/> rather than a real configuration file,
    /// because it is the one entry point all three majors share - V12 and V13 flatten
    /// <c>appsettings.json</c> into it and V11 flattens <c>appSettings</c> into it - so binding
    /// proved here is binding proved everywhere.
    /// </para>
    /// </remarks>
    public class DeploymentOptionsBindingTests
    {
        private const string Prefix = "Deployment:";

        [Fact]
        public void Nothing_configured_leaves_the_defaults()
        {
            var options = InstrumentationConfiguration
                .Bind(new Dictionary<string, string?>(), logger: null)
                .Deployment;

            var defaults = new DeploymentOptions();

            Assert.Equal(defaults.Enabled, options.Enabled);
            Assert.Equal(defaults.InventoryMode, options.InventoryMode);
            Assert.Equal(defaults.HeartbeatMinutes, options.HeartbeatMinutes);
            Assert.Null(options.StatePath);
        }

        [Fact]
        public void Every_setting_in_the_node_binds()
        {
            var options = InstrumentationConfiguration.Bind(
                new Dictionary<string, string?>
                {
                    [Prefix + "Enabled"] = "false",
                    [Prefix + "StatePath"] = @"D:\home\data\opticounters",
                    [Prefix + "InventoryMode"] = "Always",
                    [Prefix + "HeartbeatMinutes"] = "60",
                    [Prefix + "InventoryReemitHours"] = "24",
                    [Prefix + "IncludeSystemAssemblies"] = "false",
                    [Prefix + "IncludeFilePaths"] = "true",
                    [Prefix + "StampTelemetry"] = "false",
                    [Prefix + "MaxChangeEvents"] = "50",
                    [Prefix + "MaxAssemblies"] = "500",
                },
                logger: null).Deployment;

            Assert.False(options.Enabled);
            Assert.Equal(@"D:\home\data\opticounters", options.StatePath);
            Assert.Equal("Always", options.InventoryMode);
            Assert.Equal(60, options.HeartbeatMinutes);
            Assert.Equal(24, options.InventoryReemitHours);
            Assert.False(options.IncludeSystemAssemblies);
            Assert.True(options.IncludeFilePaths);
            Assert.False(options.StampTelemetry);
            Assert.Equal(50, options.MaxChangeEvents);
            Assert.Equal(500, options.MaxAssemblies);
        }

        /// <remarks>
        /// The test above names each setting by hand and would still pass with a new one added and
        /// forgotten. This one fails in that case, and fails for the reason that matters: the
        /// binder takes <c>bool</c>, <c>int</c> and <c>string</c>, so a <c>TimeSpan</c> option -
        /// the obvious way to express a heartbeat, and why the heartbeat is an <c>int</c> of
        /// minutes instead - would be unconfigurable rather than rejected.
        /// </remarks>
        [Fact]
        public void No_setting_in_the_node_is_unreachable_from_configuration()
        {
            var unreachable = new List<string>();
            var settings = new Dictionary<string, string?>();

            foreach (var property in typeof(DeploymentOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                var probe = Probe(property.PropertyType);

                if (probe == null)
                {
                    unreachable.Add(property.Name + " (" + property.PropertyType.Name + ")");
                    continue;
                }

                settings[Prefix + property.Name] = probe;
            }

            Assert.True(
                unreachable.Count == 0,
                "The configuration binder handles bool, int and string only, so these settings " +
                "cannot be set by an operator on any host: " + string.Join(", ", unreachable));

            var logger = new RecordingLogger();
            var options = InstrumentationConfiguration.Bind(settings, logger).Deployment;

            // Nothing reported as unrecognised: the paths a settings file would use are the
            // property names, and the walk found every one of them.
            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.Message.IndexOf("Deployment:", StringComparison.OrdinalIgnoreCase) >= 0);

            // And every probe value actually landed, so a property that binds to nothing - a
            // getter-only shape the reflection above still counted as writable - is caught too.
            foreach (var property in typeof(DeploymentOptions)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite))
            {
                var expected = Probe(property.PropertyType);
                var actual = Convert.ToString(property.GetValue(options), CultureInfo.InvariantCulture);

                Assert.True(
                    string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                    $"Deployment:{property.Name} was set to '{expected}' and read back as '{actual}'.");
            }
        }

        [Fact]
        public void A_misspelled_setting_is_reported_rather_than_ignored()
        {
            // The reason the binder is hand-written rather than Microsoft.Extensions' - see the
            // remarks on InstrumentationConfiguration. StatePath is the one an operator is most
            // likely to type, and a silently ignored StatePath means no change events at all on a
            // host where the state would otherwise have been durable.
            var logger = new RecordingLogger();

            InstrumentationConfiguration.Bind(
                new Dictionary<string, string?> { [Prefix + "StatePth"] = @"D:\home\data" }, logger);

            Assert.Contains(
                logger.Entries,
                entry => entry.Message.IndexOf("StatePth", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void A_comment_in_the_template_is_not_a_misspelled_setting()
        {
            var logger = new RecordingLogger();

            InstrumentationConfiguration.Bind(
                new Dictionary<string, string?>
                {
                    [Prefix + "_comment"] = "why this node exists",
                    [Prefix + "_comment_StatePath"] = "where the last manifest is kept",
                    [Prefix + "Enabled"] = "true",
                },
                logger);

            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.Message.IndexOf("_comment", StringComparison.Ordinal) >= 0);
        }

        /// <summary>A value to set a property of this type to, or null if the binder cannot.</summary>
        private static string? Probe(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(bool))
            {
                // The opposite of the default for every bool in this node but IncludeFilePaths,
                // which the named test above covers explicitly.
                return "false";
            }

            if (underlying == typeof(int))
            {
                return "7";
            }

            return underlying == typeof(string) ? "a-value" : null;
        }
    }
}
