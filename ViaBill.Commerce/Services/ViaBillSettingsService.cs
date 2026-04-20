using EPiServer.Commerce.Order;
using EPiServer.Logging;
using Mediachase.Commerce.Orders.Managers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ViaBill.Commerce.Constants;


namespace ViaBill.Commerce.Services
{
    public class ViaBillSettingsService : IViaBillSettingsService
    {
        // Lazily loaded and cached for the lifetime of this (scoped) instance
        private IDictionary<string, object> _settings;

        // Add at the top of the class:
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(ViaBillSettingsService));

        public bool IsDebugEnabled =>
            string.Equals(GetSetting(ViaBillConstants.DebugModeSettingKey), "false", StringComparison.OrdinalIgnoreCase);

        public void LogDebug(string message)
        {
            if (IsDebugEnabled)
                Logger.Information(message);
        }

        private IDictionary<string, object> Settings
        {
            get
            {
                if (_settings != null) return _settings;

                var dto = PaymentManager.GetPaymentMethodBySystemName(
                    ViaBillConstants.SystemKeyword,
                    CultureInfo.CurrentUICulture.Name
                );

                var row = dto?.PaymentMethod?.FirstOrDefault();
                if (row == null)
                {
                    _settings = new Dictionary<string, object>();
                    return _settings;
                }

                // Parameters are in the same DTO — no second DB call needed
                _settings = dto.PaymentMethodParameter
                    .Cast<System.Data.DataRow>()
                    .Where(r => r["Parameter"] != DBNull.Value && r["Parameter"] != null)
                    .ToDictionary(
                        r => r["Parameter"].ToString()!,   // ! = null-forgiving, safe after the Where filter
                        r => r["Value"]
                    );

                return _settings;
            }
        }

        public string GetSetting(string key)
        {
            if (Settings.TryGetValue(key, out var value))
                return value?.ToString() ?? string.Empty;
            return string.Empty;
        }

        public string PriceTagScript => GetSetting(ViaBillConstants.PriceTagScript);

        public bool IsAvailable =>
            !string.IsNullOrWhiteSpace(PriceTagScript);
    }
}