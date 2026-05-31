using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Anthropic Claude Messages API implementation of <see cref="ILlmProvider"/>. Routes born-digital
/// PDFs through text-only extraction (cheaper, more accurate) and everything else through a vision
/// block. Forces a single tool_use response so the model returns structured JSON plus a self-reported
/// 0–1 confidence per field. Falls back to a deterministic no-op (no network call, all confidence 0)
/// when no API key is configured, so the app runs offline.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const string ToolName = "record_extraction";

    private readonly HttpClient _http;
    private readonly AssistantConfig _config;
    private readonly ILogger<AnthropicLlmProvider> _logger;
    private bool _warnedNoKey;

    public AnthropicLlmProvider(HttpClient http, IOptions<AssistantConfig> config, ILogger<AnthropicLlmProvider> logger)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            WarnNoKeyOnce();
            return string.Empty;
        }

        var body = new
        {
            model = _config.ModelId,
            max_tokens = 1024,
            messages = new[] { new { role = "user", content = prompt } }
        };
        using var resp = await SendAsync(body, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        return text ?? string.Empty;
    }

    public async Task<ExtractedFields> ExtractAsync(
        byte[] documentBytes,
        string contentType,
        string instructions,
        IReadOnlyList<ExtractionFieldSpec> fields,
        string? groundingContext = null,
        CancellationToken ct = default)
    {
        // --- Deterministic no-op fallback (offline / unconfigured) ---
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            WarnNoKeyOnce();
            return new ExtractedFields
            {
                ModelId = "noop",
                TokensUsed = 0,
                Fields = fields.ToDictionary(
                    f => f.Name,
                    _ => new FieldExtraction { Value = string.Empty, Confidence = 0m })
            };
        }

        var isPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || (documentBytes.Length >= 4 && documentBytes[0] == 0x25 && documentBytes[1] == 0x50
                        && documentBytes[2] == 0x44 && documentBytes[3] == 0x46); // %PDF

        // Text-first routing for born-digital PDFs: if we can recover meaningful text, send it as text
        // (no vision tokens). Scanned/handwritten PDFs yield little text and fall through to vision.
        string? bornDigitalText = isPdf ? TryExtractPdfText(documentBytes) : null;

        var systemPrompt = new StringBuilder(instructions);
        if (!string.IsNullOrWhiteSpace(groundingContext))
        {
            systemPrompt.Append("\n\nKnown records in this portfolio you may match against (JSON):\n");
            systemPrompt.Append(groundingContext);
        }

        object userContent;
        if (bornDigitalText is { Length: > 40 })
        {
            userContent = new object[]
            {
                new { type = "text", text = "Document text follows. Extract the fields.\n\n" + bornDigitalText }
            };
        }
        else
        {
            var mediaType = isPdf ? "application/pdf" : contentType;
            var blockType = isPdf ? "document" : "image";
            userContent = new object[]
            {
                new
                {
                    type = blockType,
                    source = new
                    {
                        type = "base64",
                        media_type = mediaType,
                        data = Convert.ToBase64String(documentBytes)
                    }
                },
                new { type = "text", text = "Extract the fields from the attached document." }
            };
        }

        var body = new
        {
            model = _config.ModelId,
            max_tokens = 1500,
            system = systemPrompt.ToString(),
            tools = new[] { BuildTool(fields) },
            tool_choice = new { type = "tool", name = ToolName },
            messages = new[] { new { role = "user", content = userContent } }
        };

        using var resp = await SendAsync(body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseToolResult(json, fields);
    }

    // ---- Anthropic tool schema: every field PLUS a sibling "<name>_confidence" number 0–1 ----
    private static object BuildTool(IReadOnlyList<ExtractionFieldSpec> fields)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var f in fields)
        {
            object schema = f.Type switch
            {
                "number" => new { type = "number", description = f.Description },
                "date"   => new { type = "string", description = f.Description + " (ISO 8601 date)" },
                "enum"   => new { type = "string", @enum = f.EnumValues ?? Array.Empty<string>(), description = f.Description },
                _        => new { type = "string", description = f.Description }
            };
            props[f.Name] = schema;
            props[f.Name + "_confidence"] = new
            {
                type = "number",
                description = $"Your calibrated confidence 0.0–1.0 that '{f.Name}' is correct. " +
                              "Use 1.0 only for values read verbatim and unambiguous; lower it when guessing or the source is unclear."
            };
            if (f.Required) { required.Add(f.Name); required.Add(f.Name + "_confidence"); }
        }

        return new
        {
            name = ToolName,
            description = "Return the extracted fields and a self-reported confidence for each.",
            input_schema = new { type = "object", properties = props, required = required.ToArray() }
        };
    }

    private static ExtractedFields ParseToolResult(string responseJson, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var result = new ExtractedFields
        {
            ModelId = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            TokensUsed = root.TryGetProperty("usage", out var u)
                ? (u.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0)
                  + (u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0)
                : 0
        };

        // Find the tool_use content block and read its "input" object.
        JsonElement input = default;
        var found = false;
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                    && block.TryGetProperty("input", out input))
                {
                    found = true;
                    break;
                }
            }
        }

        foreach (var f in fields)
        {
            string value = "";
            decimal conf = 0m;
            if (found && input.ValueKind == JsonValueKind.Object)
            {
                if (input.TryGetProperty(f.Name, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    value = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                }
                if (input.TryGetProperty(f.Name + "_confidence", out var c)
                    && c.ValueKind == JsonValueKind.Number)
                {
                    conf = Math.Clamp(c.GetDecimal(), 0m, 1m);
                }
                // If the model returned a value but no confidence, treat as moderate rather than zero.
                else if (value.Length > 0) { conf = 0.6m; }
            }
            result.Fields[f.Name] = new FieldExtraction { Value = value, Confidence = conf };
        }

        return result;
    }

    private Task<HttpResponseMessage> SendAsync(object body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _config.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return SendChecked(req, ct);
    }

    private async Task<HttpResponseMessage> SendChecked(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Anthropic API error {Status}: {Body}", (int)resp.StatusCode, err);
            resp.EnsureSuccessStatusCode();
        }
        return resp;
    }

    // Minimal born-digital text recovery: pull readable text segments out of the PDF stream.
    // Good enough to detect text-PDFs and route them text-first; scanned PDFs return ~nothing
    // and fall through to vision. (A richer extractor can replace this later without touching callers.)
    private static string? TryExtractPdfText(byte[] bytes)
    {
        try
        {
            var raw = Encoding.Latin1.GetString(bytes);
            var matches = Regex.Matches(raw, @"\(((?:\\.|[^()\\])*)\)");
            if (matches.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (Match mt in matches)
            {
                var s = mt.Groups[1].Value.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
                if (s.Trim().Length > 0) sb.Append(s).Append(' ');
            }
            var text = sb.ToString().Trim();
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    private void WarnNoKeyOnce()
    {
        if (_warnedNoKey) return;
        _warnedNoKey = true;
        _logger.LogWarning(
            "AssistantConfig.ApiKey is not set — AnthropicLlmProvider runs in deterministic no-op mode " +
            "(extractions return empty values with confidence 0; no network calls are made).");
    }
}
