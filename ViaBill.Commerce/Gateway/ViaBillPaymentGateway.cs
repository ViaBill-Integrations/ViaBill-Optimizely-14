using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EPiServer.Commerce.Order;
using EPiServer.Logging;
using Mediachase.Commerce.Orders;
using Mediachase.Commerce.Plugins.Payment;
using Microsoft.Extensions.Logging.Abstractions;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Models;
using ViaBill.Commerce.Services;

namespace ViaBill.Commerce.Gateway
{
    /// <summary>
    /// ViaBill payment gateway for Optimizely Commerce 14.
    /// Registered in Commerce Admin → Settings → Payments → Class Name.
    ///
    /// Handles four transaction types:
    ///   Authorization   → Initiates ViaBill checkout, stores redirect URL on the payment
    ///   Capture         → Captures a previously authorized payment
    ///   Credit (Refund) → Refunds a captured payment (partial or full)
    ///   Void (Cancel)   → Cancels an authorized but uncaptured payment
    ///
    /// Two constructors are provided:
    ///   1. Parameterless — used by Optimizely's internal gateway activation
    ///      (AbstractPaymentGateway is instantiated via Activator.CreateInstance)
    ///      and by CheckoutService when calling new ViaBillPaymentGateway().
    ///      Uses NullLogger and a plain HttpClient internally.
    ///   2. DI constructor — preferred when resolved via the IoC container.
    /// </summary>
    public class ViaBillPaymentGateway : AbstractPaymentGateway, IPaymentPlugin
    {
        private readonly ViaBillApiService _apiService;

        // EPiServer logger (used for all log calls in this class)
        private readonly ILogger _logger = LogManager.GetLogger(typeof(ViaBillPaymentGateway));

        // ─────────────────────────────────────────────────────────────
        // Settings — populated from Commerce Admin second tab
        // ─────────────────────────────────────────────────────────────
        private string ApiKey => GetSetting(ViaBillConstants.ApiKeySettingKey);
        private string Secret => GetSetting(ViaBillConstants.SecretSettingKey);        
        private bool TestMode => string.Equals(
                                          GetSetting(ViaBillConstants.TestModeSettingKey),
                                          "true",
                                          StringComparison.OrdinalIgnoreCase);

        private bool DebugMode => string.Equals(
                                          GetSetting(ViaBillConstants.DebugModeSettingKey),
                                          "false",
                                          StringComparison.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────────────────────
        // Constructors
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Parameterless constructor required by:
        ///   • Optimizely's AbstractPaymentGateway activation (Activator.CreateInstance)
        ///   • CheckoutService: new ViaBillPaymentGateway()
        ///
        /// Uses NullLogger&lt;ViaBillApiService&gt;.Instance (no-op logger) because there
        /// is no DI container available in this activation path.
        /// Microsoft.Extensions.Logging.Abstractions is already a project dependency.
        /// </summary>
        public ViaBillPaymentGateway()
        {
            _apiService = new ViaBillApiService(
                new SingletonHttpClientFactory(),
                NullLogger<ViaBillApiService>.Instance);
        }

        /// <summary>
        /// DI constructor — preferred when the gateway is resolved via the IoC container.
        /// </summary>
        public ViaBillPaymentGateway(ViaBillApiService apiService)
        {
            _apiService = apiService;
        }

        // ─────────────────────────────────────────────────────────────
        // Debug logging — reads DebugEnabled from gateway Settings
        // ─────────────────────────────────────────────────────────────
        private bool IsDebugEnabled => string.Equals(
            GetSetting(ViaBillConstants.DebugModeSettingKey),
            "false",
            StringComparison.OrdinalIgnoreCase);

        private void LogDebug(string message)
        {
            if (IsDebugEnabled)
                _logger.Information(message);
        }

        // ─────────────────────────────────────────────────────────────
        // IPaymentPlugin — modern interface (Commerce 14)
        // ─────────────────────────────────────────────────────────────
        public PaymentProcessingResult ProcessPayment(IOrderGroup orderGroup, IPayment payment)
        {
            try
            {
                // NOTE: TransactionType enum in this version of Optimizely Commerce only
                // contains: Authorization, Capture, Credit, Void.
                // There is no AuthorizationCapture member — use string comparison instead.
                var txType = payment.TransactionType ?? string.Empty;

                if (txType == TransactionType.Authorization.ToString())
                    return HandleAuthorize(orderGroup, payment);

                if (txType == TransactionType.Capture.ToString())
                    return HandleCapture(orderGroup, payment);

                if (txType == TransactionType.Credit.ToString())
                    return HandleRefund(orderGroup, payment);

                if (txType == TransactionType.Void.ToString())
                    return HandleVoid(orderGroup, payment);

                // "Sale" / "AuthorizationCapture" strings may be set by some checkout
                // implementations — treat them as Authorization (ViaBill is always
                // authorize-first; capture happens via the callback handler).
                if (txType.IndexOf("Sale", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    txType.IndexOf("AuthorizationCapture", StringComparison.OrdinalIgnoreCase) >= 0)
                    return HandleAuthorize(orderGroup, payment);

                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: Unsupported transaction type '{payment.TransactionType}'.");
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Unhandled exception in ProcessPayment: {ex.Message}", ex);
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: An unexpected error occurred — {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // AbstractPaymentGateway — legacy interface, delegates to above
        // ─────────────────────────────────────────────────────────────
        public override bool ProcessPayment(Payment payment, ref string message)
        {
            var result = ProcessPayment(null!, payment);
            message = result.Message;
            return result.IsSuccessful;
        }

        // ─────────────────────────────────────────────────────────────
        // AUTHORIZE
        // ─────────────────────────────────────────────────────────────
        private PaymentProcessingResult HandleAuthorize(IOrderGroup orderGroup, IPayment payment)
        {
            LogDebug($"[ViaBill] Authorize called for transaction '{GetTransactionId(payment)}'");
            ValidateSettings();

            var billingAddress = orderGroup?.GetFirstForm().Payments.FirstOrDefault()?.BillingAddress;
            var transactionId = GetTransactionId(payment);
            var currency = orderGroup?.Currency.CurrencyCode ?? "EUR";           

            var SuccessUrl = payment.Properties[ViaBillConstants.SuccessUrlKey]?.ToString();
            var CancelUrl = payment.Properties[ViaBillConstants.CancelUrlKey]?.ToString();
            var CallbackUrl = payment.Properties[ViaBillConstants.CallbackUrlKey]?.ToString();            

            if (string.IsNullOrEmpty(SuccessUrl) || string.IsNullOrEmpty(CancelUrl) || string.IsNullOrEmpty(CallbackUrl))
            {
                _logger.Error("[ViaBill] SuccessUrl, CancelUrl or CallbackUrl is missing from payment properties.");
                return PaymentProcessingResult.CreateUnsuccessfulResult("ViaBill gateway URLs are not configured.");
            } 

            ViaBillCheckoutResponse response;
            try
            {
                response = Task.Run(() => _apiService.AuthorizeAsync(
                    apiKey: ApiKey,
                    secret: Secret,
                    transactionId: transactionId,
                    amount: payment.Amount,
                    currency: currency,
                    successUrl: SuccessUrl,
                    cancelUrl: CancelUrl,
                    callbackUrl: CallbackUrl,
                    fullName: billingAddress != null
                                       ? (billingAddress.FirstName + " " + billingAddress.LastName).Trim()
                                       : string.Empty,
                    email: billingAddress?.Email ?? string.Empty,
                    phoneNumber: billingAddress?.DaytimePhoneNumber ?? string.Empty,
                    address: billingAddress?.Line1 ?? string.Empty,
                    city: billingAddress?.City ?? string.Empty,
                    postalCode: billingAddress?.PostalCode ?? string.Empty,
                    country: billingAddress?.CountryCode ?? string.Empty,
                    testMode: TestMode,
                    debugEnabled: DebugMode
                )).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Authorize HTTP call failed: {ex.Message}", ex);
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: Failed to contact payment provider — {ex.Message}");
            }

            if (!response.IsSuccess)
            {
                // ViaBillCheckoutResponse.Errors is ViaBillErrorList? which has a .Message property.
                // Access it directly — no extension method needed.                                
                var firstError = response.Errors?.FirstOrDefault();
                var errorMsg = firstError?.Message ?? firstError?.Code ?? "Unknown error from ViaBill.";
                _logger.Warning($"[ViaBill] Authorize failed: {errorMsg}");

                return PaymentProcessingResult.CreateUnsuccessfulResult("ViaBill: " + errorMsg);
            }

            payment.Properties[ViaBillConstants.RedirectUrlKey] = response.RedirectUrl;
            payment.Properties[ViaBillConstants.TransactionIdKey] = transactionId;
            payment.TransactionID = transactionId;

            LogDebug($"[ViaBill] Authorize succeeded. RedirectUrl: {response.RedirectUrl}");
            return PaymentProcessingResult.CreateSuccessfulResult(string.Empty);
        }

        // ─────────────────────────────────────────────────────────────
        // CAPTURE
        // ─────────────────────────────────────────────────────────────
        private PaymentProcessingResult HandleCapture(IOrderGroup orderGroup, IPayment payment)
        {
            var transactionId = GetStoredTransactionId(payment);
            LogDebug($"[ViaBill] Capture called for transaction '{transactionId}'");

            if (string.IsNullOrEmpty(transactionId))
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Cannot capture — no transaction ID found on payment.");

            var currency = orderGroup?.Currency.CurrencyCode ?? "DKK";
            bool success;
            try
            {
                success = Task.Run(() => _apiService.CaptureAsync(
                    apiKey: ApiKey,
                    secret: Secret,
                    transactionId: transactionId,
                    amount: -Math.Abs(payment.Amount),   // ViaBill requires negative amount
                    currency: currency,
                    testMode: TestMode,
                    debugEnabled: DebugMode
                )).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Capture HTTP call failed: {ex.Message}", ex);
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: Capture failed — {ex.Message}");
            }

            if (!success)
            {
                _logger.Warning($"[ViaBill] Capture failed for transaction '{transactionId}'");
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Capture request was rejected by the payment provider.");
            }

            payment.Status = PaymentStatus.Processed.ToString();
            LogDebug($"[ViaBill] Capture succeeded for transaction '{transactionId}'");
            return PaymentProcessingResult.CreateSuccessfulResult("Capture succeeded");
        }

        // ─────────────────────────────────────────────────────────────
        // REFUND (Credit)
        // ─────────────────────────────────────────────────────────────
        private PaymentProcessingResult HandleRefund(IOrderGroup orderGroup, IPayment payment)
        {
            var transactionId = GetStoredTransactionId(payment);
            LogDebug($"[ViaBill] Refund called for transaction '{transactionId}'");

            if (string.IsNullOrEmpty(transactionId))
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Cannot refund — no transaction ID found on payment.");

            var currency = orderGroup?.Currency.CurrencyCode ?? "EUR";
            bool success;
            try
            {
                success = Task.Run(() => _apiService.RefundAsync(
                    apiKey: ApiKey,
                    secret: Secret,
                    transactionId: transactionId,
                    amount: payment.Amount,
                    currency: currency,
                    testMode: TestMode,
                    debugEnabled: DebugMode
                )).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Refund HTTP call failed: {ex.Message}", ex);
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: Refund failed — {ex.Message}");
            }

            if (!success)
            {
                _logger.Warning($"[ViaBill] Refund failed for transaction '{transactionId}'");
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Refund request was rejected by the payment provider.");
            }

            payment.Status = PaymentStatus.Processed.ToString();
            LogDebug($"[ViaBill] Refund succeeded for transaction '{transactionId}'");
            return PaymentProcessingResult.CreateSuccessfulResult("Refund succeeded");
        }

        // ─────────────────────────────────────────────────────────────
        // VOID (Cancel)
        // ─────────────────────────────────────────────────────────────
        private PaymentProcessingResult HandleVoid(IOrderGroup orderGroup, IPayment payment)
        {
            var transactionId = GetStoredTransactionId(payment);
            LogDebug($"[ViaBill] Void called for transaction '{transactionId}'");

            if (string.IsNullOrEmpty(transactionId))
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Cannot void — no transaction ID found on payment.");

            bool success;
            try
            {
                success = Task.Run(() => _apiService.CancelAsync(
                    apiKey: ApiKey,
                    secret: Secret,
                    transactionId: transactionId,
                    testMode: TestMode,
                    debugEnabled: DebugMode
                )).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Error($"[ViaBill] Void HTTP call failed: {ex.Message}", ex);
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    $"ViaBill: Void failed — {ex.Message}");
            }

            if (!success)
            {
                _logger.Warning($"[ViaBill] Void failed for transaction '{transactionId}'");
                return PaymentProcessingResult.CreateUnsuccessfulResult(
                    "ViaBill: Void request was rejected by the payment provider.");
            }

            payment.Status = PaymentStatus.Processed.ToString();
            LogDebug($"[ViaBill] Void succeeded for transaction '{transactionId}'");
            return PaymentProcessingResult.CreateSuccessfulResult("Void succeeded");
        }

        // ─────────────────────────────────────────────────────────────
        // CALLBACK PROCESSING (called from ViaBillController)
        // ─────────────────────────────────────────────────────────────
        public PaymentProcessingResult ProcessCallback(
            IOrderGroup orderGroup, IPayment payment, ViaBillCallbackPayload payload)
        {            
            LogDebug($"[ViaBill] Callback received for transaction '{payload.Transaction}' " +                                 $"with status '{payload.Status}'");

            if (!ViaBillApiService.ValidateCallbackSignature(payload, Secret))
            {
                _logger.Error($"[ViaBill] Invalid callback signature for transaction '{payload.Transaction}'");
                return PaymentProcessingResult.CreateUnsuccessfulResult("Invalid callback signature.");
            }

            payment.Properties[ViaBillConstants.CallbackStatusKey] = payload.Status;

            if (payload.Status == ViaBillConstants.StatusApproved)
            {
                // If this was an authorize-only transaction, just mark as Processed.
                // Explicit capture is triggered separately (e.g. on shipment).
                payment.Status = PaymentStatus.Processed.ToString();
                return PaymentProcessingResult.CreateSuccessfulResult("Callback handled: Approved");
            }

            payment.Status = PaymentStatus.Failed.ToString();
            return PaymentProcessingResult.CreateUnsuccessfulResult($"Callback handled: {payload.Status}");
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE HELPERS
        // ─────────────────────────────────────────────────────────────
        private static string GetTransactionId(IPayment payment)
        {
            var existing = payment.Properties[ViaBillConstants.TransactionIdKey]?.ToString();
            if (!string.IsNullOrEmpty(existing))
                return existing;
            return $"{payment.PaymentId}-{Guid.NewGuid().ToString("N")[..8]}";
        }

        private static string GetStoredTransactionId(IPayment payment)
            => payment.Properties[ViaBillConstants.TransactionIdKey]?.ToString() ?? string.Empty;

        private string GetSetting(string key)
        {
            if (Settings != null && Settings.ContainsKey(key))
                return Settings[key]?.ToString() ?? string.Empty;
            _logger.Warning($"[ViaBill] Missing gateway setting: '{key}'");
            return string.Empty;
        }

        private void ValidateSettings()
        {
            if (string.IsNullOrEmpty(ApiKey))
                throw new InvalidOperationException(
                    "ViaBill gateway is missing 'ApiKey'. " +
                    "Configure it in Commerce Admin → Payments → (your ViaBill method) → second tab.");
            if (string.IsNullOrEmpty(Secret))
                throw new InvalidOperationException(
                    "ViaBill gateway is missing 'Secret'. " +
                    "Configure it in Commerce Admin → Payments → (your ViaBill method) → second tab.");
            /*
            if (string.IsNullOrEmpty(SuccessUrl) || string.IsNullOrEmpty(CancelUrl) || string.IsNullOrEmpty(CallbackUrl))
                throw new InvalidOperationException(
                    "ViaBill gateway is missing one or more URL settings (SuccessUrl, CancelUrl, CallbackUrl).");
            */
        }

        // ─────────────────────────────────────────────────────────────
        // INNER CLASS — minimal IHttpClientFactory shim for parameterless ctor
        // ─────────────────────────────────────────────────────────────
        private sealed class SingletonHttpClientFactory : IHttpClientFactory
        {
            private static readonly HttpClient _client = new HttpClient();
            public HttpClient CreateClient(string name) => _client;
        }
    }
}
