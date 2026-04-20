using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    /// <summary>
    /// The payload POSTed by ViaBill's server to your CallbackUrl.
    /// Validate the signature before trusting any field here.
    ///
    /// ViaBill signature algorithm (HMAC-MD5):
    ///   md5(transaction + amount + currency + status + secret)
    /// </summary>
    public class ViaBillCallbackPayload
    {
        /// <summary>
        /// Your original transaction reference — the value you passed as
        /// <c>transaction</c> during checkout initiation.
        /// </summary>
        [JsonPropertyName("transaction")]
        public string Transaction { get; set; } = string.Empty;

        /// <summary>
        /// Payment status: "approved", "cancelled", "pending", "rejected"
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("amount")]
        public string Amount { get; set; } = string.Empty;

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;

        /// <summary>
        /// HMAC-MD5 signature from ViaBill.
        /// Validate using: md5(transaction + amount + currency + status + secret)
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// Optional order number returned by ViaBill (protocol 3.1).
        /// May be null for the current JSON-based API.
        /// </summary>
        [JsonPropertyName("orderNumber")]
        public string? OrderNumber { get; set; }

        /// <summary>
        /// Optional ISO-8601 timestamp of the callback event.
        /// </summary>
        [JsonPropertyName("time")]
        public string? Time { get; set; }
    }
}
