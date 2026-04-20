using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    public class ViaBillCheckoutRequest
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = "3.1";

        [JsonPropertyName("apikey")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("sha256check")]
        public string? Signature { get; set; }

        [JsonPropertyName("transaction")]
        public string? Transaction { get; set; }

        [JsonPropertyName("order_number")]
        public string? OrderNumber { get; set; }

        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

        [JsonPropertyName("success_url")]
        public string? SuccessUrl { get; set; }

        [JsonPropertyName("cancel_url")]
        public string? CancelUrl { get; set; }

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [JsonPropertyName("test")]
        public bool Test { get; set; }     // true or false

        [JsonPropertyName("customParams")]
        public ViaBillCustomParams? CustomParams { get; set; }
    }

    public class ViaBillCustomParams
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("phoneNumber")]
        public string? PhoneNumber { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("address")]
        public string? Address { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("postalCode")]
        public string? PostalCode { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }
    }
}

