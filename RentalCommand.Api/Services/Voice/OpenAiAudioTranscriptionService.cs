using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services.Voice;

public sealed class OpenAiAudioTranscriptionService : IAudioTranscriptionService
{
    private readonly HttpClient _http;
    private readonly AssistantConfig _config;
    private readonly ILogger<OpenAiAudioTranscriptionService> _logger;
    private bool _warnedNoKey;

    public OpenAiAudioTranscriptionService(
        HttpClient http,
        IOptions<AssistantConfig> config,
        ILogger<OpenAiAudioTranscriptionService> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(
        byte[] audioBytes,
        string contentType,
        string fileName,
        CancellationToken ct = default)
    {
        if (audioBytes.Length == 0)
            return string.Empty;

        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            if (!_warnedNoKey)
            {
                _logger.LogWarning("Audio transcription requested but Assistant:ApiKey is not configured.");
                _warnedNoKey = true;
            }
            throw new VoiceTranscriptionUnavailableException(
                "Voice transcription is not configured. Set Assistant:ApiKey, then restart the API.");
        }

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("whisper-1"), "model");

        var audio = new ByteArrayContent(audioBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        content.Add(audio, "file", string.IsNullOrWhiteSpace(fileName) ? "voice.webm" : fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty;
    }
}
