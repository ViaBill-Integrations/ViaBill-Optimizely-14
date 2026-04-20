using System;
using System.Collections.Generic;
using EPiServer.Globalization;
using Mediachase.Commerce.Orders.Dto;
using Mediachase.Commerce.Orders.Managers;
using ViaBill.Commerce.Constants;

namespace Foundation.Features.Checkout
{
    /// <summary>
    /// Resolves ViaBill gateway settings from Commerce Admin at runtime.
    /// Register as a singleton in your DI container:
    ///   services.AddSingleton&lt;ViaBillSettingsResolver&gt;();
    /// </summary>
    public class ViaBillSettingsResolver
    {
        private Dictionary<string, string>? _cachedSettings;
        private readonly object _lock = new object();

        private Dictionary<string, string> GetSettings()
        {
            var language = ContentLanguage.PreferredCulture.Name;

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var dto = PaymentManager.GetPaymentMethodBySystemName(
                ViaBillConstants.SystemKeyword, language);

            if (dto?.PaymentMethod != null && dto.PaymentMethod.Count > 0)
            {
                var methodRow = dto.PaymentMethod[0];

                foreach (PaymentMethodDto.PaymentMethodParameterRow row
                         in methodRow.GetPaymentMethodParameterRows())
                {
                    settings[row.Parameter] = row.Value ?? string.Empty;
                }
            }

            return settings;
        }

        public string GetApiKey()
            => GetSettings().TryGetValue(ViaBillConstants.ApiKeySettingKey, out var v) ? v : string.Empty;

        public string GetSecret()
            => GetSettings().TryGetValue(ViaBillConstants.SecretSettingKey, out var v) ? v : string.Empty;

        public bool GetTestMode()
        {
            var raw = GetSettings().TryGetValue(ViaBillConstants.TestModeSettingKey, out var v) ? v : "false";
            return bool.TryParse(raw, out var b) && b;
        }

        public bool GetDebugEnabled()
        {
            var raw = GetSettings().TryGetValue(ViaBillConstants.DebugModeSettingKey, out var v) ? v : "false";
            return bool.TryParse(raw, out var b) && b;
        }

        public string GetPriceTagScript()
            => GetSettings().TryGetValue("PriceTagScript", out var v) ? v : string.Empty;

        public string GetCountryCode()
            => GetSettings().TryGetValue("CountryCode", out var v) ? v : string.Empty;

        public string GetLanguage()
            => GetSettings().TryGetValue("Language", out var v) ? v : string.Empty;


        public string GetOrderConfirmationUrl()
        {
            var settings = GetSettings();
            return settings != null && settings.TryGetValue(ViaBillConstants.OrderConfirmationUrlKey, out var url)
                ? url
                : "/";
        }

        public string GetCheckoutUrl()
        {
            var settings = GetSettings();
            return settings != null && settings.TryGetValue(ViaBillConstants.CheckoutUrlKey, out var url)
                ? url
                : "/";
        }

        public bool IsAuthorizeAndCapture()
        {
            var settings = GetSettings();
            if (settings == null || !settings.TryGetValue(ViaBillConstants.AutoCaptureSettingKey, out var raw))
                return false;

            raw = (raw ?? string.Empty).Trim();
            return string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Invalidates the cached settings so the next call re-reads from Commerce Admin.
        /// Call this after saving changes in Commerce Admin.
        /// </summary>
        public void InvalidateCache()
        {
            lock (_lock)
                _cachedSettings = null;
        }
    }
}
