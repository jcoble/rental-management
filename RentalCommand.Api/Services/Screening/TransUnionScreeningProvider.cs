using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Screening;

/// <summary>
/// <see cref="IScreeningProvider"/> backed by TransUnion's screening API (SmartMove). The HTTP calls are
/// structured to a real screening request/result flow but only fire when an API key is configured — when
/// it is absent the provider reports itself unconfigured and every call is a clear no-op (the gated
/// pattern, like Stripe/LLM/e-sign). Authentication is a bearer API key; a partner/account id is sent
/// when configured. The provider never invents a "passed" result: a non-completed/failed call returns a
/// result that the screening service records as <see cref="ScreeningStatus.Failed"/>.
/// </summary>
public sealed class TransUnionScreeningProvider : IScreeningProvider
{
    private readonly HttpClient _http;
    private readonly ScreeningConfig _config;
    private readonly ILogger<TransUnionScreeningProvider> _logger;

    public TransUnionScreeningProvider(HttpClient http, IOptions<ScreeningConfig> config, ILogger<TransUnionScreeningProvider> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public bool IsConfigured => _config.Enabled;

    public async Task<ScreeningProviderResult> RequestScreeningAsync(ScreeningRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TransUnion screening is not configured — RequestScreeningAsync is a no-op.");
            return ScreeningProviderResult.NotConfigured();
        }

        // POST /v1/screening-requests — submit the applicant and (synchronously, for SmartMove's
        // instant report) read back the credit/criminal/eviction outcome and recommendation.
        var body = JsonSerializer.Serialize(new
        {
            client_reference = request.ApplicationId.ToString(),
            applicant = new
            {
                full_name = request.FullName,
                email = request.Email,
                ssn = request.Ssn,
                address = request.Address,
            },
            products = new[] { "credit", "criminal", "eviction" },
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/screening-requests")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(httpRequest);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(httpRequest, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TransUnion screening request failed to send.");
            return new ScreeningProviderResult { Completed = false, Error = "Screening provider request failed." };
        }

        using (resp)
        {
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("TransUnion screening failed ({Status}): {Body}", (int)resp.StatusCode, responseBody);
                return new ScreeningProviderResult
                {
                    Completed = false,
                    Error = $"Provider returned {(int)resp.StatusCode}.",
                    RawResultJson = responseBody,
                };
            }

            return Parse(responseBody);
        }
    }

    /// <summary>Map the provider's screening-result body onto our transient result shape.</summary>
    private ScreeningProviderResult Parse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            string? providerRef = root.TryGetProperty("id", out var id) ? id.GetString() : null;
            string? band = root.TryGetProperty("credit_band", out var cb) ? cb.GetString() : null;
            bool? criminal = root.TryGetProperty("has_criminal_record", out var cr) && cr.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? cr.GetBoolean() : null;
            bool? eviction = root.TryGetProperty("has_eviction_record", out var ev) && ev.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? ev.GetBoolean() : null;

            ScreeningRecommendation? recommendation = null;
            if (root.TryGetProperty("recommendation", out var rec) && rec.ValueKind == JsonValueKind.String &&
                Enum.TryParse<ScreeningRecommendation>(rec.GetString(), ignoreCase: true, out var parsed))
            {
                recommendation = parsed;
            }

            return new ScreeningProviderResult
            {
                Completed = true,
                ProviderReference = providerRef,
                CreditScoreBand = band,
                HasCriminalRecord = criminal,
                HasEvictionRecord = eviction,
                Recommendation = recommendation,
                RawResultJson = responseBody,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "TransUnion screening returned an unparseable body.");
            return new ScreeningProviderResult { Completed = false, Error = "Screening provider returned an unparseable response.", RawResultJson = responseBody };
        }
    }

    /// <summary>TransUnion uses a bearer API key; a partner/account id is sent as a header when configured.</summary>
    private void ApplyAuth(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        if (!string.IsNullOrWhiteSpace(_config.AccountId))
        {
            request.Headers.Add("X-Account-Id", _config.AccountId);
        }

        if (request.RequestUri is { IsAbsoluteUri: false } && !string.IsNullOrWhiteSpace(_config.BaseUrl))
        {
            request.RequestUri = new Uri(new Uri(_config.BaseUrl), request.RequestUri);
        }
    }
}
