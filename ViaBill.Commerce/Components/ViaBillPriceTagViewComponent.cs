using Microsoft.AspNetCore.Mvc;
using ViaBill.Commerce.Models;
using ViaBill.Commerce.Services;

namespace ViaBill.Commerce.Components
{
    [ViewComponent(Name = "ViaBillPriceTag")]
    public class ViaBillPriceTagViewComponent : ViewComponent
    {
        private readonly IViaBillSettingsService _settings;

        public ViaBillPriceTagViewComponent(IViaBillSettingsService settings)
        {
            _settings = settings;
        }

        /// <param name="dataView">ViaBill view context: "product" or "cart".</param>
        /// <param name="price">Known server-side price (simple products).</param>
        /// <param name="dynamicPriceSelector">CSS selector for variant products.</param>
        /// <param name="currency">ISO currency code, lowercase.</param>
        public IViewComponentResult Invoke(
            string dataView,
            decimal? price = null,
            string dynamicPriceSelector = null,
            string currency = "dkk")
        {
            if (!_settings.IsAvailable)
                return Content(string.Empty);

            var model = new ViaBillPriceTagViewModel
            {
                PriceTagScript = _settings.PriceTagScript,
                DataView = dataView,
                StaticPrice = price,
                DynamicPriceSelector = dynamicPriceSelector,
                Currency = currency.ToLowerInvariant(),
                IsAvailable = true
            };

            return View(model);
        }
    }
}