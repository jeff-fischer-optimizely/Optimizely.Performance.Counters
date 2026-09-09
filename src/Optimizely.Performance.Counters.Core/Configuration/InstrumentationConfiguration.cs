using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if NET472
using System.Collections.Specialized;
using System.Configuration;
#else
using Microsoft.Extensions.Configuration;
#endif

namespace Optimizely.Performance.Counters.Core.Configuration
{
    /// <summary>
    /// Reads <see cref="InstrumentationOptions"/> out of whatever the host calls configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// V12 and V13 have <c>IConfiguration</c>; V11 has <c>ConfigurationManager.AppSettings</c>.
    /// Both are flattened to the same colon-delimited paths and bound by the same code, so
    /// <c>Optimizely:Instrumentation:Probes:ThreadPool:Enabled</c> means one thing across all three
    /// versions and one template can be handed to any of them.
    /// </para>
    /// <para>
    /// Written here rather than delegated to <c>Microsoft.Extensions.Configuration.Binder</c> for
    /// two reasons. The binder cannot be used on V11 - there is no <c>IConfiguration</c> to bind
    /// from without adding a configuration provider to a .NET Framework site - so it would only ever
    /// have covered two versions out of three. And it ignores keys it does not recognise, which for
    /// a settings file nobody reads back means a typo silently does nothing; this reports them.
    /// </para>
    /// <para>
    /// Nothing here throws. A configuration file is written by hand, usually under pressure, and a
    /// site that will not start because a counter setting is misspelled is a far worse outcome than
    /// a counter that runs with its default. Every failure is logged and defaulted past.
    /// </para>
    /// </remarks>
    public static class InstrumentationConfiguration
    {
        // Deep enough for the tree in InstrumentationOptions several times over, and finite, which
        // is the point: the walk is driven by reflection over whatever type it is handed.
        private const int MaxDepth = 8;

        // Enough to show the operator the shape of the mistake. A file with more unrecognised keys
        // than this was pasted from somewhere else, and listing all of them helps nobody.
        private const int MaxReportedUnknownKeys = 10;

        /// <summary>
        /// Loads the options from the host's configuration.
        /// </summary>
        /// <param name="services">
        /// The container being configured, or null on V11. Used to find the host's
        /// <c>IConfiguration</c> without building a service provider.
        /// </param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The bound options, or the defaults if there was nothing to bind from.</returns>
        public static InstrumentationOptions Load(IServiceCollection? services, ILogger? logger)
        {
#if NET472
            _ = services;
            return FromAppSettings(ConfigurationManager.AppSettings, logger);
#else
            var configuration = FindConfiguration(services);

            if (configuration == null)
            {
                // Not a warning. A host with no IConfiguration registered as an instance is unusual
                // but legitimate, and the defaults are the values this package would have used
                // before it read configuration at all.
                logger?.LogInformation(
                    "No IConfiguration was available during container configuration, so the " +
                    "instrumentation defaults are in use. Settings under '{SectionName}' were not read.",
                    InstrumentationOptions.SectionName);

                return new InstrumentationOptions();
            }

            return FromConfiguration(configuration, logger);
#endif
        }

#if NET472
        /// <summary>
        /// Binds from V11's <c>appSettings</c>, taking the keys prefixed with the section name.
        /// </summary>
        /// <param name="appSettings">The settings to read; usually <c>ConfigurationManager.AppSettings</c>.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The bound options.</returns>
        /// <remarks>
        /// Plain <c>appSettings</c> keys rather than a custom <c>ConfigurationSection</c>, which is
        /// what <c>Optimizely.Performance.DotNetCounters</c> uses on this version. That package
        /// configures a <em>list</em> of counters, which XML expresses well and appSettings does
        /// not. Everything here is a scalar, so a section would buy six nested
        /// <c>ConfigurationElement</c> types and cost the operator a different key syntax from the
        /// one they will use when the site moves to V12.
        /// </remarks>
        public static InstrumentationOptions FromAppSettings(
            NameValueCollection appSettings, ILogger? logger)
        {
            if (appSettings == null)
            {
                return new InstrumentationOptions();
            }

            var prefix = InstrumentationOptions.SectionName + ":";
            var settings = new List<KeyValuePair<string, string?>>();

            foreach (var key in appSettings.AllKeys)
            {
                if (key != null && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    settings.Add(new KeyValuePair<string, string?>(
                        key.Substring(prefix.Length), appSettings[key]));
                }
            }

            return Bind(settings, logger);
        }
#else
        /// <summary>
        /// Binds from the host's <c>IConfiguration</c>.
        /// </summary>
        /// <param name="configuration">The host's configuration root or any section above ours.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The bound options.</returns>
        public static InstrumentationOptions FromConfiguration(
            IConfiguration configuration, ILogger? logger)
        {
            if (configuration == null)
            {
                return new InstrumentationOptions();
            }

            var section = configuration.GetSection(InstrumentationOptions.SectionName);

            // Paths relative to the section, so the binder below sees the same shape it sees from
            // appSettings on V11. Intermediate nodes come through with a null value and are
            // dropped: they name a subsection rather than a setting.
            var settings = section
                .AsEnumerable(makePathsRelative: true)
                .Where(pair => pair.Value != null && pair.Key.Length > 0);

            return Bind(settings, logger);
        }

        /// <remarks>
        /// Reads the descriptor rather than calling <c>BuildServiceProvider</c>. Building a second
        /// provider during container configuration gives every singleton resolved through it a
        /// second instance, which for a logger is wasteful and for anything stateful is a bug -
        /// it is the same trap <c>DeferredLogger</c> exists to avoid.
        /// <para>
        /// The generic host registers its configuration as an instance, which is what makes this
        /// work. A host that registers it by factory instead cannot be read this early, and
        /// <see cref="Load"/> falls back to the defaults and says so.
        /// </para>
        /// </remarks>
        private static IConfiguration? FindConfiguration(IServiceCollection? services) =>
            services?
                .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))?
                .ImplementationInstance as IConfiguration;
#endif

        /// <summary>
        /// Binds a flat set of colon-delimited paths, relative to the section, onto a fresh
        /// <see cref="InstrumentationOptions"/>.
        /// </summary>
        /// <param name="settings">Path and value pairs, for example <c>Probes:ThreadPool:Enabled</c>.</param>
        /// <param name="logger">Log sink; may be null.</param>
        /// <returns>The bound options. Anything not named keeps its default.</returns>
        public static InstrumentationOptions Bind(
            IEnumerable<KeyValuePair<string, string?>> settings, ILogger? logger)
        {
            var options = new InstrumentationOptions();

            if (settings == null)
            {
                return options;
            }

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in settings)
            {
                if (!string.IsNullOrEmpty(pair.Key) && !IsComment(pair.Key))
                {
                    // Last wins. Configuration providers already resolve their own precedence
                    // before we see the values, so a duplicate here is a duplicate in one file.
                    values[pair.Key] = pair.Value;
                }
            }

            if (values.Count == 0)
            {
                return options;
            }

            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BindObject(options, prefix: string.Empty, values, consumed, logger, depth: 0);
            ReportUnknownKeys(values, consumed, logger);

            return options;
        }

        private static void BindObject(
            object target,
            string prefix,
            IReadOnlyDictionary<string, string?> values,
            HashSet<string> consumed,
            ILogger? logger,
            int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            foreach (var property in target.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;

                if (IsLeaf(property.PropertyType))
                {
                    if (property.CanWrite && values.TryGetValue(path, out var raw))
                    {
                        consumed.Add(path);
                        AssignLeaf(target, property, path, raw, logger);
                    }

                    continue;
                }

                // A subsection. The default instance is always there, so there is nothing to
                // construct - the tree is fully populated by its own initializers.
                var child = property.GetValue(target);

                if (child != null)
                {
                    BindObject(child, path, values, consumed, logger, depth + 1);
                }
            }
        }

        private static void AssignLeaf(
            object target, PropertyInfo property, string path, string? raw, ILogger? logger)
        {
            if (property.PropertyType == typeof(string))
            {
                property.SetValue(target, raw);
                return;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                // An empty value is how a configuration file says "I have not decided". Treated as
                // absent rather than as a parse failure, which would be a warning about nothing.
                return;
            }

            var text = raw!.Trim();

            if (property.PropertyType == typeof(bool))
            {
                if (bool.TryParse(text, out var flag))
                {
                    property.SetValue(target, flag);
                    return;
                }
            }
            else if (property.PropertyType == typeof(int))
            {
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    property.SetValue(target, number);
                    return;
                }
            }

            logger?.LogWarning(
                "'{SectionName}:{Path}' is set to '{Value}', which is not a valid {Type}. The " +
                "default of {Default} is in use instead.",
                InstrumentationOptions.SectionName,
                path,
                text,
                property.PropertyType.Name,
                property.GetValue(target));
        }

        /// <remarks>
        /// The reason this class binds by hand. A misspelled setting is indistinguishable from an
        /// absent one at runtime, and the operator's evidence that they configured anything is a
        /// counter that does not change - which is also what a correctly configured counter looks
        /// like on a healthy site.
        /// </remarks>
        private static void ReportUnknownKeys(
            IReadOnlyDictionary<string, string?> values, HashSet<string> consumed, ILogger? logger)
        {
            if (logger == null)
            {
                return;
            }

            var unknown = values.Keys
                .Where(key => !consumed.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unknown.Count == 0)
            {
                return;
            }

            var shown = string.Join(", ", unknown.Take(MaxReportedUnknownKeys));

            if (unknown.Count > MaxReportedUnknownKeys)
            {
                shown += $", and {unknown.Count - MaxReportedUnknownKeys} more";
            }

            logger.LogWarning(
                "{Count} setting(s) under '{SectionName}' were not recognised and have been " +
                "ignored: {Keys}. Check the spelling against the template in " +
                "App_Data/Optimizely.Performance.Counters.",
                unknown.Count,
                InstrumentationOptions.SectionName,
                shown);
        }

        private static bool IsLeaf(Type type) =>
            type == typeof(bool) || type == typeof(int) || type == typeof(string);

        /// <remarks>
        /// JSON has no comments, so the settings templates annotate themselves with keys named
        /// <c>_comment</c>, the convention the rest of this repository's examples already use. They
        /// are dropped here rather than reported as unrecognised, which is the whole point of
        /// keeping them: an operator who copied the template with its explanations intact should
        /// not be warned about the explanations.
        /// </remarks>
        private static bool IsComment(string key) =>
            key.Length > 0
            && (key[0] == '_' || key.IndexOf(":_", StringComparison.Ordinal) >= 0);
    }
}
