using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// <see cref="IEsignProvider"/> backed by the Dropbox Sign (formerly HelloSign) REST API (v3). The HTTP
/// calls are structured to the real API but only fire when an API key is configured — when it is absent
/// the provider reports itself unconfigured and every call is a clear no-op (the gated pattern, like
/// Stripe/LLM). Authentication is HTTP Basic with the API key as the username and an empty password.
/// </summary>
public sealed class DropboxSignEsignProvider : IEsignProvider
{
    // Dropbox Sign / HelloSign v3 API root. The product was renamed but the host/path are unchanged.
    private const string BaseUrl = "https://api.hellosign.com/v3/";

    private readonly HttpClient _http;
    private readonly EsignConfig _config;
    private readonly ILogger<DropboxSignEsignProvider> _logger;

    public DropboxSignEsignProvider(HttpClient http, IOptions<EsignConfig> config, ILogger<DropboxSignEsignProvider> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public bool IsConfigured => _config.Enabled;

    public async Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Dropbox Sign is not configured — SendForSignatureAsync is a no-op.");
            return EsignResult.NotConfigured();
        }

        // POST /signature_request/send — multipart form with the document file + one row per signer.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(request.DocumentName), "title" },
            { new StringContent(request.Subject ?? request.DocumentName), "subject" },
        };

        for (var i = 0; i < request.Signers.Count; i++)
        {
            var signer = request.Signers[i];
            form.Add(new StringContent(signer.Name), $"signers[{i}][name]");
            form.Add(new StringContent(signer.Email), $"signers[{i}][email_address]");
        }

        var fileContent = new ByteArrayContent(request.DocumentBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file[0]", request.DocumentName);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "signature_request/send")
        {
            Content = form,
        };
        ApplyAuth(httpRequest);

        using var resp = await _http.SendAsync(httpRequest, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Dropbox Sign send failed ({Status}): {Body}", (int)resp.StatusCode, body);
            return new EsignResult { Status = "Error", Error = $"Provider returned {(int)resp.StatusCode}." };
        }

        using var doc = JsonDocument.Parse(body);
        var sr = doc.RootElement.GetProperty("signature_request");
        var id = sr.GetProperty("signature_request_id").GetString() ?? string.Empty;
        var status = sr.TryGetProperty("is_complete", out var complete) && complete.GetBoolean() ? "Completed" : "Sent";

        _logger.LogInformation("Dropbox Sign signature request {Id} created (status {Status}).", id, status);
        return EsignResult.Sent(id, status);
    }

    public async Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return EsignResult.NotConfigured();
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"signature_request/{envelopeId}");
        ApplyAuth(httpRequest);

        using var resp = await _http.SendAsync(httpRequest, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Dropbox Sign status check failed ({Status}): {Body}", (int)resp.StatusCode, body);
            return new EsignResult { EnvelopeId = envelopeId, Status = "Error", Error = $"Provider returned {(int)resp.StatusCode}." };
        }

        using var doc = JsonDocument.Parse(body);
        var sr = doc.RootElement.GetProperty("signature_request");
        var status = MapStatus(sr);
        return new EsignResult { EnvelopeId = envelopeId, Status = status };
    }

    public async Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // GET /signature_request/files/{id}?file_type=pdf returns the flattened, signed PDF bytes.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"signature_request/files/{envelopeId}?file_type=pdf");
        ApplyAuth(httpRequest);

        using var resp = await _http.SendAsync(httpRequest, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Dropbox Sign signed-file download failed for {Id} ({Status}).", envelopeId, (int)resp.StatusCode);
            return null;
        }

        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Map the provider's signature_request shape onto our coarse status vocabulary.</summary>
    private static string MapStatus(JsonElement sr)
    {
        if (sr.TryGetProperty("is_declined", out var declined) && declined.GetBoolean())
        {
            return "Declined";
        }

        if (sr.TryGetProperty("is_complete", out var complete) && complete.GetBoolean())
        {
            return "Completed";
        }

        return "Sent";
    }

    /// <summary>Dropbox Sign uses HTTP Basic auth: API key as the username, empty password.</summary>
    private void ApplyAuth(HttpRequestMessage request)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_config.ApiKey}:"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        if (request.RequestUri is { IsAbsoluteUri: false })
        {
            request.RequestUri = new Uri(new Uri(BaseUrl), request.RequestUri);
        }
    }
}
