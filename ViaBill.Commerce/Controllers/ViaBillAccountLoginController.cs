using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ViaBill.Commerce.Constants;
using ViaBill.Commerce.Models;
using ViaBill.Commerce.Services;

namespace ViaBill.Commerce.Controllers
{
    /// <summary>
    /// Admin-only page for retrieving ViaBill API credentials.
    /// Accessible only to Optimizely administrators.
    /// Route: /viabill-login
    /// </summary>    
    [Route("viabill-login")]
    [Authorize(Roles = "CommerceAdmins,WebAdmins")]
    public class ViaBillAccountLoginController : Controller
    {        
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IViaBillSettingsService _settings;

        public ViaBillAccountLoginController(
            IHttpClientFactory httpClientFactory,
            IViaBillSettingsService settings)
        {
            _httpClientFactory = httpClientFactory;
            _settings = settings;
        }

        [HttpGet("")]
        public IActionResult Index()
            => View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", new ViaBillLoginViewModel());

        [HttpPost("")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(ViaBillLoginViewModel model)
        {
            if (!ModelState.IsValid)
                return View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", model);

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    email = model.Email,
                    password = model.Password
                });

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                var testMode = string.Equals(
                                          _settings.GetSetting(ViaBillConstants.TestModeSettingKey),
                                          "true",
                                          StringComparison.OrdinalIgnoreCase);

                var baseUrl = ViaBillConstants.DevelopmentMode
                ? ViaBillConstants.DevelopmentApiBaseUrl
                : testMode
                    ? ViaBillConstants.TestApiBaseUrl
                : ViaBillConstants.ProductionApiBaseUrl;

                var endpoint = $"{baseUrl}/api/addon/{ViaBillConstants.AddonName}/login";                

                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(endpoint, content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    model.ErrorMessage = $"ViaBill returned HTTP {(int)response.StatusCode}. " +
                                          "Please check your credentials and try again.";
                    return View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", model);
                }

                var result = JsonSerializer.Deserialize<ViaBillLoginResponse>(
                    body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null)
                {
                    model.ErrorMessage = "Unexpected empty response from ViaBill.";
                    return View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", model);
                }

                if (!string.IsNullOrEmpty(result.Error))
                {
                    model.ErrorMessage = $"ViaBill error: {result.Error}";
                    return View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", model);
                }

                model.Success = true;
                model.ApiKey = result.ApiKey;
                model.Secret = result.Secret;
                model.PriceTagScript = result.PriceTagScript;
            }
            catch (TaskCanceledException)
            {
                model.ErrorMessage = "Request timed out after 30 seconds.";
            }
            catch (Exception ex)
            {
                model.ErrorMessage = $"Unexpected error: {ex.Message}";
            }

            return View("/Views/Shared/Components/ViaBillAccountLogin/Default.cshtml", model);
        }
    }
    
}