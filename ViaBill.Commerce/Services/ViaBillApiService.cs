using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Models;

namespace ViaBill.Commerce.Services
{
    public class ViaBillApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ViaBillApiService> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public ViaBillApiService(
            IHttpClientFactory httpClientFactory,
            ILogger<ViaBillApiService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger            = logger;
        }

        // ─────────────────────────────────────────────────────────────
        // PUBLIC API METHODS
        // ─────────────────────────────────────────────────────────────

        public async Task<ViaBillCheckoutResponse> AuthorizeAsync(
            string apiKey,
            string secret,
            string transactionId,
            decimal amount,
            string currency,
            string successUrl,
            string cancelUrl,
            string callbackUrl,
            string fullName,
            string email,
            string phoneNumber,
            string address,
            string city,
            string postalCode,
            string country,
            bool testMode = false,
            bool debugEnabled = false)
        {
            static string AppendQueryParameter(string url, string key, string value)
            {
                var builder = new UriBuilder(url);
                var queryToAppend = $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

                if (!string.IsNullOrEmpty(builder.Query) && builder.Query.Length > 1)
                {
                    builder.Query = builder.Query.TrimStart('?') + "&" + queryToAppend;
                }
                else
                {
                    builder.Query = queryToAppend;
                }

                return builder.Uri.ToString();
            }            

            static void ValidateRequiredSignatureField(string value, string fieldName)
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException(
                        $"ViaBill signature field '{fieldName}' cannot be null, empty, or whitespace.",
                        fieldName);
            }

            if (amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "ViaBill amount must be greater than zero.");

            var amountStr = FormatAmount(amount);
            var orderNumber = transactionId;

            successUrl = AppendQueryParameter(successUrl, "transaction", transactionId);
            cancelUrl = AppendQueryParameter(cancelUrl, "transaction", transactionId);

            ValidateRequiredSignatureField(apiKey, nameof(apiKey));
            ValidateRequiredSignatureField(secret, nameof(secret));
            ValidateRequiredSignatureField(transactionId, nameof(transactionId));
            ValidateRequiredSignatureField(amountStr, nameof(amountStr));
            ValidateRequiredSignatureField(currency, nameof(currency));
            ValidateRequiredSignatureField(orderNumber, nameof(orderNumber));
            ValidateRequiredSignatureField(successUrl, nameof(successUrl));
            ValidateRequiredSignatureField(cancelUrl, nameof(cancelUrl));
            ValidateRequiredSignatureField(callbackUrl, nameof(callbackUrl));

            LogDebug(debugEnabled,
                "[ViaBill] AuthorizeAsync called. TransactionId={TransactionId}, Amount={Amount}, Currency={Currency}, TestMode={TestMode}",
                transactionId, amountStr, currency, testMode);

            LogDebug(debugEnabled,
                "[ViaBill] URLs — Success={SuccessUrl}, Cancel={CancelUrl}, Callback={CallbackUrl}",
                successUrl, cancelUrl, callbackUrl);

            LogDebug(debugEnabled,
                "[ViaBill] Customer — FullName={FullName}, Email={Email}, Address={Address}, City={City}, PostalCode={PostalCode}, Country={Country}",
                fullName, email, address, city, postalCode, country);

            var signatureParts = new List<string>
            {
                apiKey,
                amountStr,
                currency,
                transactionId,
                orderNumber,
                successUrl,
                cancelUrl,
                secret
            };

            if (testMode)
            {
                signatureParts.Add("true");
            }

            var signature = BuildSha256Signature(signatureParts.ToArray());

            LogDebug(debugEnabled, "[ViaBill] Signature computed: {Signature}", signature);

            var request = new ViaBillCheckoutRequest
            {
                Protocol = "3.1",
                ApiKey = apiKey,
                Signature = signature,
                Transaction = transactionId,
                OrderNumber = orderNumber,
                Amount = amountStr,
                Currency = currency,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                CallbackUrl = callbackUrl,
                Test = testMode,
                CustomParams = new ViaBillCustomParams
                {
                    FullName = fullName,
                    Email = email,
                    PhoneNumber = phoneNumber,
                    Address = address,
                    City = city,
                    PostalCode = postalCode,
                    Country = country
                }
            };

            var endpoint = $"{ViaBillConstants.CheckoutPath}{ViaBillConstants.AddonName}";

            LogDebug(debugEnabled,
                "[ViaBill] Sending checkout request to ViaBill API endpoint: {Endpoint}",
                endpoint);

            ViaBillCheckoutResponse response;
            try
            {
                response = await PostFormAsync(endpoint, request, testMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ViaBill] PostFormAsync threw an exception: {Message}", ex.Message);
                throw;
            }

            LogDebug(debugEnabled,
                "[ViaBill] AuthorizeAsync response received. IsSuccess={IsSuccess}, RedirectUrl={RedirectUrl}, FirstError={FirstError}",
                response?.IsSuccess,
                response?.RedirectUrl ?? "(null)",
                response?.FirstError?.Message ?? "(none)");

            return response;
        }        

        public async Task<bool> CaptureAsync(
            string apiKey,
            string secret,
            string transactionId,
            decimal amount,
            string currency,
            bool testMode = false,
            bool debugEnabled = false)
        {
            var amountStr = FormatAmount(amount);

            // ✅ SHA256 with # separators: transaction#apikey#amount#currency#secret             
            var signature = BuildSha256Signature(transactionId, apiKey, amountStr, currency, secret);

            var request = new ViaBillCaptureRequest
            {
                ApiKey      = apiKey,
                Signature   = signature,
                Transaction = transactionId,
                Amount      = amountStr,
                Currency    = currency
            };

            // 🔍 Debug: log all capture fields before sending
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            LogDebug(debugEnabled, "[ViaBill] CaptureAsync — TransactionId: '" + transactionId + "', AmountDecimal: '" + amount + "', AmountStr: '" + amountStr + "', Currency: '" + currency + "', ApiKey empty: " + string.IsNullOrEmpty(apiKey) + ", Secret empty: " + string.IsNullOrEmpty(secret) + ", Signature empty: " + string.IsNullOrEmpty(signature) + ", TestMode: " + testMode + ", JSON: " + json);

            return await PostExpectOkAsync(ViaBillConstants.CapturePath, request, testMode);
        }

        public async Task<bool> RefundAsync(
            string apiKey,
            string secret,
            string transactionId,
            decimal amount,
            string currency,
            bool testMode = false,
            bool debugEnabled = false)
        {
            var amountStr = FormatAmount(amount);

            // ✅ SHA256 with # separators: transaction#apikey#amount#currency#secret
            var signature = BuildSha256Signature(transactionId, apiKey, amountStr, currency, secret);            

            var request = new ViaBillRefundRequest
            {
                ApiKey      = apiKey,
                Signature   = signature,
                Transaction = transactionId,
                Amount      = amountStr,
                Currency    = currency
            };
            
            return await PostExpectOkAsync(ViaBillConstants.RefundPath, request, testMode);
        }

        public async Task<bool> CancelAsync(
            string apiKey,
            string secret,
            string transactionId,
            bool testMode = false,
            bool debugEnabled = false)
        {
            // ✅ SHA256 with # separators: transaction#apikey#secret
            var signature = BuildSha256Signature(transactionId, apiKey, secret);

            var request = new ViaBillRefundRequest
            {
                ApiKey      = apiKey,
                Signature   = signature,
                Transaction = transactionId
            };

            return await PostExpectOkAsync(ViaBillConstants.CancelPath, request, testMode);
        }

        // ─────────────────────────────────────────────────────────────
        // SIGNATURE HELPERS
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a SHA256 hash of parts joined with '#' separator.
        /// Formula: sha256(part1#part2#part3#...)
        /// </summary>
        public static string BuildSha256Signature(params string[] parts)
        {
            var raw = string.Join("#", parts);
            using var sha = SHA256.Create();                              // ✅ SHA256, not MD5
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Validates the SHA256 signature on an incoming ViaBill callback.
        /// Formula: sha256(transaction#orderNumber#amount#currency#status#time#secret)
        /// </summary>
        public static bool ValidateCallbackSignature(
            ViaBillCallbackPayload payload,
            string secret,
            ILogger<ViaBillApiService>? logger = null,
            bool debugEnabled = false)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            // ✅ SHA256 with # separators matching PHP format:
            // {transaction}#{orderNumber}#{amount}#{currency}#{status}#{time}#{secret}
            var raw = string.Join("#",
                payload.Transaction,
                payload.OrderNumber  ?? string.Empty,
                payload.Amount,
                payload.Currency,
                payload.Status,
                payload.Time         ?? string.Empty,
                secret);

            using var sha = SHA256.Create();                              // ✅ SHA256, not MD5
            var hash       = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            var calculated = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            var received = payload.Signature?.ToLowerInvariant() ?? string.Empty;
            var isValid  = CryptographicEquals(calculated, received);

            // ── Debug output ─────────────────────────────────────────
            if (!isValid)
            {
                LogDebug(debugEnabled, logger, "[ViaBill] Signature raw string : '{Raw}'", raw);
                LogDebug(debugEnabled, logger, "[ViaBill] Signature calculated : '{Calculated}'", calculated);
                LogDebug(debugEnabled, logger, "[ViaBill] Signature received   : '{Received}'", received);
                LogDebug(debugEnabled, logger, "[ViaBill] Signature match      : {IsValid}", isValid);
            }            
            // ─────────────────────────────────────────────────────────

            return isValid;
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATE HTTP HELPERS
        // ─────────────────────────────────────────────────────────────

        private async Task<ViaBillCheckoutResponse> PostFormAsync(
    string path,
    ViaBillCheckoutRequest payload,
    bool testMode,
    bool debugEnabled = false)
        {
            var baseUrl = ViaBillConstants.DevelopmentMode
                ? ViaBillConstants.DevelopmentApiBaseUrl
                : testMode
                    ? ViaBillConstants.TestApiBaseUrl
                    : ViaBillConstants.ProductionApiBaseUrl;

            var url = baseUrl + path;

            // Build the JSON body matching what ViaBill expects
            var bodyObject = new Dictionary<string, object?>
            {
                ["protocol"] = payload.Protocol,
                ["apikey"] = payload.ApiKey,
                ["sha256check"] = payload.Signature,
                ["transaction"] = payload.Transaction,
                ["order_number"] = payload.OrderNumber,
                ["amount"] = payload.Amount,
                ["currency"] = payload.Currency,
                ["success_url"] = payload.SuccessUrl,
                ["cancel_url"] = payload.CancelUrl,
                ["callback_url"] = payload.CallbackUrl,
                ["test"] = payload.Test,
                ["tbyb"] = 0
            };           

            var jsonBody = JsonSerializer.Serialize(bodyObject, JsonOptions);
            var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");            

            try
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                var client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("Accept", "*/*");
                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
                client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
                client.DefaultRequestHeaders.Add("Connection", "keep-alive");
                client.DefaultRequestHeaders.Add("Referer", url);
                client.DefaultRequestHeaders.Add("User-Agent", "ViaBill-Optimizely/1.0");

                var response = await client.PostAsync(url, content);
                var body = await response.Content.ReadAsStringAsync();

                LogDebug(debugEnabled, "ViaBill {Path} → HTTP {Status}: {Body}", path, (int)response.StatusCode, body);

                var statusCode = (int)response.StatusCode;
                if (statusCode >= 300 && statusCode <= 399)
                {
                    var redirectUrl = response.Headers.Location?.ToString();

                    LogDebug(debugEnabled, "ViaBill checkout redirect URL: {Url}", redirectUrl);
                    return new ViaBillCheckoutResponse { RedirectUrl = redirectUrl };
                }

                LogDebug(debugEnabled, "ViaBill {Path} returned non-redirect status {Status}: {Body}", path, statusCode, body);

                return JsonSerializer.Deserialize<ViaBillCheckoutResponse>(body, JsonOptions)
                       ?? new ViaBillCheckoutResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViaBill HTTP call to {Path} failed", path);
                throw;
            }
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(
            string path,
            TRequest payload,
            bool testMode,
            bool debugEnabled = false)
            where TResponse : class, new()
        {
            var baseUrl = ViaBillConstants.DevelopmentMode
                ? ViaBillConstants.DevelopmentApiBaseUrl
                : testMode
                    ? ViaBillConstants.TestApiBaseUrl
                    : ViaBillConstants.ProductionApiBaseUrl;

            var url     = baseUrl + path;
            var json    = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var client   = _httpClientFactory.CreateClient("ViaBill");
                var response = await client.PostAsync(url, content);
                var body     = await response.Content.ReadAsStringAsync();

                LogDebug(debugEnabled, "ViaBill {Path} → HTTP {Status}: {Body}", path, (int)response.StatusCode, body);

                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("ViaBill {Path} returned non-success status {Status}: {Body}",
                        path, (int)response.StatusCode, body);

                return JsonSerializer.Deserialize<TResponse>(body, JsonOptions) ?? new TResponse();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViaBill HTTP call to {Path} failed", path);
                throw;
            }
        }

        private async Task<bool> PostExpectOkAsync<TRequest>(
            string path,
            TRequest payload,
            bool testMode,
            bool debugEnabled = false)
        {
            var baseUrl = ViaBillConstants.DevelopmentMode
                ? ViaBillConstants.DevelopmentApiBaseUrl
                : testMode
                    ? ViaBillConstants.TestApiBaseUrl
                    : ViaBillConstants.ProductionApiBaseUrl;

            var url     = baseUrl + path;
            var json    = JsonSerializer.Serialize(payload, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var client   = _httpClientFactory.CreateClient("ViaBill");
                var response = await client.PostAsync(url, content);
                var body     = await response.Content.ReadAsStringAsync();

                LogDebug(debugEnabled, "ViaBill {Path} → HTTP {Status}: {Body}", path, (int)response.StatusCode, body);

                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("ViaBill {Path} failed with HTTP {Status}: {Body}",
                        path, (int)response.StatusCode, body);

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViaBill HTTP call to {Path} failed", path);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // UTILITY
        // ─────────────────────────────────────────────────────────────
        
        private void LogDebug(bool debugEnabled, string message, params object[] args)
        {
            if (debugEnabled)
                _logger.LogInformation(message, args);
        }

        private static void LogDebug(bool debugEnabled, ILogger? logger, string message, params object[] args)
        {
            if (debugEnabled && logger != null)
                logger.LogInformation(message, args);
        }

        private static string FormatAmount(decimal amount)
            => amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        private static bool CryptographicEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var result = 0;
            for (var i = 0; i < a.Length; i++)
                result |= a[i] ^ b[i];
            return result == 0;
        }
    }
}
