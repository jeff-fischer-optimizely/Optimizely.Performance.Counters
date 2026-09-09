#if NET472
using System;
using System.IO;
using System.Linq;
using System.Xml;
using log4net;
using log4net.Config;
using log4net.Repository;
using Optimizely.Performance.Counters.Core.Diagnostics;
using Optimizely.Performance.Counters.Core.Telemetry;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Diagnostics
{
    /// <summary>
    /// The V11 sink, against a real log4net.
    /// <para>
    /// V11 has no <c>ILoggerProvider</c> to register, so the write rate is counted by an appender
    /// added to the root logger of every configured repository. Everything about that is reflective
    /// - the appender is a <c>DispatchProxy</c> over <c>log4net.Appender.IAppender</c>, because Core
    /// deliberately holds no compile-time reference to log4net - so a real log4net is the only thing
    /// that can tell whether it still binds. See the note on the package reference in the test
    /// project for why the reference lives here and not in Core.
    /// </para>
    /// <para>
    /// The two configurations a V11 site actually ships are covered separately, because they reach
    /// log4net by different entry points: a standalone <c>EPiServerLog.config</c> loaded from a
    /// file, and an inline <c>&lt;log4net&gt;</c> section in <c>web.config</c> handed over as an
    /// XML element by log4net's own configuration section handler.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Every test builds its own named repository rather than configuring the default one. log4net
    /// has no way to remove a repository once created, so a test that configured the default would
    /// leave the whole assembly's logging rearranged behind it.
    /// </remarks>
    [Collection(LogWriteRateCollection.Name)]
    public class Log4NetWriteRateSinkTests : IDisposable
    {
        private const string RootOnlyConfiguration =
            "<log4net><root><level value=\"ALL\" /></root></log4net>";

        public void Dispose() => LogWriteRateMonitor.Stop();

        [Fact]
        public void Writes_are_counted_when_log4net_was_configured_from_a_separate_file()
        {
            // The EPiServerLog.config case: a standalone file, loaded by XmlConfigurator, which is
            // what EPiServer.Logging.Log4Net does on a stock V11 site.
            var repository = LogManager.CreateRepository(UniqueName());
            var file = WriteTemporaryConfiguration(RootOnlyConfiguration);

            try
            {
                XmlConfigurator.Configure(repository, new FileInfo(file));

                var tracker = Count(repository);

                Assert.Equal(Rate(3), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
                Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WarningsPerSecond));
                Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.ErrorsPerSecond));
            }
            finally
            {
                TryDelete(file);
            }
        }

        [Fact]
        public void Writes_are_counted_when_log4net_was_configured_from_an_inline_section()
        {
            // The web.config case. log4net's ConfigurationSectionHandler hands the section over as
            // an XmlElement and XmlConfigurator takes it from there, so an element parsed here is
            // the same input the site's own configuration produces.
            var repository = LogManager.CreateRepository(UniqueName());

            var document = new XmlDocument();
            document.LoadXml(RootOnlyConfiguration);

            XmlConfigurator.Configure(repository, document.DocumentElement!);

            var tracker = Count(repository);

            Assert.Equal(Rate(3), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.WarningsPerSecond));
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.ErrorsPerSecond));
        }

        [Fact]
        public void Fatal_counts_as_an_error_and_debug_does_not()
        {
            // The severity split is made on log4net's numeric level rather than on the name, so a
            // custom level - which a site can and does declare - lands on the right side of the
            // line instead of falling through to 'normal'.
            var repository = LogManager.CreateRepository(UniqueName());
            XmlConfigurator.Configure(repository, Element(RootOnlyConfiguration));

            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);

            var log = LogManager.GetLogger(repository.Name, "Test.Logger");
            log.Debug("quiet");
            log.Fatal("loud");

            Flush();

            Assert.Equal(Rate(2), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
            Assert.Equal(0d, ValueOf(tracker, CounterNames.Runtime.Logging.WarningsPerSecond));
            Assert.Equal(Rate(1), ValueOf(tracker, CounterNames.Runtime.Logging.ErrorsPerSecond));
        }

        [Fact]
        public void Every_configured_repository_is_counted()
        {
            // Why the sink enumerates repositories rather than taking the default one. A V11 site
            // can end up with more than one - EPiServer configures its own, and so does anything
            // else in the process that calls CreateRepository - and a write rate that covered only
            // one of them would read low by however much the others were logging.
            var first = LogManager.CreateRepository(UniqueName());
            var second = LogManager.CreateRepository(UniqueName());

            XmlConfigurator.Configure(first, Element(RootOnlyConfiguration));
            XmlConfigurator.Configure(second, Element(RootOnlyConfiguration));

            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);

            LogManager.GetLogger(first.Name, "Test.Logger").Info("one");
            LogManager.GetLogger(second.Name, "Test.Logger").Info("two");

            Flush();

            Assert.Equal(Rate(2), ValueOf(tracker, CounterNames.Runtime.Logging.WritesPerSecond));
        }

        [Fact]
        public void Nothing_is_counted_once_the_monitor_has_stopped()
        {
            // Stop has to detach the appender, not merely drop the recorder. An appender left on
            // the root logger of a repository nobody owns any more is a leak that survives an
            // Uninitialize, and on a site that restarts its initialization - which V11 does - it
            // would accumulate one per cycle.
            var repository = LogManager.CreateRepository(UniqueName());
            XmlConfigurator.Configure(repository, Element(RootOnlyConfiguration));

            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);
            LogWriteRateMonitor.Stop();

            LogManager.GetLogger(repository.Name, "Test.Logger").Error("after shutdown");

            Assert.Empty(tracker.Metrics);
        }

        /// <summary>
        /// Starts the monitor, writes one of each severity through the repository, and flushes.
        /// </summary>
        /// <remarks>
        /// The monitor is started after the repository is configured, which is the order a site
        /// runs in: log4net is configured during application start, and the initialization module
        /// that starts this runs after it. It is also the documented limitation - a repository
        /// created later is not seen - so a helper that got the order the other way round would
        /// test something no site does.
        /// </remarks>
        private static RecordingMetricTracker Count(ILoggerRepository repository)
        {
            var tracker = new RecordingMetricTracker();
            LogWriteRateMonitor.Start(tracker);

            var log = LogManager.GetLogger(repository.Name, "Test.Logger");
            log.Info("ordinary");
            log.Warn("a warning");
            log.Error("an error");

            Flush();

            return tracker;
        }

        private static void Flush() => MetricFlush.Run(LogWriteRateMonitor.Current!);

        private static XmlElement Element(string xml)
        {
            var document = new XmlDocument();
            document.LoadXml(xml);
            return document.DocumentElement!;
        }

        private static string WriteTemporaryConfiguration(string xml)
        {
            var path = Path.Combine(Path.GetTempPath(), UniqueName() + ".config");
            File.WriteAllText(path, xml);
            return path;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover file in the temp directory is not worth failing a passing test over.
            }
        }

        // Unique per test, because log4net keeps every repository it is ever asked to create for
        // the life of the process and creating one twice under the same name throws.
        private static string UniqueName() =>
            "OptiCountersLogWriteRate-" + Guid.NewGuid().ToString("N");

        private static double Rate(int writes) => LogWriteRateTests.Rate(writes);

        private static double? ValueOf(RecordingMetricTracker tracker, string name) =>
            LogWriteRateTests.ValueOf(tracker, name);
    }
}
#endif
