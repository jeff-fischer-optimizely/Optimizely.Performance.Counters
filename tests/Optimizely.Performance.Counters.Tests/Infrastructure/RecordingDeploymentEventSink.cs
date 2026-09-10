using System;
using System.Collections.Generic;
using System.Linq;
using Optimizely.Performance.Counters.Core.Deployment;

namespace Optimizely.Performance.Counters.Tests.Infrastructure
{
    /// <summary>
    /// An <see cref="IDeploymentEventSink"/> that keeps what it was given.
    /// <para>
    /// The event names and the dimension names are the query surface: every saved query, workbook
    /// and alert built on this feature spells them out, so renaming one silently is a different
    /// class of change from renaming a private method. Asserting against a recorder is the only way
    /// to hold them still.
    /// </para>
    /// </summary>
    internal sealed class RecordingDeploymentEventSink : IDeploymentEventSink
    {
        internal List<Recorded> Events { get; } = new List<Recorded>();

        public void Track(string eventName, IDictionary<string, string> properties) =>
            Events.Add(new Recorded(
                eventName,
                new Dictionary<string, string>(properties, StringComparer.Ordinal)));

        /// <summary>Every event of one name, in the order it was emitted.</summary>
        internal IReadOnlyList<Recorded> Named(string eventName) =>
            Events.Where(recorded => recorded.Name == eventName).ToList();

        /// <summary>The single event of one name, failing the assertion if there is not exactly one.</summary>
        internal Recorded Single(string eventName) => Named(eventName).Single();

        /// <summary>Forgets everything, so a second capture can be asserted on by itself.</summary>
        internal void Clear() => Events.Clear();

        internal sealed class Recorded
        {
            internal Recorded(string name, IReadOnlyDictionary<string, string> properties)
            {
                Name = name;
                Properties = properties;
            }

            internal string Name { get; }

            internal IReadOnlyDictionary<string, string> Properties { get; }

            /// <summary>The value of one dimension, or null if it was not written.</summary>
            internal string? this[string key] =>
                Properties.TryGetValue(key, out var value) ? value : null;

            public override string ToString() =>
                Name + " " + string.Join(
                    " ",
                    Properties
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => pair.Key + "=" + pair.Value));
        }
    }
}
