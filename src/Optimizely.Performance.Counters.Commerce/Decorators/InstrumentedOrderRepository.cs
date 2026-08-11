using System;
using System.Collections.Generic;
using System.Linq;
using EPiServer.Commerce.Order;
using Optimizely.Performance.Counters.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Names = Optimizely.Performance.Counters.Core.Telemetry.CounterNames.CommerceOrders;
#if COMMERCE15
using System.Threading;
using System.Threading.Tasks;
#endif

namespace Optimizely.Performance.Counters.Commerce.Decorators
{
    /// <summary>
    /// Decorator for IOrderRepository that instruments cart and order operations.
    /// Tracks: save/load/create/delete duration and rate, plus cart size and value.
    /// <para>
    /// The interface shape differs per Commerce major:
    /// Commerce 13 (V11) adds a non-generic <c>Load(Guid, string)</c>;
    /// Commerce 15 (V13) adds an async counterpart for every member.
    /// The eight synchronous members below are common to all three.
    /// </para>
    /// </summary>
    public class InstrumentedOrderRepository : IOrderRepository
    {
        private readonly IOrderRepository _inner;
        private readonly IMetricTracker _metricTracker;
        private readonly ILogger<InstrumentedOrderRepository> _logger;

        /// <summary>
        /// Wraps the repository resolved by the container.
        /// </summary>
        /// <param name="inner">The implementation being decorated.</param>
        /// <param name="metricTracker">Sink for the emitted counters.</param>
        /// <param name="logger">Used only to report instrumentation failures; never to fail the caller.</param>
        public InstrumentedOrderRepository(
            IOrderRepository inner,
            IMetricTracker metricTracker,
            ILogger<InstrumentedOrderRepository> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _metricTracker = metricTracker ?? throw new ArgumentNullException(nameof(metricTracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Synchronous members (Commerce 13, 14 and 15)

        /// <inheritdoc />
        public TOrderGroup Create<TOrderGroup>(Guid customerId, string name)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Create<TOrderGroup>(customerId, name);
                TrackOperation(typeof(TOrderGroup).Name, Names.Create, timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Create, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IOrderGroup Load(OrderReference orderLink)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Load(orderLink);
                TrackOperation(OrderTypeOf(result), Names.Load, timer.ElapsedMilliseconds, success: result != null);
                TrackCartMetrics(result as ICart);
                // A load that finds nothing returns null; the interface is not annotated for it.
                return result!;
            }
            catch
            {
                TrackOperation("OrderGroup", Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public TOrderGroup Load<TOrderGroup>(int orderGroupId)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Load<TOrderGroup>(orderGroupId);
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: result != null);
                TrackCartMetrics(result as ICart);
                // A load that finds nothing returns null; the interface is not annotated for it.
                return result!;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TOrderGroup> Load<TOrderGroup>(Guid customerId, string name)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                // Materialized so the measured duration covers the actual work rather than
                // just the construction of a deferred query, and so the count is available.
                var result = _inner.Load<TOrderGroup>(customerId, name)?.ToList()
                             ?? new List<TOrderGroup>();

                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: true);
                TrackMetric(Names.CartsLoaded, result.Count);
                return result;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public OrderReference Save(IOrderGroup order)
        {
            var timer = OperationTimer.Start();
            var orderType = OrderTypeOf(order);
            try
            {
                var result = _inner.Save(order);
                TrackOperation(orderType, Names.Save, timer.ElapsedMilliseconds, success: true);
                TrackCartMetrics(order as ICart);
                return result;
            }
            catch
            {
                TrackOperation(orderType, Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public OrderReference SaveAsPaymentPlan(IOrderGroup cart)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.SaveAsPaymentPlan(cart);
                TrackOperation("PaymentPlan", Names.Save, timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackOperation("PaymentPlan", Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public OrderReference SaveAsPurchaseOrder(IOrderGroup cart)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.SaveAsPurchaseOrder(cart);
                TrackOperation("PurchaseOrder", Names.Save, timer.ElapsedMilliseconds, success: true);
                TrackCartMetrics(cart as ICart);
                return result;
            }
            catch
            {
                TrackOperation("PurchaseOrder", Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public void Delete(OrderReference orderLink)
        {
            var timer = OperationTimer.Start();
            try
            {
                _inner.Delete(orderLink);
                TrackOperation("OrderGroup", Names.Delete, timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackOperation("OrderGroup", Names.Delete, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion

#if COMMERCE13
        #region Commerce 13 only

        /// <inheritdoc />
        // Obsolete on the interface since Commerce 13, but still a member we must implement.
        // Marking our override obsolete too is what suppresses CS0618 on the inner call.
        [Obsolete("Mirrors the obsolete IOrderRepository.Load(Guid, string) member.")]
        public IEnumerable<IOrderGroup> Load(Guid customerId, string name)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = _inner.Load(customerId, name)?.ToList() ?? new List<IOrderGroup>();

                TrackOperation("OrderGroup", Names.Load, timer.ElapsedMilliseconds, success: true);
                TrackMetric(Names.CartsLoaded, result.Count);
                return result;
            }
            catch
            {
                TrackOperation("OrderGroup", Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion
#endif

#if COMMERCE15
        #region Asynchronous members (Commerce 15 only)

        /// <inheritdoc />
        public async Task<TOrderGroup> CreateAsync<TOrderGroup>(Guid customerId, string name, CancellationToken cancellationToken)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = await _inner.CreateAsync<TOrderGroup>(customerId, name, cancellationToken).ConfigureAwait(false);
                TrackOperation(typeof(TOrderGroup).Name, Names.Create, timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Create, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IOrderGroup> LoadAsync(OrderReference orderLink, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = await _inner.LoadAsync(orderLink, cancellationToken).ConfigureAwait(false);
                TrackOperation(OrderTypeOf(result), Names.Load, timer.ElapsedMilliseconds, success: result != null);
                TrackCartMetrics(result as ICart);
                // A load that finds nothing returns null; the interface is not annotated for it.
                return result!;
            }
            catch
            {
                TrackOperation("OrderGroup", Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<TOrderGroup> LoadAsync<TOrderGroup>(int orderGroupId, CancellationToken cancellationToken)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = await _inner.LoadAsync<TOrderGroup>(orderGroupId, cancellationToken).ConfigureAwait(false);
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: result != null);
                TrackCartMetrics(result as ICart);
                // A load that finds nothing returns null; the interface is not annotated for it.
                return result!;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<TOrderGroup>> LoadAsync<TOrderGroup>(Guid customerId, string name, CancellationToken cancellationToken)
            where TOrderGroup : class, IOrderGroup
        {
            var timer = OperationTimer.Start();
            try
            {
                var loaded = await _inner.LoadAsync<TOrderGroup>(customerId, name, cancellationToken).ConfigureAwait(false);
                var result = loaded?.ToList() ?? new List<TOrderGroup>();

                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: true);
                TrackMetric(Names.CartsLoaded, result.Count);
                return result;
            }
            catch
            {
                TrackOperation(typeof(TOrderGroup).Name, Names.Load, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<OrderReference> SaveAsync(IOrderGroup order, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            var orderType = OrderTypeOf(order);
            try
            {
                var result = await _inner.SaveAsync(order, cancellationToken).ConfigureAwait(false);
                TrackOperation(orderType, Names.Save, timer.ElapsedMilliseconds, success: true);
                TrackCartMetrics(order as ICart);
                return result;
            }
            catch
            {
                TrackOperation(orderType, Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<OrderReference> SaveAsPaymentPlanAsync(IOrderGroup cart, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = await _inner.SaveAsPaymentPlanAsync(cart, cancellationToken).ConfigureAwait(false);
                TrackOperation("PaymentPlan", Names.Save, timer.ElapsedMilliseconds, success: true);
                return result;
            }
            catch
            {
                TrackOperation("PaymentPlan", Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<OrderReference> SaveAsPurchaseOrderAsync(IOrderGroup cart, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            try
            {
                var result = await _inner.SaveAsPurchaseOrderAsync(cart, cancellationToken).ConfigureAwait(false);
                TrackOperation("PurchaseOrder", Names.Save, timer.ElapsedMilliseconds, success: true);
                TrackCartMetrics(cart as ICart);
                return result;
            }
            catch
            {
                TrackOperation("PurchaseOrder", Names.Save, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(OrderReference orderLink, CancellationToken cancellationToken)
        {
            var timer = OperationTimer.Start();
            try
            {
                await _inner.DeleteAsync(orderLink, cancellationToken).ConfigureAwait(false);
                TrackOperation("OrderGroup", Names.Delete, timer.ElapsedMilliseconds, success: true);
            }
            catch
            {
                TrackOperation("OrderGroup", Names.Delete, timer.ElapsedMilliseconds, success: false);
                throw;
            }
        }

        #endregion
#endif

        #region Instrumentation helpers

        /// <summary>
        /// Classifies an order group for the OrderType dimension. Falls back to "OrderGroup"
        /// so a null or unrecognised implementation still produces a usable series.
        /// </summary>
        private static string OrderTypeOf(IOrderGroup order)
        {
            if (order is IPurchaseOrder) return "PurchaseOrder";
            if (order is IPaymentPlan) return "PaymentPlan";
            if (order is ICart) return "Cart";
            return "OrderGroup";
        }

        private void TrackOperation(string orderType, CounterPair counter, double durationMs, bool success)
        {
            try
            {
                // Track operation duration
                _metricTracker.TrackMetric(
                    counter.TimeMs,
                    durationMs,
                    "OrderType", orderType);

                // Track operation rate
                _metricTracker.TrackMetric(
                    counter.Operations,
                    1,
                    "OrderType", orderType,
                    "Success", success ? "True" : "False");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to track {Counter} for {OrderType}", counter.Operations, orderType);
            }
        }

        private void TrackMetric(string name, double value)
        {
            try
            {
                _metricTracker.TrackMetric(name, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to track {Metric}", name);
            }
        }

        /// <summary>
        /// Emits cart size and value. No-op for anything that is not a cart, so callers can
        /// pass the result of an <c>as ICart</c> without a null check of their own.
        /// <para>
        /// Gated on <see cref="IMetricTracker.IsEnabled"/>, unlike every other track method here.
        /// Both values below cost real work to obtain rather than merely being handed to us, and
        /// this runs on eight call sites covering every load, save, create and delete - so with no
        /// collector attached that work would be pure waste on the caller's thread.
        /// </para>
        /// </summary>
        private void TrackCartMetrics(ICart? cart)
        {
            if (cart == null || !_metricTracker.IsEnabled)
            {
                return;
            }

            try
            {
                _metricTracker.TrackMetric(
                    Names.CartLineItemCount,
                    CountLineItems(cart));

                // GetTotal() is not an accessor. It resolves IOrderGroupCalculator and runs the
                // whole subtotal-shipping-handling-tax pipeline, which on a site with an external
                // tax provider leaves the process. It is the price of the counter and there is no
                // cheaper way to the number, which is why the IsEnabled gate above exists.
                //
                // Money is a struct - GetTotal() always returns a value, never null.
                var total = cart.GetTotal();
                _metricTracker.TrackMetric(
                    Names.CartTotal,
                    (double)total.Amount,
                    "Currency", cart.Currency.CurrencyCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to track cart metrics");
            }
        }

        /// <summary>
        /// Counts line items by summing the collection counts rather than calling
        /// <c>GetAllLineItems().Count()</c>, which chains two SelectMany iterators and walks every
        /// line item to arrive at the same number.
        /// </summary>
        private static int CountLineItems(IOrderGroup order)
        {
            var count = 0;
            foreach (var form in order.Forms)
            {
                foreach (var shipment in form.Shipments)
                {
                    count += shipment.LineItems.Count;
                }
            }

            return count;
        }

        #endregion
    }
}
