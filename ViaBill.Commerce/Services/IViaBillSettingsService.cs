using ViaBill.Commerce.Constants;

namespace ViaBill.Commerce.Services
{
    public interface IViaBillSettingsService
    {
        string GetSetting(string key);
        string PriceTagScript => GetSetting(ViaBillConstants.PriceTagScript);
        bool IsAvailable { get; }
        bool IsDebugEnabled { get; }
        void LogDebug(string message);
    }
}