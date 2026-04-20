namespace ViaBill.Commerce.Models
{
    public class ViaBillPriceTagViewModel
    {
        /// <summary>The {SHOPSPECIFIC} token from PriceTagScript setting.</summary>
        public string PriceTagScript { get; set; }

        /// <summary>"product", "cart", "basket", etc.</summary>
        public string DataView { get; set; }

        /// <summary>Static price for simple (non-variant) products.</summary>
        public decimal? StaticPrice { get; set; }

        /// <summary>
        /// CSS selector pointing to the DOM element that holds the price.
        /// When set, data-dynamic-price is used instead of data-price.
        /// </summary>
        public string DynamicPriceSelector { get; set; }

        /// <summary>ISO currency code, lowercase (e.g. "eur").</summary>
        public string Currency { get; set; } = "EUR";

        /// <summary>False = ViaBill not configured; render nothing.</summary>
        public bool IsAvailable { get; set; }
    }
}