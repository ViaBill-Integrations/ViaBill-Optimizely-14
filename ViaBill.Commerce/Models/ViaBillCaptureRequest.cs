using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    /// <summary>
    /// Payload POSTed to ViaBill /api/transaction/capture.
    /// Used when capturing a previously authorized payment.
    /// </summary>
    public class ViaBillCaptureRequest
    {
        [JsonPropertyName("apikey")]
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// SHA256 signature: sha256(transaction + amount + currency + secret)
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// The original transaction reference used during authorization.
        /// </summary>
        [JsonPropertyName("id")]
        public string Transaction { get; set; } = string.Empty;

        /// <summary>
        /// Amount to capture — must be less than or equal to the authorized amount.
        /// </summary>
        [JsonPropertyName("amount")]
        public string Amount { get; set; } = string.Empty;

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;
    }
}
