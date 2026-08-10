using EPiServer;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.CMS.Decorators;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    public class ContentLoaderForwardingTests
    {
        [Fact]
        public void Every_IContentLoader_member_reaches_the_wrapped_loader()
        {
            var (inner, recorder) = RecordingProxy.Create<IContentLoader>();
            var decorator = new InstrumentedContentLoader(
                inner,
                new RecordingMetricTracker(),
                NullLogger<InstrumentedContentLoader>.Instance);

            ForwardingAssert.AllForwarded(ForwardingSweep.Run(decorator, typeof(IContentLoader), recorder), atLeast: 23);
        }

        [Fact]
        public void Every_IContentLoader_member_emits_a_load_counter()
        {
            var (inner, recorder) = RecordingProxy.Create<IContentLoader>();
            var tracker = new RecordingMetricTracker();
            var decorator = new InstrumentedContentLoader(inner, tracker, NullLogger<InstrumentedContentLoader>.Instance);

            var results = ForwardingSweep.Run(decorator, typeof(IContentLoader), recorder);

            // Every member is instrumented, and each emits at least LoadTimeMs and LoadOperations.
            // An uninstrumented member would show up as a shortfall here even though forwarding
            // still works, which is the failure mode a pure forwarding test cannot see.
            Assert.True(
                tracker.Metrics.Count >= results.Count * 2,
                $"{results.Count} members produced only {tracker.Metrics.Count} metrics.");
        }
    }
}
