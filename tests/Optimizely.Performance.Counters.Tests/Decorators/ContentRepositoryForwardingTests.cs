using System.Linq;
using EPiServer;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    public class ContentRepositoryForwardingTests
    {
        private static (InstrumentedContentRepository Decorator, RecordingProxy Recorder, RecordingMetricTracker Tracker) Build()
        {
            var (inner, recorder) = RecordingProxy.Create<IContentRepository>();
            var tracker = new RecordingMetricTracker();
            var decorator = new InstrumentedContentRepository(
                inner,
                tracker,
                NullLogger<InstrumentedContentRepository>.Instance);

            return (decorator, recorder, tracker);
        }

        [Fact]
        public void Every_IContentRepository_member_reaches_the_wrapped_repository()
        {
            var (decorator, recorder, _) = Build();

            // The sweep walks base interfaces too, so this also re-checks the 23 IContentLoader
            // members the repository decorator inherits rather than restates.
            ForwardingAssert.AllForwarded(ForwardingSweep.Run(decorator, typeof(IContentRepository), recorder), atLeast: 38);
        }

        [Fact]
        public void Save_with_the_Publish_action_is_counted_as_a_publish()
        {
            var (decorator, _, tracker) = Build();

            decorator.Save(content: null!, SaveAction.Publish, AccessLevel.NoAccess);

            Assert.Contains("Optimizely.CMS.Content.PublishTimeMs", tracker.Names);
            Assert.DoesNotContain("Optimizely.CMS.Content.SaveTimeMs", tracker.Names);
        }

        [Fact]
        public void Save_with_Publish_combined_with_a_modifier_is_still_counted_as_a_publish()
        {
            var (decorator, _, tracker) = Build();

            // SaveAction is a flags enum and callers routinely OR in modifiers. Comparing for
            // equality instead of masking would silently misfile these as plain saves.
            decorator.Save(content: null!, SaveAction.Publish | SaveAction.ForceCurrentVersion, AccessLevel.NoAccess);

            Assert.Contains("Optimizely.CMS.Content.PublishTimeMs", tracker.Names);
            Assert.DoesNotContain("Optimizely.CMS.Content.SaveTimeMs", tracker.Names);
        }

        [Fact]
        public void Save_without_the_Publish_action_is_counted_as_a_save()
        {
            var (decorator, _, tracker) = Build();

            decorator.Save(content: null!, SaveAction.CheckIn, AccessLevel.NoAccess);

            Assert.Contains("Optimizely.CMS.Content.SaveTimeMs", tracker.Names);
            Assert.DoesNotContain("Optimizely.CMS.Content.PublishTimeMs", tracker.Names);
        }

        [Fact]
        public void A_failed_save_is_counted_as_a_failure_and_the_exception_still_reaches_the_caller()
        {
            var tracker = new RecordingMetricTracker();
            var decorator = new InstrumentedContentRepository(
                ThrowingProxy.Create<IContentRepository>(),
                tracker,
                NullLogger<InstrumentedContentRepository>.Instance);

            Assert.Throws<InnerFailureException>(() =>
                decorator.Save(content: null!, SaveAction.Save, AccessLevel.NoAccess));

            var operations = tracker.Metrics.Single(m => m.Name == "Optimizely.CMS.Content.SaveOperations");
            Assert.Equal("False", operations.Dimensions["Success"]);
        }
    }
}
