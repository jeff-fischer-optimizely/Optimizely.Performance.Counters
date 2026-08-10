using System.Threading;
using Optimizely.Performance.Counters.Core.Telemetry;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Telemetry
{
    public class OperationTimerTests
    {
        [Fact]
        public void The_timer_does_not_allocate()
        {
            // The whole point of not using Stopwatch is that Stopwatch is a class, so StartNew
            // heap-allocates on a path that runs thousands of times a second.
            Assert.True(typeof(OperationTimer).IsValueType);
        }

        [Fact]
        public void Elapsed_time_moves_forward()
        {
            var timer = OperationTimer.Start();
            Thread.Sleep(20);
            var first = timer.ElapsedMilliseconds;

            Assert.True(first >= 10.0, $"Expected at least 10ms after a 20ms sleep, got {first}ms.");

            Thread.Sleep(20);

            // Reading is non-destructive: a decorator reads the same timer in both its success and
            // failure paths, and the value must keep growing rather than reset or freeze.
            Assert.True(timer.ElapsedMilliseconds > first);
        }

        [Fact]
        public void A_timer_read_immediately_is_not_negative()
        {
            Assert.True(OperationTimer.Start().ElapsedMilliseconds >= 0.0);
        }
    }
}
