using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

public sealed record JevChoice(
    string Choice,
    IReadOnlyDictionary<string, double> Probabilities);

public sealed class JevDecisionClient(HttpClient http)
{
    private static readonly IReadOnlyDictionary<string, string> BaseUrls =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["typesafe"] = "https://api.typesafe.ai/v1",
            ["openrouter"] = "https://openrouter.ai/api/v1",
        };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JevChoice> ChooseAsync(
        WorkspaceLlmRuntimeCredential credential,
        object state,
        string instructions,
        IReadOnlyDictionary<string, string> options,
        CancellationToken ct)
    {
        if (!BaseUrls.TryGetValue(credential.Provider, out var baseUrl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(credential), credential.Provider, "Unknown Jev provider.");
        }

        var body = new
        {
            model = credential.ModelId,
            state,
            questions = new
            {
                pick = new { type = "choice", instructions, criteria = options },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/systemone")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                "Jev rejected the request.", null, response.StatusCode);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var pick = document.RootElement.GetProperty("answers").GetProperty("pick");
            var choice = pick.GetProperty("choice").GetString();
            if (string.IsNullOrWhiteSpace(choice)) throw new JsonException();
            var probabilities = pick.GetProperty("probabilities")
                .EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.GetDouble());
            return new JevChoice(choice, probabilities);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            throw new HttpRequestException("Jev returned an unparseable reply.", ex);
        }
    }
}

public sealed class JevCredentialProbe(
    string providerKey,
    JevDecisionClient client) : ILlmCredentialProbe
{
    public string ProviderKey => providerKey;

    public async Task<LlmCredentialProbeResult> TestCredentialAsync(
        string apiKey,
        string modelId,
        CancellationToken ct = default)
    {
        try
        {
            await client.ChooseAsync(
                new WorkspaceLlmRuntimeCredential(0, providerKey, modelId, apiKey),
                new { text = "one" },
                "Pick one option.",
                new Dictionary<string, string>
                {
                    ["one"] = "The value is one.",
                    ["two"] = "The value is two.",
                },
                ct);
            return new LlmCredentialProbeResult(true, providerKey, modelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var label = providerKey.Equals("typesafe", StringComparison.OrdinalIgnoreCase)
                ? "TypeSafe"
                : "OpenRouter";
            var status = ex is HttpRequestException { StatusCode: not null } httpError
                ? ((int)httpError.StatusCode.Value).ToString()
                : "error";
            return new LlmCredentialProbeResult(
                false, providerKey, modelId, $"{label} rejected the credential ({status}).");
        }
    }
}
