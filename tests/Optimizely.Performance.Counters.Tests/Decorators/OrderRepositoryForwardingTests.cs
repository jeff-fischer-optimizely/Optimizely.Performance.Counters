using System;
using System.Linq;
using EPiServer.Commerce.Order;
using Microsoft.Extensions.Logging.Abstractions;
using Optimizely.Performance.Counters.Commerce.Decorators;
using Optimizely.Performance.Counters.Tests.Infrastructure;
using Xunit;

namespace Optimizely.Performance.Counters.Tests.Decorators
{
    public class OrderRepositoryForwardingTests
    {
        private static (InstrumentedOrderRepository Decorator, RecordingProxy Recorder, RecordingMetricTracker Tracker) Build()
        {
            var (inner, recorder) = RecordingProxy.Create<IOrderRepository>();
            var tracker = new RecordingMetricTracker();
            var decorator = new InstrumentedOrderRepository(
                inner, tracker, NullLogger<InstrumentedOrderRepository>.Instance);

            return (decorator, recorder, tracker);
        }

        [Fact]
        public void Every_IOrderRepository_member_reaches_the_wrapped_repository()
        {
            var (decorator, recorder, _) = Build();

            ForwardingAssert.AllForwarded(ForwardingSweep.Run(decorator, typeof(IOrderRepository), recorder), atLeast: 8);
        }

        [Fact]
        public void Every_IOrderRepository_member_emits_a_duration_and_a_rate()
        {
            var (decorator, recorder, tracker) = Build();

            var results = ForwardingSweep.Run(decorator, typeof(IOrderRepository), recorder);

            // Every member emits a TimeMs and an Operations counter. Anything less means a member
            // was forwarded but left uninstrumented.
            Assert.True(
                tracker.Metrics.Count >= results.Count * 2,
                $"{results.Count} members produced only {tracker.Metrics.Count} metrics.");
        }

        [Fact]
        public void A_save_is_filed_under_the_order_type_it_was_given()
        {
            var (decorator, _, tracker) = Build();

            // A null order is not a real scenario, but it is the one shape available without a
            // Commerce database. It exercises the OrderTypeOf fallback, which is what keeps the
            // OrderType dimension from ever being empty.
            decorator.Save(order: null!);

            var operations = tracker.Metrics.Single(m => m.Name == "Optimizely.Commerce.Orders.SaveOperations");
            Assert.Equal("OrderGroup", operations.Dimensions["OrderType"]);
            Assert.Equal("True", operations.Dimensions["Success"]);
        }

        [Fact]
        public void SaveAsPurchaseOrder_and_SaveAsPaymentPlan_share_the_Save_counter_but_not_the_dimension()
        {
            var (decorator, _, tracker) = Build();

            decorator.SaveAsPurchaseOrder(cart: null!);
            decorator.SaveAsPaymentPlan(cart: null!);

            var orderTypes = tracker.Metrics
                .Where(m => m.Name == "Optimizely.Commerce.Orders.SaveOperations")
                .Select(m => m.Dimensions["OrderType"])
                .ToList();

            Assert.Equal(new[] { "PurchaseOrder", "PaymentPlan" }, orderTypes);
        }

        [Fact]
        public void A_batch_load_reports_how_many_carts_it_returned()
        {
            var (decorator, _, tracker) = Build();

            // The recording proxy returns null, which the decorator normalises to an empty list
            // rather than letting the count throw.
            var loaded = decorator.Load<ICart>(Guid.Empty, "Default");

            Assert.Empty(loaded);
            Assert.Equal(0.0, tracker.Metrics.Single(m => m.Name == "Optimizely.Commerce.Orders.CartsLoaded").Value);
        }

        [Fact]
        public void A_failed_save_is_counted_as_a_failure_and_the_exception_still_reaches_the_caller()
        {
            var tracker = new RecordingMetricTracker();
            var decorator = new InstrumentedOrderRepository(
                ThrowingProxy.Create<IOrderRepository>(),
                tracker,
                NullLogger<InstrumentedOrderRepository>.Instance);

            Assert.Throws<InnerFailureException>(() => decorator.Save(order: null!));

            var operations = tracker.Metrics.Single(m => m.Name == "Optimizely.Commerce.Orders.SaveOperations");
            Assert.Equal("False", operations.Dimensions["Success"]);
        }
    }
}
