using EPiServer.Commerce.Order;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using ViaBill.Commerce.Constants;

namespace Foundation.Features.Checkout
{
    [ApiController]
    [Route("api/viabill")]
    public class ViaBillOrderApiController : ControllerBase
    {
        private readonly IOrderRepository _orderRepository;

        public ViaBillOrderApiController(IOrderRepository orderRepository)
        {
            _orderRepository = orderRepository;
        }

        [HttpGet("getordernumber/{groupOrderId:int}")]
        public IActionResult GetOrderNumber(int groupOrderId)
        {
            try
            {
                // IOrderRepository.Load<T>(int) loads by OrderGroupId directly
                var order = _orderRepository.Load<IPurchaseOrder>(groupOrderId);

                if (order == null)
                    return NotFound(new { error = "Order not found", id = groupOrderId });

                var paymentNames = order.Forms
                    .SelectMany(f => f.Payments)
                    .Select(p => p.PaymentMethodName)
                    .ToList();

                var isViaBill = paymentNames.Any(name =>
                    name.Equals(ViaBillConstants.SystemKeyword,
                                StringComparison.OrdinalIgnoreCase));

                if (!isViaBill)
                    return NotFound(new
                    {
                        error = "Not a ViaBill order",
                        paymentMethods = paymentNames
                    });

                return Ok(new { orderNumber = order.OrderNumber });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, type = ex.GetType().Name });
            }
        }
    }
}