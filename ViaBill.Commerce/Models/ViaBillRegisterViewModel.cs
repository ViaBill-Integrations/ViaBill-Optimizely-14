namespace ViaBill.Commerce.Models
{
    public class ViaBillRegisterViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string? TaxId { get; set; }

        public bool IsAvailable { get; set; }
        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }

        // Populated on successful registration
        public string? ApiKey { get; set; }
        public string? Secret { get; set; }
        public string? PriceTagScript { get; set; }
    }
}