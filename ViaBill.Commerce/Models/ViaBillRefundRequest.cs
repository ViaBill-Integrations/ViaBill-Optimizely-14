using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    /// <summary>
    /// Payload POSTed to ViaBill /api/transaction/refund.
    /// Also reused for cancel/void by posting to /api/transaction/cancel
    /// (cancel does not require amount — only transaction + signature).
    /// </summary>
    public class ViaBillRefundRequest
    {
        [JsonPropertyName("apikey")]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// SHA256 signature: sha256(transaction + amount + currency + secret)
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public string Transaction { get; set; } = string.Empty;

        /// <summary>
        /// Amount to refund. For a full void/cancel, this field is omitted.
        /// </summary>
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }
    }
}