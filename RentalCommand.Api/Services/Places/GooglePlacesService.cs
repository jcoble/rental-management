using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Services.Places;

/// <summary>A single address suggestion: primary line ("123 Main St") + secondary ("Columbus, OH, USA").</summary>
public sealed record PlaceSuggestion(string PlaceId, string Primary, string Secondary);

/// <summary>A resolved address split into the fields the address forms own.</summary>
public sealed record ResolvedAddress(string Line1, string City, string State, string Zip);

/// <summary>
/// Server-side Google Places (New) client. The API key lives in .NET configuration
/// (<see cref="GooglePlacesConfig"/>) and is never sent to the browser — the web app calls the
/// API's <c>/api/v1/places/*</c> endpoints, which delegate here. When no key is configured the
/// service reports <see cref="Enabled"/> = false and the address fields stay manual-entry inputs.
/// </summary>
public sealed class GooglePlacesService
{
    private readonly HttpClient _http;
    private readonly GooglePlacesConfig _config;
    private readonly ILogger<GooglePlacesService> _logger;

    public GooglePlacesService(HttpClient http, IOptions<GooglePlacesConfig> config, ILogger<GooglePlacesService> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>True when an API key is configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_config.ApiKey);

    /// <summary>
    /// Address-biased autocomplete. Returns an empty list when disabled, the input is too short, or
    /// the upstream call fails, so the caller can silently fall back to manual entry.
    /// </summary>
    public async Task<IReadOnlyList<PlaceSuggestion>> AutocompleteAsync(
        string input, string? sessionToken, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(input) || input.Trim().Length < 3)
            return Array.Empty<PlaceSuggestion>();

        var body = new Dictionary<string, object?>
        {
            ["input"] = input,
            ["includedRegionCodes"] = new[] { "us" },
            // Bias toward street addresses (not businesses/POIs) for property/owner forms.
            ["includedPrimaryTypes"] = new[] { "street_address", "premise", "subpremise", "route" }
        };
        if (!string.IsNullOrWhiteSpace(sessionToken)) body["sessionToken"] = sessionToken;

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/places:autocomplete")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _config.ApiKey);
        req.Headers.TryAddWithoutValidation("X-Goog-FieldMask",
            "suggestions.placePrediction.placeId," +
            "suggestions.placePrediction.structuredFormat.mainText.text," +
            "suggestions.placePrediction.structuredFormat.secondaryText.text");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Places autocomplete failed {Status}: {Body}",
                (int)resp.StatusCode, await SafeBody(resp, ct));
            return Array.Empty<PlaceSuggestion>();
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("suggestions", out var suggestions)
            || suggestions.ValueKind != JsonValueKind.Array)
            return Array.Empty<PlaceSuggestion>();

        var results = new List<PlaceSuggestion>();
        foreach (var s in suggestions.EnumerateArray())
        {
            if (!s.TryGetProperty("placePrediction", out var p)) continue;
            var placeId = p.TryGetProperty("placeId", out var pid) ? pid.GetString() : null;
            if (string.IsNullOrEmpty(placeId)) continue;

            string primary = "", secondary = "";
            if (p.TryGetProperty("structuredFormat", out var sf))
            {
                if (sf.TryGetProperty("mainText", out var mt) && mt.TryGetProperty("text", out var mtt))
                    primary = mtt.GetString() ?? "";
                if (sf.TryGetProperty("secondaryText", out var st) && st.TryGetProperty("text", out var stt))
                    secondary = stt.GetString() ?? "";
            }
            results.Add(new PlaceSuggestion(placeId, primary, secondary));
        }
        return results;
    }

    /// <summary>
    /// Resolve a placeId into structured street/city/state/zip. The session token should match the
    /// one used for the autocomplete calls so Google bills the lookup as one session. Returns null
    /// when disabled or on error.
    /// </summary>
    public async Task<ResolvedAddress?> DetailsAsync(
        string placeId, string? sessionToken, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(placeId)) return null;

        var path = $"v1/places/{Uri.EscapeDataString(placeId)}";
        if (!string.IsNullOrWhiteSpace(sessionToken))
            path += $"?sessionToken={Uri.EscapeDataString(sessionToken)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _config.ApiKey);
        req.Headers.TryAddWithoutValidation("X-Goog-FieldMask", "addressComponents");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Places details failed {Status}: {Body}",
                (int)resp.StatusCode, await SafeBody(resp, ct));
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("addressComponents", out var comps)
            || comps.ValueKind != JsonValueKind.Array)
            return new ResolvedAddress("", "", "", "");

        string Long(string type) => Find(comps, type, longText: true);
        string Short(string type) => Find(comps, type, longText: false);

        var line1 = string.Join(' ', new[] { Long("street_number"), Long("route") }
            .Where(s => !string.IsNullOrEmpty(s))).Trim();
        var city = FirstNonEmpty(
            Long("locality"), Long("postal_town"), Long("sublocality_level_1"),
            Long("sublocality"), Long("administrative_area_level_2"));
        var state = Short("administrative_area_level_1");
        var zip = Long("postal_code");

        return new ResolvedAddress(line1, city, state, zip);
    }

    private static string Find(JsonElement components, string type, bool longText)
    {
        foreach (var c in components.EnumerateArray())
        {
            if (!c.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
                continue;
            var match = types.EnumerateArray().Any(t => t.GetString() == type);
            if (!match) continue;
            var key = longText ? "longText" : "shortText";
            if (c.TryGetProperty(key, out var v)) return v.GetString() ?? "";
            // Fall back to the other field if the requested one is absent.
            if (c.TryGetProperty(longText ? "shortText" : "longText", out var alt)) return alt.GetString() ?? "";
        }
        return "";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    private static async Task<string> SafeBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Length > 500 ? body[..500] : body;
        }
        catch { return "<no body>"; }
    }
}
