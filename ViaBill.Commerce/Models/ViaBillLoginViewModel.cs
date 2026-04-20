using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ViaBill.Commerce.Models
{
    public class ViaBillLoginViewModel
    {
        [Required(ErrorMessage = "Email is required.")]
        [EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required.")]
        public string Password { get; set; } = string.Empty;

        // UI state — not submitted via form
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        // Populated on successful API response
        public string ApiKey { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string PriceTagScript { get; set; } = string.Empty;
    }

    public class ViaBillLoginResponse
    {
        [JsonPropertyName("key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("secret")]
        public string Secret { get; set; } = string.Empty;

        [JsonPropertyName("pricetagScript")]
        public string PriceTagScript { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }
}