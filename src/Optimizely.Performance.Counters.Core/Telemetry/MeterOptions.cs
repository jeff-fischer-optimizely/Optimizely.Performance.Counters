namespace Optimizely.Performance.Counters.Core.Telemetry
{
    /// <summary>
    /// Options for publishing the counters to a <see cref="System.Diagnostics.Metrics.Meter"/>
    /// alongside the EventSource.
    /// </summary>
    /// <remarks>
    /// Declared in Core beside the tracker rather than in Configuration, matching
    /// <c>LogWriteRateOptions</c> and <c>HttpCacheabilityOptions</c>: the option type lives with the
    /// thing it configures, and <c>InstrumentationOptions</c> only assembles them into one section.
    /// </remarks>
    public class MeterOptions
    {
        /// <summary>
        /// Gets or sets whether the counters are also published to a meter named
        /// <see cref="CounterNames.MeterName"/>. Ignored on .NET Framework, which has no meters.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Default true, unlike most of the switches in this tree, because the failure it prevents is
        /// silent. A site running Application Insights SDK 3.x, <c>UseAzureMonitor()</c>, or plain
        /// OpenTelemetry has nothing that collects EventCounters at all - there is no
        /// <c>EventCounterCollectionModule</c> in the 3.x SDK and no 3.x of the collector package -
        /// so with the meter off, every counter in this package is published to nobody and nothing
        /// says so. Defaulting on means the counters arrive wherever the host is already looking,
        /// and the operator finds out that the EventSource path is dead by having not noticed.
        /// </para>
        /// <para>
        /// It costs an unsubscribed <c>Record</c> per measurement - a virtual call that returns on a
        /// disabled check - so leaving it on when nothing collects meters is not something a site
        /// will measure. Turn it off to be certain, or to keep a single publication path while
        /// diagnosing a discrepancy between the two.
        /// </para>
        /// </remarks>
        public bool Enabled { get; set; } = true;
    }
}
