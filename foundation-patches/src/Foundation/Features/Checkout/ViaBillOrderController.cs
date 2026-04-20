using System;
using System.Threading.Tasks;
using EPiServer.Commerce.Order;
using EPiServer.Commerce.Orders.Internal;
using EPiServer.Logging;
using Mediachase.Commerce.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Services;

namespace Foundation.Features.Checkout
{
    /// <summary>
    /// Admin UI and API for ViaBill post-checkout payment operations:
    ///
    ///   GET  /viabill/admin                — Admin page (load order by number)
    ///   GET  /viabill/admin?orderNumber=X  — Admin page pre-loaded with order X
    ///   POST /viabill/admin/capture        — Capture an authorized payment
    ///   POST /viabill/admin/refund         — Refund a captured payment (full or partial)
    ///   POST /viabill/admin/void           — Void an authorized, uncaptured payment
    ///
    /// Security:
    ///   • [Authorize] requires the user to be logged in and hold CommerceAdmins or WebAdmins role.
    ///   • POST actions use [IgnoreAntiforgeryToken] because the HTML is generated directly in C#
    ///     (no Razor view engine) to avoid Foundation's checkout middleware conflict.
    ///     The [Authorize] role check is the primary security layer.
    ///
    /// Bug fixes applied (2026-04-14):
    ///   1. Capture POST: payment.Status is now set to Processed ONLY on a full capture.
    ///   2. PopulateOrderDetails: running totals computed BEFORE the legacy PaymentStatus fallback.
    ///   3. PopulateOrderDetails: isAuthorized re-enabled when isCaptured && remainingCapture > 0.
    ///   4. PopulateOrderDetails: isCaptured inferred from CapturedAmountKey > 0 as an additional
    ///      fallback, fixing the case where CapturedKey property was not persisted correctly.
    ///   5. CanVoid: explicitly blocked when any partial capture has been performed (isCaptured=true).
    ///   6. BuildHtml: "Partially Captured" badge class added; captured/remaining rows always shown
    ///      when capturedSoFar > 0, regardless of the IsCaptured flag.
    /// </summary>
    [Route("viabill/admin")]
    [Authorize(Roles = "CommerceAdmins,WebAdmins")]
    public class ViaBillOrderController : Controller
    {
        private readonly IOrderRepository _orderRepository;
        private readonly IOrderGroupFactory _orderGroupFactory;
        private readonly ViaBillApiService _apiService;
        private readonly ViaBillSettingsResolver _settingsResolver;
        private readonly IConfiguration _configuration;
        private readonly IViaBillSettingsService _settingsService;
        private readonly ILogger _logger = LogManager.GetLogger(typeof(ViaBillOrderController));

        public ViaBillOrderController(
            IOrderRepository orderRepository,
            IOrderGroupFactory orderGroupFactory,
            ViaBillApiService apiService,
            ViaBillSettingsResolver settingsResolver,
            IConfiguration configuration,
            IViaBillSettingsService settingsService)
        {
            _orderRepository = orderRepository;
            _orderGroupFactory = orderGroupFactory;
            _apiService = apiService;
            _settingsResolver = settingsResolver;
            _configuration = configuration;
            _settingsService = settingsService;
        }

        // ─────────────────────────────────────────────────────────────
        // GET /viabill/admin
        // GET /viabill/admin?orderNumber=PO22350
        // ─────────────────────────────────────────────────────────────
        [HttpGet("")]
        public IActionResult Index(string? orderNumber)
        {
            var model = new ViaBillOrderAdminViewModel { OrderNumber = orderNumber ?? string.Empty };

            if (TempData["SuccessMessage"] is string ok)
                model.SuccessMessage = ok;
            if (TempData["ErrorMessage"] is string err)
                model.ErrorMessage = err;

            if (!string.IsNullOrWhiteSpace(orderNumber))
                PopulateOrderDetails(model, orderNumber);

            return Content(BuildHtml(model), "text/html");
        }

        // ─────────────────────────────────────────────────────────────
        // POST /viabill/admin/capture
        // ─────────────────────────────────────────────────────────────
        [HttpPost("capture")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Capture(
            [FromForm] string orderNumber,
            [FromForm] decimal? amount)
        {            
            if (string.IsNullOrWhiteSpace(orderNumber))
                return RedirectWithError(orderNumber, "Order number is required.");

            var (order, payment, error) = LoadOrderAndPayment(orderNumber);
            if (error != null)
                return RedirectWithError(orderNumber, error);

            var transactionId = GetTransactionId(payment);
            if (string.IsNullOrEmpty(transactionId))
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': no ViaBill transaction ID found on payment.");

            decimal totalAuthorized = Math.Abs(payment!.Amount);
            decimal alreadyCaptured = ParseDecimalProperty(payment, ViaBillConstants.CapturedAmountKey);
            decimal remainingCapture = totalAuthorized - alreadyCaptured;

            if (remainingCapture <= 0)
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': the full authorized amount has already been captured ({totalAuthorized:F2} {order!.Currency.CurrencyCode}).");

            var captureAmount = amount.HasValue
                ? Math.Abs(amount.Value)
                : remainingCapture;

            if (captureAmount > remainingCapture)
                return RedirectWithError(orderNumber,
                    $"Capture amount {captureAmount:F2} exceeds the remaining uncaptured amount {remainingCapture:F2} {order!.Currency.CurrencyCode}.");

            var secret = _settingsResolver.GetSecret();
            var apiKey = _settingsResolver.GetApiKey();
            var testMode = _settingsResolver.GetTestMode();
            var debugEnabled = _settingsResolver.GetDebugEnabled();
            var currency = order!.Currency.CurrencyCode;

            // TODO: remove hardcoded EUR before production
            currency = "EUR";            

            bool captured;
            try
            {
                captured = await _apiService.CaptureAsync(
                    apiKey, secret, transactionId, -captureAmount, currency, testMode, debugEnabled);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill-Admin] Capture API failed for '{orderNumber}': {ex.Message}", ex);
                return RedirectWithError(orderNumber, $"ViaBill API call failed: {ex.Message}");
            }            

            if (!captured)
                return RedirectWithError(orderNumber, "ViaBill rejected the capture request.");

            decimal newCapturedTotal = alreadyCaptured + captureAmount;
            bool isFullCapture = newCapturedTotal >= totalAuthorized;

            // Only advance PaymentStatus to Processed on a FULL capture.
            // Setting it on a partial capture would flip isAuthorized → false in PopulateOrderDetails.
            if (isFullCapture)
                payment.Status = PaymentStatus.Processed.ToString();

            // Always persist both flags so PopulateOrderDetails can resolve state correctly.
            payment.Properties[ViaBillConstants.CapturedKey] = "true";
            payment.Properties[ViaBillConstants.CapturedAmountKey] =
                newCapturedTotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            if (isFullCapture)
                order.OrderStatus = OrderStatus.Completed;

            var noteDetail = isFullCapture
                ? $"Full capture of {captureAmount:F2} {currency} succeeded (total captured: {newCapturedTotal:F2} of {totalAuthorized:F2})."
                : $"Partial capture of {captureAmount:F2} {currency} succeeded (total captured: {newCapturedTotal:F2} of {totalAuthorized:F2}, remaining: {totalAuthorized - newCapturedTotal:F2}).";

            AddOrderNote(order, "ViaBill: Payment Captured",
                $"{noteDetail} Transaction ID: {transactionId}. Captured by {User.Identity?.Name}.");

            _orderRepository.Save(order);

            // Re-load a fresh copy and write properties to that instance.
            // Commerce's Save() only flushes MetaObject property bags when the
            // object is freshly loaded (not when mutated on an already-tracked instance).
            var freshOrder = _orderRepository.Load<IPurchaseOrder>(order.OrderLink.OrderGroupId);
            if (freshOrder != null)
            {
                var freshPayment = freshOrder.Forms
                    .SelectMany(f => f.Payments)
                    .FirstOrDefault(p => string.Equals(
                        p.PaymentMethodName,
                        ViaBillConstants.SystemKeyword,
                        StringComparison.OrdinalIgnoreCase));

                if (freshPayment != null)
                {
                    freshPayment.Properties[ViaBillConstants.CapturedKey] = "true";
                    freshPayment.Properties[ViaBillConstants.CapturedAmountKey] =
                        newCapturedTotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                    if (isFullCapture)
                    {
                        freshPayment.Status = PaymentStatus.Processed.ToString();
                        freshOrder.OrderStatus = OrderStatus.Completed;
                    }

                    _orderRepository.Save(freshOrder);                    
                }
            }

            // ── Verify persistence ───────────────────────────────────────────────────
            var verify = _orderRepository.Load<IPurchaseOrder>(order.OrderLink.OrderGroupId);
            var verifyPayment = verify?.Forms.SelectMany(f => f.Payments).FirstOrDefault();

            _settingsService.LogDebug($"[ViaBill-Admin] Capture succeeded for '{orderNumber}' ({captureAmount:F2} {currency}, " +               $"total captured: {newCapturedTotal:F2}/{totalAuthorized:F2}). Requested by '{User.Identity?.Name}'.");

            var msg = isFullCapture
                ? $"Full capture of {captureAmount:F2} {currency} succeeded. Order status: Completed."
                : $"Partial capture of {captureAmount:F2} {currency} succeeded. Remaining to capture: {totalAuthorized - newCapturedTotal:F2} {currency}.";

            return RedirectWithSuccess(orderNumber, msg);
        }

        // ─────────────────────────────────────────────────────────────
        // POST /viabill/admin/refund
        // ─────────────────────────────────────────────────────────────
        [HttpPost("refund")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Refund(
            [FromForm] string orderNumber,
            [FromForm] decimal? amount)
        {            
            if (string.IsNullOrWhiteSpace(orderNumber))
                return RedirectWithError(orderNumber, "Order number is required.");

            if (amount.HasValue && amount.Value <= 0)
                return RedirectWithError(orderNumber,
                    "Refund amount must be a positive number, or leave blank for a full refund.");

            var (order, payment, error) = LoadOrderAndPayment(orderNumber);
            if (error != null)
                return RedirectWithError(orderNumber, error);

            // Accept captured via explicit flag OR via PaymentStatus=Processed (legacy).
            bool hasCaptured = string.Equals(
                payment!.Properties[ViaBillConstants.CapturedKey]?.ToString(),
                "true", StringComparison.OrdinalIgnoreCase)
                || payment.Status == PaymentStatus.Processed.ToString()
                || ParseDecimalProperty(payment, ViaBillConstants.CapturedAmountKey) > 0;

            if (!hasCaptured)
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': payment has not been captured yet (Status={payment.Status}). " +
                    "Use Void to cancel an authorized payment.");

            var transactionId = GetTransactionId(payment);
            if (string.IsNullOrEmpty(transactionId))
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': no ViaBill transaction ID found on payment.");

            decimal totalCaptured = ParseDecimalProperty(payment, ViaBillConstants.CapturedAmountKey);
            if (totalCaptured == 0) totalCaptured = Math.Abs(payment.Amount); // legacy fallback
            decimal alreadyRefunded = ParseDecimalProperty(payment, ViaBillConstants.RefundedAmountKey);
            decimal remainingRefund = totalCaptured - alreadyRefunded;

            if (remainingRefund <= 0)
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': the full captured amount has already been refunded ({totalCaptured:F2} {order!.Currency.CurrencyCode}).");

            var refundAmount = amount.HasValue
                ? Math.Abs(amount.Value)
                : remainingRefund;

            if (refundAmount > remainingRefund)
                return RedirectWithError(orderNumber,
                    $"Refund amount {refundAmount:F2} exceeds the remaining refundable amount {remainingRefund:F2} {order!.Currency.CurrencyCode}.");

            var secret = _settingsResolver.GetSecret();
            var apiKey = _settingsResolver.GetApiKey();
            var testMode = _settingsResolver.GetTestMode();
            var debugEnabled = _settingsResolver.GetDebugEnabled();
            var currency = order!.Currency.CurrencyCode;

            // TODO: remove hardcoded EUR before production
            currency = "EUR";            

            bool refunded;
            try
            {
                refunded = await _apiService.RefundAsync(
                    apiKey, secret, transactionId, refundAmount, currency, testMode, debugEnabled);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill-Admin] Refund API failed for '{orderNumber}': {ex.Message}", ex);
                return RedirectWithError(orderNumber, $"ViaBill API call failed: {ex.Message}");
            }            

            if (!refunded)
                return RedirectWithError(orderNumber, "ViaBill rejected the refund request.");

            decimal newRefundedTotal = alreadyRefunded + refundAmount;
            bool isFullRefund = newRefundedTotal >= totalCaptured;

            payment.Properties[ViaBillConstants.RefundedKey] = "true";
            payment.Properties[ViaBillConstants.RefundedAmountKey] =
                newRefundedTotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            if (isFullRefund)
                order.OrderStatus = OrderStatus.Cancelled;

            var refundNoteDetail = isFullRefund
                ? $"Full refund of {refundAmount:F2} {currency} processed (total refunded: {newRefundedTotal:F2} of {totalCaptured:F2})."
                : $"Partial refund of {refundAmount:F2} {currency} processed (total refunded: {newRefundedTotal:F2} of {totalCaptured:F2}, remaining: {totalCaptured - newRefundedTotal:F2}).";

            AddOrderNote(order, "ViaBill: Payment Refunded",
                $"{refundNoteDetail} Transaction ID: {transactionId}. Refunded by {User.Identity?.Name}.");

            _orderRepository.Save(order);

            _settingsService.LogDebug($"[ViaBill-Admin] Refund succeeded for '{orderNumber}' ({refundAmount:F2} {currency}, " +             $"total refunded: {newRefundedTotal:F2}/{totalCaptured:F2}). Requested by '{User.Identity?.Name}'.");

            var msg = isFullRefund
                ? $"Full refund of {refundAmount:F2} {currency} succeeded. Order status: Cancelled."
                : $"Partial refund of {refundAmount:F2} {currency} succeeded. Remaining to refund: {totalCaptured - newRefundedTotal:F2} {currency}.";

            return RedirectWithSuccess(orderNumber, msg);
        }

        // ─────────────────────────────────────────────────────────────
        // POST /viabill/admin/void
        // ─────────────────────────────────────────────────────────────
        [HttpPost("void")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Void([FromForm] string orderNumber)
        {            
            if (string.IsNullOrWhiteSpace(orderNumber))
                return RedirectWithError(orderNumber, "Order number is required.");

            var (order, payment, error) = LoadOrderAndPayment(orderNumber);
            if (error != null)
                return RedirectWithError(orderNumber, error);

            // Block void if ANY capture has been performed (partial or full).
            bool anyCapturePerformed =
                string.Equals(payment!.Properties[ViaBillConstants.CapturedKey]?.ToString(),
                    "true", StringComparison.OrdinalIgnoreCase)
                || payment.Status == PaymentStatus.Processed.ToString()
                || ParseDecimalProperty(payment, ViaBillConstants.CapturedAmountKey) > 0;

            if (anyCapturePerformed)
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': payment has already been (partially) captured. Use Refund instead of Void.");

            var transactionId = GetTransactionId(payment);
            if (string.IsNullOrEmpty(transactionId))
                return RedirectWithError(orderNumber,
                    $"Order '{orderNumber}': no ViaBill transaction ID found on payment.");

            var secret = _settingsResolver.GetSecret();
            var apiKey = _settingsResolver.GetApiKey();
            var testMode = _settingsResolver.GetTestMode();
            var debugEnabled = _settingsResolver.GetDebugEnabled();

            bool voided;
            try
            {
                voided = await _apiService.CancelAsync(apiKey, secret, transactionId, testMode, debugEnabled);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill-Admin] Void API failed for '{orderNumber}': {ex.Message}", ex);
                return RedirectWithError(orderNumber, $"ViaBill API call failed: {ex.Message}");
            }            

            if (!voided)
                return RedirectWithError(orderNumber, "ViaBill rejected the void request.");

            payment.Status = PaymentStatus.Failed.ToString();
            order.OrderStatus = OrderStatus.Cancelled;

            AddOrderNote(order, "ViaBill: Payment Voided",
                $"The authorized payment was voided by {User.Identity?.Name}. Transaction ID: {transactionId}.");

            _orderRepository.Save(order);

            _settingsService.LogDebug($"[ViaBill-Admin] Void succeeded for '{orderNumber}'. Requested by '{User.Identity?.Name}'.");

            return RedirectWithSuccess(orderNumber, "Payment voided successfully. Order status: Cancelled.");
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE HELPERS
        // ─────────────────────────────────────────────────────────────

        private void PopulateOrderDetails(ViaBillOrderAdminViewModel model, string orderNumber)
        {
            var (order, payment, error) = LoadOrderAndPayment(orderNumber);

            if (error != null)
            {
                model.ErrorMessage = error;
                return;
            }

            model.OrderFound = true;
            model.OrderStatus = order!.OrderStatus.ToString();
            model.Currency = order.Currency.CurrencyCode;
            model.PaymentMethodName = payment!.PaymentMethodName;
            model.PaymentAmount = Math.Abs(payment.Amount);
            model.TransactionId = GetTransactionId(payment) ?? "(not found)";

            // ── Read explicit lifecycle flags ─────────────────────────────────────────
            bool isAuthorized = string.Equals(
                payment.Properties[ViaBillConstants.AuthorizedKey]?.ToString(),
                "true", StringComparison.OrdinalIgnoreCase);

            bool isCaptured = string.Equals(
                payment.Properties[ViaBillConstants.CapturedKey]?.ToString(),
                "true", StringComparison.OrdinalIgnoreCase);

            bool isRefunded = string.Equals(
                payment.Properties[ViaBillConstants.RefundedKey]?.ToString(),
                "true", StringComparison.OrdinalIgnoreCase);

            // ── Compute running totals FIRST (needed by legacy fallback guard below) ──
            decimal totalAuthorized = Math.Abs(payment.Amount);
            decimal capturedSoFar = ParseDecimalProperty(payment, ViaBillConstants.CapturedAmountKey);
            decimal refundedSoFar = ParseDecimalProperty(payment, ViaBillConstants.RefundedAmountKey);

            // FIX #4: infer isCaptured from CapturedAmountKey > 0 as belt-and-suspenders
            // fallback for cases where CapturedKey was not persisted (e.g. first partial capture
            // on some Commerce storage backends that don't flush custom properties reliably).
            if (!isCaptured && capturedSoFar > 0)
                isCaptured = true;

            // Legacy: CapturedKey set but CapturedAmountKey missing → assume full capture.
            if (isCaptured && capturedSoFar == 0)
                capturedSoFar = totalAuthorized;

            decimal remainingCapture = Math.Max(0, totalAuthorized - capturedSoFar);
            decimal remainingRefund = Math.Max(0, capturedSoFar - refundedSoFar);

            // ── Legacy PaymentStatus fallback (pre-dates explicit flag system) ─────────
            if (!isAuthorized && !isCaptured)
            {
                isCaptured = payment.Status == PaymentStatus.Processed.ToString();
                isAuthorized = !isCaptured && payment.Status == PaymentStatus.Pending.ToString();

                if (isCaptured && capturedSoFar == 0)
                {
                    capturedSoFar = totalAuthorized;
                    remainingCapture = 0;
                    remainingRefund = Math.Max(0, capturedSoFar - refundedSoFar);
                }
            }

            // FIX #3: partial capture — re-enable isAuthorized so CanCapture stays true.
            if (isCaptured && remainingCapture > 0)
                isAuthorized = true;

            bool isCancelled = payment.Status == PaymentStatus.Failed.ToString()
                            || order.OrderStatus == OrderStatus.Cancelled;

            // ── Human-readable payment status ─────────────────────────────────────────
            if (isCancelled)
                model.PaymentStatus = "Cancelled";
            else if (isRefunded)
                model.PaymentStatus = isCaptured ? "Captured / Refunded" : "Refunded";
            else if (isCaptured)
                model.PaymentStatus = remainingCapture > 0 ? "Partially Captured" : "Captured";
            else if (isAuthorized)
                model.PaymentStatus = "Authorized";
            else
                model.PaymentStatus = "Pending";

            model.IsAuthorized = isAuthorized;
            model.IsCaptured = isCaptured;
            model.IsRefunded = isRefunded;

            model.TotalAuthorized = totalAuthorized;
            model.CapturedSoFar = capturedSoFar;
            model.RefundedSoFar = refundedSoFar;
            model.RemainingCapture = remainingCapture;
            model.RemainingRefund = remainingRefund;

            // FIX #5: CanVoid is false once ANY capture has been performed.
            model.CanCapture = isAuthorized && remainingCapture > 0 && !isCancelled;
            model.CanRefund = isCaptured && remainingRefund > 0 && !isCancelled;
            model.CanVoid = isAuthorized && !isCaptured && !isCancelled;
        }

        private static string BuildHtml(ViaBillOrderAdminViewModel m)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"utf-8\" />");
            sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
            sb.AppendLine($"  <title>ViaBill Admin — {Encode(m.OrderNumber)}</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    *,*::before,*::after{box-sizing:border-box}");
            sb.AppendLine("    body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f4f6f8;color:#1a1a2e;margin:0;padding:2rem}");
            sb.AppendLine("    .card{background:#fff;border-radius:8px;box-shadow:0 2px 8px rgba(0,0,0,.10);max-width:680px;margin:0 auto;padding:2rem}");
            sb.AppendLine("    h1{font-size:1.4rem;margin:0 0 1.5rem}");
            sb.AppendLine("    .meta{display:grid;grid-template-columns:1fr 1fr;gap:.4rem 1.5rem;background:#f9fafb;border:1px solid #e5e7eb;border-radius:6px;padding:1rem 1.2rem;margin-bottom:1.5rem;font-size:.9rem}");
            sb.AppendLine("    .meta dt{color:#6b7280;font-weight:500} .meta dd{margin:0;font-weight:600}");
            sb.AppendLine("    .badge{display:inline-block;padding:.2rem .7rem;border-radius:999px;font-size:.78rem;font-weight:700;text-transform:uppercase}");
            sb.AppendLine("    .auth{background:#fef3c7;color:#92400e} .cap{background:#d1fae5;color:#065f46} .partial{background:#dbeafe;color:#1e40af} .can{background:#fee2e2;color:#991b1b} .ref{background:#ede9fe;color:#5b21b6} .unk{background:#e5e7eb;color:#374151}");
            sb.AppendLine("    .sec-title{font-size:1rem;font-weight:700;margin:1.5rem 0 .75rem;padding-bottom:.4rem;border-bottom:2px solid #e5e7eb}");
            sb.AppendLine("    .action{background:#f9fafb;border:1px solid #e5e7eb;border-radius:6px;padding:1.2rem;margin-bottom:1rem}");
            sb.AppendLine("    .action h3{margin:0 0 .5rem;font-size:.95rem;font-weight:700}");
            sb.AppendLine("    .action p{margin:0 0 .8rem;font-size:.85rem;color:#6b7280;line-height:1.5}");
            sb.AppendLine("    .row{display:flex;align-items:center;gap:.6rem;flex-wrap:wrap}");
            sb.AppendLine("    .row label{font-size:.85rem;font-weight:500;white-space:nowrap}");
            sb.AppendLine("    input[type=number]{width:130px;padding:.4rem .6rem;border:1px solid #d1d5db;border-radius:5px;font-size:.9rem}");
            sb.AppendLine("    .btn{padding:.45rem 1.1rem;border:none;border-radius:5px;font-size:.9rem;font-weight:600;cursor:pointer}");
            sb.AppendLine("    .btn-cap{background:#2563eb;color:#fff} .btn-ref{background:#059669;color:#fff} .btn-void{background:#dc2626;color:#fff} .btn-srch{background:#4b5563;color:#fff}");
            sb.AppendLine("    .alert{padding:.8rem 1rem;border-radius:6px;font-size:.9rem;margin-bottom:1rem}");
            sb.AppendLine("    .ok{background:#d1fae5;color:#065f46;border:1px solid #6ee7b7} .err{background:#fee2e2;color:#991b1b;border:1px solid #fca5a5} .info{background:#dbeafe;color:#1e40af;border:1px solid #93c5fd}");
            sb.AppendLine("    .search{display:flex;gap:.6rem;margin-bottom:1.5rem;flex-wrap:wrap}");
            sb.AppendLine("    .search input{flex:1;min-width:180px;padding:.45rem .7rem;border:1px solid #d1d5db;border-radius:5px;font-size:.9rem}");
            sb.AppendLine("    .none{font-size:.9rem;color:#6b7280;font-style:italic}");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head><body><div class=\"card\">");
            sb.AppendLine("  <h1>ViaBill Order Management</h1>");

            // Search form
            sb.AppendLine("  <form method=\"get\" action=\"/viabill/admin\" class=\"search\">");
            sb.AppendLine($"    <input type=\"text\" name=\"orderNumber\" value=\"{Encode(m.OrderNumber)}\" placeholder=\"Order number e.g. PO22350\" required />");
            sb.AppendLine("    <button type=\"submit\" class=\"btn btn-srch\">Load Order</button>");
            sb.AppendLine("  </form>");

            // Flash messages
            if (!string.IsNullOrEmpty(m.SuccessMessage))
                sb.AppendLine($"  <div class=\"alert ok\">&#10003; {Encode(m.SuccessMessage)}</div>");
            if (!string.IsNullOrEmpty(m.ErrorMessage))
                sb.AppendLine($"  <div class=\"alert err\">&#10007; {Encode(m.ErrorMessage)}</div>");

            if (m.OrderFound)
            {
                // FIX #6: added "partial" badge class for Partially Captured status
                var badgeClass = m.PaymentStatus switch
                {
                    "Authorized" => "auth",
                    "Partially Captured" => "partial",
                    "Captured" => "cap",
                    "Captured / Refunded" => "ref",
                    "Refunded" => "ref",
                    "Cancelled" => "can",
                    _ => "unk"
                };

                sb.AppendLine("  <dl class=\"meta\">");
                sb.AppendLine($"    <dt>Order Number</dt><dd>{Encode(m.OrderNumber)}</dd>");
                sb.AppendLine($"    <dt>Order Status</dt><dd>{Encode(m.OrderStatus)}</dd>");
                sb.AppendLine($"    <dt>Payment Method</dt><dd>{Encode(m.PaymentMethodName)}</dd>");
                sb.AppendLine($"    <dt>Payment Status</dt><dd><span class=\"badge {badgeClass}\">{Encode(m.PaymentStatus)}</span></dd>");
                sb.AppendLine($"    <dt>Authorized Amount</dt><dd>{m.TotalAuthorized:F2} {Encode(m.Currency)}</dd>");

                // FIX #6: show captured rows whenever capturedSoFar > 0, not just when IsCaptured flag is set
                if (m.CapturedSoFar > 0)
                {
                    sb.AppendLine($"    <dt>Captured</dt><dd>{m.CapturedSoFar:F2} {Encode(m.Currency)}</dd>");
                    if (m.RemainingCapture > 0)
                        sb.AppendLine($"    <dt>Remaining to Capture</dt><dd style=\"color:#1d4ed8;font-weight:700\">{m.RemainingCapture:F2} {Encode(m.Currency)}</dd>");
                }

                if (m.RefundedSoFar > 0)
                {
                    sb.AppendLine($"    <dt>Refunded</dt><dd>{m.RefundedSoFar:F2} {Encode(m.Currency)}</dd>");
                    if (m.RemainingRefund > 0)
                        sb.AppendLine($"    <dt>Remaining to Refund</dt><dd style=\"color:#065f46;font-weight:700\">{m.RemainingRefund:F2} {Encode(m.Currency)}</dd>");
                }

                sb.AppendLine($"    <dt>Transaction ID</dt><dd style=\"font-size:.8rem;word-break:break-all\">{Encode(m.TransactionId)}</dd>");
                sb.AppendLine("  </dl>");

                sb.AppendLine("  <div class=\"sec-title\">Available Actions</div>");

                if (m.CanCapture)
                {
                    var captureDesc = m.CapturedSoFar > 0
                        ? $"Collect the next portion of the authorized funds. Already captured: {m.CapturedSoFar:F2} {m.Currency}. Leave amount blank to capture the remaining {m.RemainingCapture:F2} {m.Currency}."
                        : $"Collect the authorized funds. Leave amount blank to capture the full amount ({m.RemainingCapture:F2} {m.Currency}).";

                    sb.AppendLine("  <div class=\"action\">");
                    sb.AppendLine("    <h3>Capture Payment</h3>");
                    sb.AppendLine($"    <p>{Encode(captureDesc)}</p>");
                    sb.AppendLine("    <form method=\"post\" action=\"/viabill/admin/capture\">");
                    sb.AppendLine($"      <input type=\"hidden\" name=\"orderNumber\" value=\"{Encode(m.OrderNumber)}\" />");
                    sb.AppendLine("      <div class=\"row\">");
                    sb.AppendLine($"        <label>Amount ({Encode(m.Currency)}):</label>");
                    sb.AppendLine($"        <input type=\"number\" name=\"amount\" min=\"0.01\" max=\"{m.RemainingCapture:F2}\" step=\"0.01\" placeholder=\"{m.RemainingCapture:F2}\" />");
                    sb.AppendLine("        <button type=\"submit\" class=\"btn btn-cap\">Capture</button>");
                    sb.AppendLine("      </div>");
                    sb.AppendLine("    </form>");
                    sb.AppendLine("  </div>");
                }

                if (m.CanRefund)
                {
                    var refundDesc = m.RefundedSoFar > 0
                        ? $"Return more funds to the customer. Already refunded: {m.RefundedSoFar:F2} {m.Currency}. Leave amount blank to refund the remaining {m.RemainingRefund:F2} {m.Currency}. Refunding the full remaining amount sets the order to Cancelled."
                        : $"Return funds to the customer. Leave amount blank to refund the full captured amount ({m.RemainingRefund:F2} {m.Currency}). A full refund sets the order to Cancelled.";

                    sb.AppendLine("  <div class=\"action\">");
                    sb.AppendLine("    <h3>Refund Payment</h3>");
                    sb.AppendLine($"    <p>{Encode(refundDesc)}</p>");
                    sb.AppendLine("    <form method=\"post\" action=\"/viabill/admin/refund\">");
                    sb.AppendLine($"      <input type=\"hidden\" name=\"orderNumber\" value=\"{Encode(m.OrderNumber)}\" />");
                    sb.AppendLine("      <div class=\"row\">");
                    sb.AppendLine($"        <label>Amount ({Encode(m.Currency)}):</label>");
                    sb.AppendLine($"        <input type=\"number\" name=\"amount\" min=\"0.01\" max=\"{m.RemainingRefund:F2}\" step=\"0.01\" placeholder=\"{m.RemainingRefund:F2}\" />");
                    sb.AppendLine("        <button type=\"submit\" class=\"btn btn-ref\">Refund</button>");
                    sb.AppendLine("      </div>");
                    sb.AppendLine("    </form>");
                    sb.AppendLine("  </div>");
                }

                if (m.CanVoid)
                {
                    sb.AppendLine("  <div class=\"action\">");
                    sb.AppendLine("    <h3>Void / Cancel Payment</h3>");
                    sb.AppendLine("    <p>Cancel the authorization and release the reserved funds. Only possible before any capture has been performed. Sets the order to Cancelled.</p>");
                    sb.AppendLine("    <form method=\"post\" action=\"/viabill/admin/void\">");
                    sb.AppendLine($"      <input type=\"hidden\" name=\"orderNumber\" value=\"{Encode(m.OrderNumber)}\" />");
                    sb.AppendLine("      <div class=\"row\">");
                    sb.AppendLine("        <button type=\"submit\" class=\"btn btn-void\">Void / Cancel</button>");
                    sb.AppendLine("      </div>");
                    sb.AppendLine("    </form>");
                    sb.AppendLine("  </div>");
                }

                if (!m.CanCapture && !m.CanRefund && !m.CanVoid)
                    sb.AppendLine($"  <p class=\"none\">No actions available for this order (Payment: {Encode(m.PaymentStatus)}, Order: {Encode(m.OrderStatus)}).</p>");
            }
            else if (!string.IsNullOrEmpty(m.OrderNumber) && string.IsNullOrEmpty(m.ErrorMessage))
            {
                sb.AppendLine("  <div class=\"alert info\">Enter an order number above and click Load Order.</div>");
            }

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        private static string Encode(string? s)
            => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        /// <summary>
        /// Resolves OrderGroupId from TrackingNumber via direct SQL, then loads via IOrderRepository.
        /// IOrderRepository has no Load(string) overload — only Load&lt;T&gt;(int orderGroupId).
        /// </summary>
        private (IPurchaseOrder? order, IPayment? payment, string? error)
            LoadOrderAndPayment(string orderNumber)
        {
            int? orderGroupId = null;
            try
            {
                var connectionString = _configuration.GetConnectionString("EcfSqlConnection");
                using var conn = new SqlConnection(connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = 10;
                cmd.CommandText = @"
                    SELECT TOP 1 po.ObjectId
                    FROM   OrderGroup_PurchaseOrder po
                    WHERE  po.TrackingNumber = @TrackingNumber";
                cmd.Parameters.AddWithValue("@TrackingNumber", orderNumber);
                var result = cmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    orderGroupId = Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill-Admin] SQL lookup failed for '{orderNumber}': {ex.Message}", ex);
                return (null, null, $"Database lookup failed for order '{orderNumber}': {ex.Message}");
            }

            if (orderGroupId == null)
                return (null, null, $"Order '{orderNumber}' was not found.");

            IPurchaseOrder? order;
            try
            {
                order = _orderRepository.Load<IPurchaseOrder>(orderGroupId.Value);
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill-Admin] Load failed for '{orderNumber}' (id={orderGroupId}): {ex.Message}", ex);
                return (null, null, $"Failed to load order '{orderNumber}': {ex.Message}");
            }

            if (order == null)
                return (null, null, $"Order '{orderNumber}' was not found.");

            var payment = FindViaBillPayment(order);
            if (payment == null)
                return (null, null, $"Order '{orderNumber}' does not have a ViaBill payment.");

            return (order, payment, null);
        }

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

        private static string? GetTransactionId(IPayment? payment)
        {
            if (payment == null) return null;
            var fromProperty = payment.Properties[ViaBillConstants.TransactionIdKey]?.ToString();
            return !string.IsNullOrEmpty(fromProperty) ? fromProperty : payment.TransactionID;
        }

        private static decimal ParseDecimalProperty(IPayment payment, string key)
        {
            var raw = payment.Properties[key]?.ToString();
            return decimal.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val) ? val : 0m;
        }

        private void AddOrderNote(IPurchaseOrder order, string title, string detail)
        {
            var note = _orderGroupFactory.CreateOrderNote(order);
            note.Title = title;
            note.Detail = detail;
            note.Type = OrderNoteTypes.System.ToString();
            note.CustomerId = Guid.Empty;
            note.Created = DateTime.UtcNow;
            order.Notes.Add(note);
        }

        private IActionResult RedirectWithSuccess(string? orderNumber, string message)
        {
            TempData["SuccessMessage"] = message;
            return Redirect($"/viabill/admin?orderNumber={Uri.EscapeDataString(orderNumber ?? string.Empty)}");
        }

        private IActionResult RedirectWithError(string? orderNumber, string message)
        {
            TempData["ErrorMessage"] = message;
            return Redirect($"/viabill/admin?orderNumber={Uri.EscapeDataString(orderNumber ?? string.Empty)}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // View Model
    // ─────────────────────────────────────────────────────────────────────────
    public class ViaBillOrderAdminViewModel
    {
        public string OrderNumber { get; set; } = string.Empty;
        public bool OrderFound { get; set; }
        public string OrderStatus { get; set; } = string.Empty;
        public string PaymentMethodName { get; set; } = string.Empty;
        public string PaymentStatus { get; set; } = string.Empty;
        public decimal PaymentAmount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;

        // Lifecycle flags
        public bool IsAuthorized { get; set; }
        public bool IsCaptured { get; set; }
        public bool IsRefunded { get; set; }

        // Running totals
        public decimal TotalAuthorized { get; set; }
        public decimal CapturedSoFar { get; set; }
        public decimal RefundedSoFar { get; set; }
        public decimal RemainingCapture { get; set; }
        public decimal RemainingRefund { get; set; }

        // Available actions
        public bool CanCapture { get; set; }
        public bool CanRefund { get; set; }
        public bool CanVoid { get; set; }

        // Flash messages
        public string? SuccessMessage { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
