using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using EPiServer.Commerce.Order;
using EPiServer.Logging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Mediachase.Commerce.Orders;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Models;
using ViaBill.Commerce.Services;
using Foundation.Features.Checkout;
using Mediachase.Commerce.Customers;
using System.Globalization;
using Foundation.Features.Settings;
using Foundation.Infrastructure.Cms.Settings;
using EPiServer.Web.Routing;
using System.Collections.Specialized;

namespace Foundation.Features.Checkout
{
    /// <summary>
    /// Handles the three external-facing URLs that ViaBill communicates with:
    ///   POST /viabill/callback  — Server-to-server webhook from ViaBill
    ///   GET  /viabill/success   — Browser redirect after successful payment
    ///   GET  /viabill/cancel    — Browser redirect after customer cancels
    /// </summary>
    [Route("viabill")]
    public class ViaBillCallbackController : Controller
    {        
        private readonly IOrderRepository _orderRepository;
        private readonly IOrderGroupFactory _orderGroupFactory;
        private readonly IPaymentProcessor _paymentProcessor;
        private readonly ViaBillApiService _apiService;
        private readonly ViaBillSettingsResolver _settingsResolver;
        private readonly IConfiguration _configuration;
        private readonly IViaBillSettingsService _settingsService;
        private readonly ISettingsService _referenceSettingsService;
        private readonly UrlResolver _urlResolver;
        private readonly CustomerContext _customerContext;
        private readonly ILogger _logger = LogManager.GetLogger(typeof(ViaBillCallbackController));

        public ViaBillCallbackController(
        IOrderRepository orderRepository,
        IOrderGroupFactory orderGroupFactory,
        IPaymentProcessor paymentProcessor,
        ViaBillApiService apiService,
        ViaBillSettingsResolver settingsResolver,
        IConfiguration configuration,
        IViaBillSettingsService settingsService,
        ISettingsService referenceSettingsService,
        UrlResolver urlResolver,
        CustomerContext customerContext)
        {
            _orderRepository = orderRepository;
            _orderGroupFactory = orderGroupFactory;
            _paymentProcessor = paymentProcessor;
            _apiService = apiService;
            _settingsResolver = settingsResolver;
            _configuration = configuration;
            _settingsService = settingsService;
            _referenceSettingsService = referenceSettingsService;
            _urlResolver = urlResolver;
            _customerContext = customerContext;
        }

        // ─────────────────────────────────────────────────────────────
        // POST /viabill/callback
        // Server-to-server webhook from ViaBill.
        // ─────────────────────────────────────────────────────────────
        [HttpPost("callback")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Callback()
        {            
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                rawBody = await reader.ReadToEndAsync();            

            _settingsService.LogDebug("[ViaBill] Callback received: " + rawBody);

            ViaBillCallbackPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ViaBillCallbackPayload>(
                    rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {                
                _logger.Warning("[ViaBill] Callback deserialization failed: " + ex.Message);
                return BadRequest("Invalid payload.");
            }

            if (payload == null || string.IsNullOrEmpty(payload.Transaction))
            {                
                _logger.Warning("[ViaBill] Callback received with null or empty transaction.");
                return BadRequest("Missing transaction reference.");
            }            

            var secret = _settingsResolver.GetSecret();            

            var isValid = ViaBillApiService.ValidateCallbackSignature(payload, secret);            
            if (!isValid)
            {
                _logger.Warning("[ViaBill] Callback signature validation FAILED for transaction '"
                    + payload.Transaction + "'.");
                return Ok("Signature mismatch — ignored.");
            }

            _settingsService.LogDebug("[ViaBill] Callback valid. Transaction: '" + payload.Transaction + "', Status: '" + payload.Status + "'");

            // ── Step 1: Try to find an already-converted purchase order ──────────            
            var purchaseOrder = FindPurchaseOrderByTransactionId(payload.Transaction);
            
            // ── Step 2: If not found, look for the cart and convert it ───────────
            if (purchaseOrder == null)
            {
                _settingsService.LogDebug("[ViaBill] No purchase order found; looking for cart with transaction '" + payload.Transaction + "'.");

                var cart = FindCartByTransactionId(payload.Transaction);             
                if (cart == null)
                {                    
                    _logger.Warning("[ViaBill] No cart or purchase order found for transaction '"
                        + payload.Transaction + "'. Returning 200 so ViaBill does not retry.");

                    return Ok("Order not found.");
                }

                _settingsService.LogDebug("[ViaBill] Found cart. Converting to purchase order.");

                purchaseOrder = ConvertCartToPurchaseOrder(cart);                
                if (purchaseOrder == null)
                {                    
                    _logger.Warning("[ViaBill] Cart-to-order conversion failed for transaction '"
                        + payload.Transaction + "'.");
                    
                    return Ok("Order conversion failed.");
                }
            }

            var payment = FindViaBillPayment(purchaseOrder);            
            if (payment == null)
            {
                _logger.Warning("[ViaBill] No ViaBill payment found on order for transaction '"
                    + payload.Transaction + "'.");
                return Ok("Payment not found.");
            }

            var apiKey = _settingsResolver.GetApiKey();
            var testMode = _settingsResolver.GetTestMode();
            var debugEnabled = _settingsResolver.GetDebugEnabled();

            switch (payload.Status?.ToUpperInvariant())
            {
                case ViaBillConstants.StatusApproved:
                    payment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusApproved;
                    // Mark as authorized — true for both auto-capture and authorize-only paths.
                    payment.Properties[ViaBillConstants.AuthorizedKey] = "true";

                    if (_settingsResolver.IsAuthorizeAndCapture())
                    {
                        var captureAmount = decimal.TryParse(
                            payload.Amount,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedAmount)
                            ? -Math.Abs(parsedAmount)
                            : 0m;

                        var captured = await _apiService.CaptureAsync(
                            apiKey, secret, payload.Transaction,
                            captureAmount, payload.Currency ?? string.Empty, testMode, debugEnabled);

                        if (captured)
                        {
                            payment.Status = PaymentStatus.Processed.ToString();
                            payment.Properties[ViaBillConstants.CapturedKey] = "true";
                            purchaseOrder.OrderStatus = OrderStatus.Completed;
                            _settingsService.LogDebug("[ViaBill] Payment approved and captured for transaction '" + payload.Transaction + "'. Order set to Completed.");
                        }
                        else
                        {
                            payment.Status = PaymentStatus.Pending.ToString();
                            _logger.Warning("[ViaBill] Payment approved but capture FAILED for transaction '"
                                + payload.Transaction + "'.");
                        }
                    }
                    else
                    {
                        // Authorize-only: funds are reserved but not yet captured.
                        // Use Pending as the standard status; AuthorizedKey=true distinguishes
                        // this from a genuinely pending/unknown payment.
                        payment.Status = PaymentStatus.Pending.ToString();
                        _settingsService.LogDebug("[ViaBill] Payment authorized (no auto-capture) for transaction '" + payload.Transaction + "'.");
                    }
                    break;

                case ViaBillConstants.StatusCancelled:
                    payment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusCancelled;
                    payment.Status = PaymentStatus.Failed.ToString();
                    purchaseOrder.OrderStatus = OrderStatus.Cancelled;
                    _settingsService.LogDebug("[ViaBill] Payment cancelled for transaction '" + payload.Transaction + "'.");
                    break;

                case ViaBillConstants.StatusPending:
                    payment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusPending;
                    payment.Status = PaymentStatus.Pending.ToString();
                    _settingsService.LogDebug("[ViaBill] Payment pending for transaction '" + payload.Transaction + "'.");
                    break;

                case ViaBillConstants.StatusRejected:
                    payment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusRejected;
                    payment.Status = PaymentStatus.Failed.ToString();
                    purchaseOrder.OrderStatus = OrderStatus.Cancelled;
                    _settingsService.LogDebug("[ViaBill] Payment rejected for transaction '" + payload.Transaction + "'.");
                    break;

                default:                    
                    _logger.Warning("[ViaBill] Unknown callback status '" + payload.Status + "'.");
                    break;
            }
            
            try
            {
                var metaObj2 = payment as Mediachase.MetaDataPlus.MetaObject;
                
                _orderRepository.Save(purchaseOrder);

                _settingsService.LogDebug("[ViaBill] Order saved after callback for transaction '" + payload.Transaction + "'.");                
            }
            catch (Exception ex)
            {                
                _logger.Error("[ViaBill] Failed to save order after callback: " + ex.Message, ex);
                return StatusCode(500, "Failed to save order.");
            }
            
            return Ok();
        }

        // ─────────────────────────────────────────────────────────────
        // GET/POST /viabill/success
        // Browser redirect after the customer approves payment on ViaBill.
        // ─────────────────────────────────────────────────────────────
        [HttpGet("success")]
        [HttpPost("success")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Success()
        {
            var (transaction, orderNumber) = await ResolveTransactionParams();           

            _settingsService.LogDebug($"[ViaBill] Success received. Transaction: '{transaction}', OrderNumber: '{orderNumber}'.");

            if (string.IsNullOrEmpty(transaction) && string.IsNullOrEmpty(orderNumber))
            {                
                return RedirectToCheckout("Payment was successful, but the transaction reference was lost.");                
            }

            var txId = transaction ?? orderNumber!;

            // Step 1: Check if the callback already ran and created a purchase order.
            var purchaseOrder = FindPurchaseOrderByTransactionId(txId);
            
            // Step 2: If not, the cart is still pending — convert it now.
            //         (The callback may arrive before or after the browser redirect.)
            if (purchaseOrder == null)
            {
                var cart = FindCartByTransactionId(txId);               
                if (cart != null)
                {
                    _settingsService.LogDebug($"[ViaBill] Success: converting cart to purchase order for transaction '{txId}'.");
                    purchaseOrder = ConvertCartToPurchaseOrder(cart);                    
                }
            }

            // Step 3: If we still have no order, show a holding page.
            //         The callback will complete the order in the background.
            if (purchaseOrder == null)
            {                                
                return RedirectToPaymentPending(txId);
            }

            // Step 4: Redirect to order confirmation.
            // We do NOT gate this on IsApproved() because the callback may not have
            // set CallbackStatusKey yet — the customer approved on ViaBill's page,
            // which is sufficient to show the confirmation. The callback will update
            // the payment status asynchronously.            
            return RedirectToOrderConfirmation(purchaseOrder);
        }

        // ─────────────────────────────────────────────────────────────
        // GET/POST /viabill/cancel
        // Browser redirect after the customer cancels on ViaBill.
        // ─────────────────────────────────────────────────────────────

        [HttpGet("cancel")]
        [HttpPost("cancel")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Cancel()
        {
            var (transaction, orderNumber) = await ResolveTransactionParams();

            _settingsService.LogDebug($"[ViaBill] Cancel received. Transaction: '{transaction}', OrderNumber: '{orderNumber}'.");

            if (string.IsNullOrEmpty(transaction) && string.IsNullOrEmpty(orderNumber))
            {                
                return RedirectToCheckout("Payment was cancelled.");
            }

            var txId = transaction ?? orderNumber!;

            // Step 1: Check if the callback already ran and created a purchase order.
            var purchaseOrder = FindPurchaseOrderByTransactionId(txId);            
            if (purchaseOrder != null)
            {
                // Order already exists — mark it cancelled if not already done by the callback.
                var payment = FindViaBillPayment(purchaseOrder);                
                if (payment != null && !IsCancelled(payment))
                {
                    payment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusCancelled;
                    payment.Status = PaymentStatus.Failed.ToString();
                    try
                    {
                        _orderRepository.Save(purchaseOrder);

                        _settingsService.LogDebug($"[ViaBill] Cancelled purchase order saved for transaction '{txId}'.");
                    }
                    catch (Exception ex)
                    {                        
                        _logger.Error($"[ViaBill] Failed to save cancelled order: {ex.Message}", ex);
                    }
                }
                else
                {
                    _settingsService.LogDebug("[ViaBill-CAN] Payment already cancelled — no save needed.");
                }
            }
            else
            {
                // Cart still exists — mark the ViaBill payment as cancelled but leave the cart
                // intact so the customer can retry with a different payment method.
                var cart = FindCartByTransactionId(txId);                
                if (cart != null)
                {
                    var cartPayment = FindViaBillPaymentOnCart(cart);                    
                    if (cartPayment != null)
                    {
                        cartPayment.Properties[ViaBillConstants.CallbackStatusKey] = ViaBillConstants.StatusCancelled;
                        cartPayment.Status = PaymentStatus.Failed.ToString();
                        try
                        {
                            _orderRepository.Save(cart);

                            _settingsService.LogDebug($"[ViaBill] Cancelled cart saved for transaction '{txId}'.");
                        }
                        catch (Exception ex)
                        {                            
                            _logger.Error($"[ViaBill] Failed to save cancelled cart: {ex.Message}", ex);
                        }
                    }
                }
                else
                {                    
                    _logger.Warning($"[ViaBill] Cancel received but no cart or order found for transaction '{txId}'.");
                }
            }
            
            return RedirectToCheckout("Payment was cancelled. Please try again.");
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE — parameter resolution
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors PHP resolve_params(): tries JSON body → form POST → query string.
        /// </summary>
        private async Task<(string? transaction, string? orderNumber)> ResolveTransactionParams()
        {
            // 1. JSON body
            if (Request.ContentLength > 0 ||
                Request.ContentType?.Contains("application/json") == true)
            {
                try { Request.Body.Position = 0; } catch { /* not seekable */ }
                string body;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
                    body = await reader.ReadToEndAsync();

                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        var json = JsonSerializer.Deserialize<JsonElement>(body);
                        var transaction = json.TryGetProperty("transaction", out var t) ? t.GetString() : null;
                        var orderNumber = json.TryGetProperty("orderNumber", out var o) ? o.GetString() : null;
                        if (!string.IsNullOrEmpty(transaction) || !string.IsNullOrEmpty(orderNumber))
                            return (transaction, orderNumber);
                    }
                    catch { /* fall through */ }
                }
            }

            // 2. Form POST
            if (Request.HasFormContentType)
            {
                var transaction = Request.Form["transaction"].FirstOrDefault();
                var orderNumber = Request.Form["orderNumber"].FirstOrDefault();
                if (!string.IsNullOrEmpty(transaction) || !string.IsNullOrEmpty(orderNumber))
                    return (transaction, orderNumber);
            }

            // 3. Query string
            {
                var transaction = Request.Query["transaction"].FirstOrDefault();
                var orderNumber = Request.Query["orderNumber"].FirstOrDefault();
                return (transaction, orderNumber);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE — order / cart lookup
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Looks up an already-converted IPurchaseOrder by the ViaBill transaction ID.
        /// Uses the OrderFormPayment table which is populated only after SaveAsPurchaseOrder.
        /// </summary>
        private IPurchaseOrder? FindPurchaseOrderByTransactionId(string transactionId)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("EcfSqlConnection");
                int? orderGroupId = null;

                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    // TransactionID column on OrderFormPayment is populated by Optimizely
                    // when IPayment.TransactionID is set before SaveAsPurchaseOrder.
                    cmd.CommandText = @"
                        SELECT TOP 1 OrderGroupId
                        FROM   OrderFormPayment
                        WHERE  TransactionID = @TransactionID";
                    cmd.Parameters.AddWithValue("@TransactionID", transactionId);
                    var result = cmd.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                        orderGroupId = Convert.ToInt32(result);
                }

                if (orderGroupId == null)
                {
                    _settingsService.LogDebug($"[ViaBill] No purchase order row found for transaction '{transactionId}'.");
                    return null;
                }

                _settingsService.LogDebug($"[ViaBill] Found purchase order OrderGroupId={orderGroupId} for transaction '{transactionId}'.");
                return _orderRepository.Load<IPurchaseOrder>(orderGroupId.Value);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Purchase-order lookup failed for '{transactionId}': {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Looks up an ICart by the ViaBill transaction ID stored inside the
        /// SerializableCart.Data JSON column.
        ///
        /// The JSON path is:
        ///   .Forms[*].Payments[*].Properties.ViaBillTransactionId.$value  == transactionId
        ///
        /// We use a LIKE search on the raw JSON text which is safe because the
        /// transaction ID is a short alphanumeric string with no SQL-special characters.
        /// </summary>
        private ICart? FindCartByTransactionId(string transactionId)
        {
            try
            {
                var connectionString = _configuration.GetConnectionString("EcfSqlConnection");
                Guid? customerId = null;
                string? cartName = null;
                int? cartId = null;

                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                SELECT TOP 1 CustomerId, Name, CartId
                FROM   SerializableCart
                WHERE  Data LIKE @Pattern1
                   OR  Data LIKE @Pattern2";

                    var escaped = EscapeSql(transactionId);
                    cmd.Parameters.AddWithValue("@Pattern1",
                        "%\"ViaBillTransactionId\":\"" + escaped + "\"%");
                    cmd.Parameters.AddWithValue("@Pattern2",
                        "%\"TransactionID\":\"" + escaped + "\"%");                    

                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        customerId = reader.GetGuid(0);
                        cartName = reader.GetString(1);
                        cartId = reader.GetInt32(2);
                    }
                }
                
                if (customerId == null || cartName == null)
                {
                    _settingsService.LogDebug($"[ViaBill] No SerializableCart row found for transaction '{transactionId}'.");
                    return null;
                }

                _settingsService.LogDebug($"[ViaBill] Found SerializableCart: CustomerId={customerId}, Name='{cartName}'.");                

                var cart = _orderRepository.Load<ICart>(cartId.Value) as ICart;                

                // Verify what cart names exist in the DB for this customer
                using (var conn2 = new SqlConnection(_configuration.GetConnectionString("EcfSqlConnection")))
                {
                    conn2.Open();
                    using var cmd2 = conn2.CreateCommand();
                    cmd2.CommandText = "SELECT CartId, Name FROM SerializableCart WHERE CustomerId = @Cid";
                    cmd2.Parameters.AddWithValue("@Cid", customerId.Value);
                    using var r2 = cmd2.ExecuteReader();
                    var rows = new System.Text.StringBuilder();
                    while (r2.Read())
                        rows.Append($"CartId={r2.GetInt32(0)}, Name='{r2.GetString(1)}' | ");                    
                }

                return cart;
            }
            catch (Exception ex)
            {                
                _logger.Error($"[ViaBill] Cart lookup failed for '{transactionId}': {ex.Message}", ex);
                return null;
            }
        }


        /// <summary>
        /// Converts an ICart to an IPurchaseOrder.
        /// The ViaBill payment is already on the cart with Status=Pending;
        /// we mark it Processed so that SaveAsPurchaseOrder succeeds.
        /// </summary>        
        private IPurchaseOrder? ConvertCartToPurchaseOrder(ICart cart)
        {
            var lockName = "ViaBill_Convert_Cart_" + cart.OrderLink.OrderGroupId;
            var connectionString = _configuration.GetConnectionString("EcfSqlConnection");

            try
            {
                using var lockConn = new SqlConnection(connectionString);
                lockConn.Open();

                // Acquire an exclusive application lock for this cart so that if
                // Success and Callback arrive simultaneously, only one thread converts.
                using var lockCmd = lockConn.CreateCommand();
                lockCmd.CommandType = System.Data.CommandType.StoredProcedure;
                lockCmd.CommandText = "sp_getapplock";
                lockCmd.Parameters.AddWithValue("@Resource", lockName);
                lockCmd.Parameters.AddWithValue("@LockMode", "Exclusive");
                lockCmd.Parameters.AddWithValue("@LockOwner", "Session");
                lockCmd.Parameters.AddWithValue("@LockTimeout", 10000);
                var lockResultParam = lockCmd.Parameters.Add("@RETURN_VALUE", System.Data.SqlDbType.Int);
                lockResultParam.Direction = System.Data.ParameterDirection.ReturnValue;
                lockCmd.ExecuteNonQuery();
                var lockResult = (int)lockResultParam.Value;                

                if (lockResult < 0)
                {
                    _logger.Warning($"[ViaBill] Could not acquire lock for cart {cart.OrderLink.OrderGroupId}. LockResult={lockResult}");
                    return null;
                }

                try
                {
                    // Re-check: another thread may have already converted this cart
                    // while we were waiting for the lock.
                    var payment = FindViaBillPaymentOnCart(cart);
                    var txId = payment?.TransactionID;

                    if (!string.IsNullOrEmpty(txId))
                    {
                        var existing = FindPurchaseOrderByTransactionId(txId);
                        if (existing != null)
                        {
                            _logger.Warning(
                                $"[ViaBill] Cart already converted by another thread. Using existing order {existing.OrderLink.OrderGroupId}.");
                            return existing;
                        }
                    }

                    // Mark the ViaBill payment as Processed so SaveAsPurchaseOrder validates.
                    if (payment != null && payment.Status != PaymentStatus.Processed.ToString())
                        payment.Status = PaymentStatus.Processed.ToString();

                    var orderReference = _orderRepository.SaveAsPurchaseOrder(cart);
                    var purchaseOrder = _orderRepository.Load<IPurchaseOrder>(orderReference.OrderGroupId);

                    _orderRepository.Delete(cart.OrderLink);

                    _settingsService.LogDebug($"[ViaBill] Cart {cart.OrderLink.OrderGroupId} converted to purchase order {purchaseOrder?.OrderNumber}.");

                    return purchaseOrder;
                }
                finally
                {
                    using var relCmd = lockConn.CreateCommand();
                    relCmd.CommandType = System.Data.CommandType.StoredProcedure;
                    relCmd.CommandText = "sp_releaseapplock";
                    relCmd.Parameters.AddWithValue("@Resource", lockName);
                    relCmd.Parameters.AddWithValue("@LockOwner", "Session");
                    relCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {                
                _logger.Error($"[ViaBill] ConvertCartToPurchaseOrder failed: {ex.Message}", ex);
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE — payment helpers
        // ─────────────────────────────────────────────────────────────
        private static IPayment? FindViaBillPayment(IPurchaseOrder order)
        {
            foreach (var form in order.Forms)
                foreach (var p in form.Payments)
                    if (string.Equals(p.PaymentMethodName,
                            ViaBillConstants.SystemKeyword,
                            StringComparison.OrdinalIgnoreCase))
                        return p;
            return null;
        }

        private static IPayment? FindViaBillPaymentOnCart(ICart cart)
        {
            foreach (var form in cart.Forms)
                foreach (var p in form.Payments)
                    if (string.Equals(p.PaymentMethodName,
                            ViaBillConstants.SystemKeyword,
                            StringComparison.OrdinalIgnoreCase))
                        return p;
            return null;
        }

        private static bool IsApproved(IPayment payment)
            => string.Equals(
                payment.Properties[ViaBillConstants.CallbackStatusKey]?.ToString(),
                ViaBillConstants.StatusApproved,
                StringComparison.OrdinalIgnoreCase);

        private static bool IsCancelled(IPayment payment)
            => string.Equals(
                payment.Properties[ViaBillConstants.CallbackStatusKey]?.ToString(),
                ViaBillConstants.StatusCancelled,
                StringComparison.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────────────────────
        // PRIVATE — redirect helpers
        // TODO: replace with real page redirects when frontend is ready
        // ─────────────────────────────────────────────────────────────        
        private IActionResult RedirectToCheckout(string? message = null)
        {
            // Primary: use the custom URL configured in ViaBill settings
            var customUrl = _settingsResolver.GetCheckoutUrl()?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(customUrl))
            {
                var url = string.IsNullOrEmpty(message)
                    ? $"{customUrl}/"
                    : $"{customUrl}/?viabillStatus=cancelled&viabillMessage={Uri.EscapeDataString(message)}";

                _logger.Information($"ViaBill: Redirecting to custom checkout URL: {url}");
                return Redirect(url);
            }

            // Fallback: resolve from Foundation's ReferencePageSettings
            var referenceSettings = _referenceSettingsService.GetSiteSettings<ReferencePageSettings>();
            var checkoutPage = referenceSettings?.CheckoutPage
                                    ?? ContentReference.EmptyReference;

            if (ContentReference.IsNullOrEmpty(checkoutPage))
            {
                _logger.Error("ViaBill: CheckoutPage not configured in ViaBill settings or ReferencePageSettings.");
                return Redirect("/");
            }

            var baseUrl = _urlResolver.GetUrl(checkoutPage).TrimEnd('/');

            var fallbackUrl = string.IsNullOrEmpty(message)
                ? $"{baseUrl}/"
                : $"{baseUrl}/?viabillStatus=cancelled&viabillMessage={Uri.EscapeDataString(message)}";

            _logger.Information($"ViaBill: Redirecting to Foundation checkout URL: {fallbackUrl}");
            return Redirect(fallbackUrl);
        }

        private IActionResult RedirectToOrderConfirmation(IPurchaseOrder order)
        {
            // Primary: use the custom URL configured in ViaBill settings
            var customUrl = _settingsResolver.GetOrderConfirmationUrl()?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(customUrl))
            {
                var url = $"{customUrl}/?orderNumber={Uri.EscapeDataString(order.OrderNumber)}&viabillStatus=success";
                _logger.Information($"ViaBill: Redirecting to custom confirmation URL: {url}");
                return Redirect(url);
            }

            // Fallback: resolve from Foundation's ReferencePageSettings
            var referenceSettings = _referenceSettingsService.GetSiteSettings<ReferencePageSettings>();
            var confirmationPage = referenceSettings?.OrderConfirmationPage
                                    ?? ContentReference.EmptyReference;

            if (ContentReference.IsNullOrEmpty(confirmationPage))
            {
                _logger.Error("ViaBill: OrderConfirmationPage not configured in ViaBill settings or ReferencePageSettings.");
                return Redirect("/");
            }

            var queryCollection = new NameValueCollection
            {
                { "orderNumber",   order.OrderLink.OrderGroupId.ToString(CultureInfo.CurrentCulture) },
                { "contactId",     _customerContext.CurrentContactId.ToString() },
                { "viabillStatus", "success" }
            };

            var fallbackUrl = new UrlBuilder(_urlResolver.GetUrl(confirmationPage))
            {
                QueryCollection = queryCollection
            }.ToString();

            _logger.Information($"ViaBill: Redirecting to Foundation confirmation URL: {fallbackUrl}");
            return Redirect(fallbackUrl);
        }

        private IActionResult RedirectToPaymentPending(string transactionId)
        {
            var baseUrl = _settingsResolver.GetCheckoutUrl().TrimEnd('/') + "/";
            var url = $"{baseUrl}?viabillStatus=pending&transaction={Uri.EscapeDataString(transactionId)}";            
            return Redirect(url);
        }

        private static string EscapeSql(string input)
            => input.Replace("'", "''");
    }
}
