using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    public class ViaBillCheckoutResponse
    {
        [JsonPropertyName("url")]
        public string? RedirectUrl { get; set; }

        /// <summary>
        /// ViaBill returns errors as an array: [{ "message": "...", "code": "..." }]
        /// </summary>
        [JsonPropertyName("errors")]
        public List<ViaBillErrorItem>? Errors { get; set; }

        /// <summary>
        /// Convenience accessor — returns first error or null.
        /// Used by ViaBillPaymentGateway: response.Errors?.Message
        /// </summary>
        [JsonIgnore]
        public ViaBillErrorItem? FirstError => Errors?.FirstOrDefault();

        public bool IsSuccess => !string.IsNullOrEmpty(RedirectUrl)
                                 && (Errors == null || !Errors.Any());
    }

    public class ViaBillErrorItem
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}
