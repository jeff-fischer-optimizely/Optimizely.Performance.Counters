using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Optimizely.Performance.Counters.Core.Deployment
{
    /// <summary>
    /// Where the deployment events go.
    /// </summary>
    /// <remarks>
    /// An interface rather than a direct call into Application Insights, for the same reason the
    /// decorators take an <c>IMetricTracker</c>: the tests need to read what was emitted, and
    /// asserting against a recorder is the only way to prove the property names and values are what
    /// the documented queries expect.
    /// </remarks>
    public interface IDeploymentEventSink
    {
        /// <summary>
        /// Records one event.
        /// </summary>
        /// <param name="eventName">The event name, from <see cref="DeploymentEventNames"/>.</param>
        /// <param name="properties">The dimensions, which become <c>customDimensions</c>.</param>
        void Track(string eventName, IDictionary<string, string> properties);
    }

    /// <summary>
    /// The event names, in one place because they are the query surface.
    /// </summary>
    /// <remarks>
    /// Renaming one of these breaks every saved query and alert built on it, which is a different
    /// class of change from renaming a private method - so they are named once here and never
    /// spelled out at a call site.
    /// </remarks>
    public static class DeploymentEventNames
    {
        /// <summary>Prefix shared by every event, so one query can find all of them.</summary>
        public const string Prefix = "OptiCounters.";

        /// <summary>
        /// One row per process start and per heartbeat, carrying the fingerprint. The tier nothing
        /// else depends on.
        /// </summary>
        public const string Manifest = Prefix + "DeploymentManifest";

        /// <summary>One row per assembly, on change or on the slow re-emit.</summary>
        public const string Inventory = Prefix + "AssemblyInventory";

        /// <summary>One row per assembly that differs from the previously recorded manifest.</summary>
        public const string Changed = Prefix + "AssemblyChanged";

        /// <summary>
        /// One row when this instance can see that a peer is running a different fingerprint.
        /// </summary>
        public const string Divergence = Prefix + "FleetDivergence";
    }

    /// <summary>
    /// The sink used when Application Insights is not there.
    /// </summary>
    /// <remarks>
    /// Not a null object. A site running without Application Insights - a V11 site logging through
    /// log4net, or anything self-hosted - still deploys, and the operator still wants to know what
    /// is in the bin folder. Writing the events to the log means the answer exists wherever the
    /// site's logs go, which is the same reasoning behind the large-cascade log line.
    /// </remarks>
    public sealed class LoggerDeploymentEventSink : IDeploymentEventSink
    {
        private readonly ILogger? _logger;

        /// <summary>Creates the sink.</summary>
        /// <param name="logger">Log sink; may be null, in which case nothing is written anywhere.</param>
        public LoggerDeploymentEventSink(ILogger? logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public void Track(string eventName, IDictionary<string, string> properties)
        {
            if (_logger == null)
            {
                return;
            }

            // The inventory is one line per assembly and would swamp a log that the manifest and
            // change lines are useful in, so it goes to Debug while the rest go to Information.
            var level = eventName == DeploymentEventNames.Inventory
                ? LogLevel.Debug
                : LogLevel.Information;

            _logger.Log(
                level,
                "{EventName} {Properties}",
                eventName,
                string.Join(
                    " ",
                    properties
                        .OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
                        .Select(pair => pair.Key + "=" + pair.Value)));
        }
    }
}
