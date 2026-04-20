using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EPiServer.Commerce.Bolt.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Models;
using ViaBill.Commerce.Services;

namespace ViaBill.Commerce.Controllers
{
    [Route("viabill-register")]
    public class ViaBillAccountRegisterController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IViaBillSettingsService _settings;        

        public ViaBillAccountRegisterController(
            IHttpClientFactory httpClientFactory,
            IViaBillSettingsService settings)
        {
            _httpClientFactory = httpClientFactory;
            _settings = settings;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View(
                "/Views/Shared/Components/ViaBillAccountRegister/Default.cshtml",
                new ViaBillRegisterViewModel { IsAvailable = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register([FromForm] ViaBillRegisterViewModel form)
        {            
            var testMode = string.Equals(
                                          _settings.GetSetting(ViaBillConstants.TestModeSettingKey),
                                          "true",
                                          StringComparison.OrdinalIgnoreCase);


            var baseUrl = ViaBillConstants.DevelopmentMode
                ? ViaBillConstants.DevelopmentApiBaseUrl
                : testMode
                    ? ViaBillConstants.TestApiBaseUrl
            : ViaBillConstants.ProductionApiBaseUrl;

            var endpoint = $"{baseUrl}/api/addon/{ViaBillConstants.AddonName}/register";

            var payload = new
            {
                email = form.Email,
                name = form.Name,
                url = form.Url,
                country = form.Country,
                taxId = form.TaxId,
                affiliate = ViaBillConstants.AddonName,
                additionalInfo = Array.Empty<string>()
            };

            try
            {
                var client = _httpClientFactory.CreateClient("ViaBill");
                var response = await client.PostAsJsonAsync(endpoint, payload);
                var body = await response.Content.ReadFromJsonAsync<ViaBillRegisterApiResponse>();

                if (!response.IsSuccessStatusCode || body?.Key == null)
                {
                    form.IsAvailable = true;
                    form.ErrorMessage = $"Registration failed ({(int)response.StatusCode}).";
                    return View("/Views/Shared/Components/ViaBillAccountRegister/Default.cshtml", form);
                }

                return View("/Views/Shared/Components/ViaBillAccountRegister/Default.cshtml",
                    new ViaBillRegisterViewModel
                    {
                        IsAvailable = true,
                        SuccessMessage = "Registration successful! Save your credentials below.",
                        ApiKey = body.Key,
                        Secret = body.Secret,
                        PriceTagScript = body.PriceTagScript
                    });
            }
            catch (Exception ex)
            {
                form.IsAvailable = true;
                form.ErrorMessage = ex.Message;
                return View("/Views/Shared/Components/ViaBillAccountRegister/Default.cshtml", form);
            }
        }

        // Local response shape — no separate file needed
        private class ViaBillRegisterApiResponse
        {
            [JsonPropertyName("key")]
            public string? Key { get; set; }

            [JsonPropertyName("secret")]
            public string? Secret { get; set; }

            [JsonPropertyName("pricetagScript")]
            public string? PriceTagScript { get; set; }
        }
    }
}