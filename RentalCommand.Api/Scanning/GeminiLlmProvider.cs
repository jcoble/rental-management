using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// Google Gemini (AI Studio / Gemini Developer API) implementation of <see cref="ILlmProvider"/>.
/// Talks to <c>generativelanguage.googleapis.com/v1beta/models/{model}:generateContent</c> with the
/// API key in the <c>x-goog-api-key</c> header.
///
/// Like the OpenAI/Anthropic providers it routes born-digital PDFs through text-only extraction
/// (cheaper, more accurate) and forces a single <c>record_extraction</c> function call so the model
/// returns structured JSON plus a self-reported 0–1 confidence per field. Unlike OpenAI Chat
/// Completions, Gemini accepts PDF binaries directly, so scanned PDFs are sent as inline data rather
/// than degraded to a text notice. Falls back to a deterministic no-op (no network call, all
/// confidence 0) when no API key is configured, so the app runs offline.
/// </summary>
public sealed class GeminiLlmProvider : ILlmProvider
{
    private const string ToolName = "record_extraction";

    private readonly HttpClient _http;
    private readonly AssistantConfig _config;
    private readonly ILogger<GeminiLlmProvider> _logger;
    private readonly IImageTextExtractor _imageTextExtractor;
    private bool _warnedNoKey;

    public GeminiLlmProvider(
        HttpClient http,
        IOptions<AssistantConfig> config,
        ILogger<GeminiLlmProvider> logger,
        IImageTextExtractor? imageTextExtractor = null)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
        _imageTextExtractor = imageTextExtractor ?? new TesseractImageTextExtractor();
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
            contents = new[]
            {
                new { role = "user", parts = new object[] { new { text = prompt } } }
            },
            generationConfig = new { maxOutputTokens = 1024 }
        };

        using var resp = await SendChecked(BuildRequest(body), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ExtractText(doc.RootElement) ?? string.Empty;
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
                FailureReason = "AI extraction unavailable (no API key configured)",
                Fields = fields.ToDictionary(
                    f => f.Name,
                    _ => new FieldExtraction { Value = string.Empty, Confidence = 0m })
            };
        }

        var isPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || (documentBytes.Length >= 4 && documentBytes[0] == 0x25 && documentBytes[1] == 0x50
                        && documentBytes[2] == 0x44 && documentBytes[3] == 0x46); // %PDF

        // Text-first routing for born-digital PDFs: if we can recover meaningful text, send it as
        // text (no vision tokens). Scanned/handwritten PDFs yield little text and fall through.
        string? bornDigitalText = isPdf ? PdfTextExtractor.TryExtractText(documentBytes) : null;

        var systemText = new StringBuilder(instructions);
        if (!string.IsNullOrWhiteSpace(groundingContext))
        {
            systemText.Append("\n\nKnown records in this portfolio you may match against (JSON):\n");
            systemText.Append(groundingContext);
        }

        // Build the user turn's parts: either a text block or an inline document (image/PDF).
        object[] userParts;
        if (bornDigitalText is { Length: > 40 })
        {
            userParts = new object[]
            {
                new { text = "Document text follows. Extract the fields.\n\n" + bornDigitalText }
            };
        }
        else
        {
            // Optionally OCR images locally. Legacy text-only mode replaces the vision call; hybrid
            // keeps the image/PDF data authoritative and includes OCR as a secondary hint.
            string? ocrText = null;
            var ocrMode = !isPdf ? ImageOcrRouting.Resolve(_config) : ImageOcrRoutingMode.Disabled;
            if (ocrMode != ImageOcrRoutingMode.Disabled)
                ocrText = _imageTextExtractor.TryExtractText(documentBytes, contentType);

            if (ocrMode == ImageOcrRoutingMode.TextOnly && ImageOcrRouting.IsUseful(ocrText))
            {
                userParts = new object[]
                {
                    new { text = "Document text follows. Extract the fields.\n\n" + ocrText }
                };
            }
            else
            {
                // Gemini accepts images AND PDFs as inline data. Default the mime type to PDF when
                // the bytes are a PDF but the caller passed something generic.
                var mime = isPdf && !contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    ? "application/pdf"
                    : contentType;
                var promptText = ocrMode == ImageOcrRoutingMode.Hybrid && ImageOcrRouting.IsUseful(ocrText)
                    ? ImageOcrRouting.BuildHybridHint(ocrText!)
                    : "Extract the fields from the attached document.";
                userParts = new object[]
                {
                    new { text = promptText },
                    new { inlineData = new { mimeType = mime, data = Convert.ToBase64String(documentBytes) } }
                };
            }
        }

        var body = new
        {
            systemInstruction = new { parts = new object[] { new { text = systemText.ToString() } } },
            contents = new[] { new { role = "user", parts = userParts } },
            tools = new[]
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = ToolName,
                            description = "Return the extracted fields and a self-reported confidence for each.",
                            parameters = BuildInputSchema(fields)
                        }
                    }
                }
            },
            // Force the model to call record_extraction (structured output), mirroring the
            // tool_choice/forced-tool behaviour of the OpenAI and Anthropic providers.
            toolConfig = new
            {
                functionCallingConfig = new { mode = "ANY", allowedFunctionNames = new[] { ToolName } }
            },
            // The receipt schema asks for ~20 fields + a confidence per field + a line-items array;
            // give the structured call ample room so it isn't truncated.
            generationConfig = new { maxOutputTokens = 4096, temperature = 0.0 }
        };

        using var resp = await SendChecked(BuildRequest(body), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseExtraction(json, fields);
    }

    public async Task<LlmToolResult> ChatWithToolsAsync(
        string systemPrompt,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolSpec> tools,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            WarnNoKeyOnce();
            return new LlmToolResult("noop", "AI is unavailable because no API key is configured.",
                Array.Empty<LlmToolCall>(), 0, 0, "noop");
        }

        // Track each requested tool-call id -> name so a later role="tool" result (which carries the
        // id but not the name) can be mapped back to the function name Gemini requires.
        var idToName = new Dictionary<string, string>();
        var contents = new List<object>();

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    contents.Add(new { role = "user", parts = new object[] { new { text = m.Content ?? "" } } });
                    break;

                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    foreach (var tc in m.ToolCalls)
                        if (!string.IsNullOrEmpty(tc.Id)) idToName[tc.Id] = tc.Name;
                    contents.Add(new
                    {
                        role = "model",
                        parts = m.ToolCalls.Select(tc => (object)new
                        {
                            functionCall = new { name = tc.Name, args = ParseJsonObject(tc.ArgumentsJson) }
                        }).ToArray()
                    });
                    break;

                case "assistant":
                    contents.Add(new { role = "model", parts = new object[] { new { text = m.Content ?? "" } } });
                    break;

                case "tool":
                    var fnName = m.ToolCallId is not null && idToName.TryGetValue(m.ToolCallId, out var n) ? n : "tool";
                    contents.Add(new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { functionResponse = new { name = fnName, response = new { result = m.Content ?? "" } } }
                        }
                    });
                    break;

                default:
                    contents.Add(new { role = "user", parts = new object[] { new { text = m.Content ?? "" } } });
                    break;
            }
        }

        var toolDefs = new[]
        {
            new
            {
                functionDeclarations = tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = ParseJsonObject(t.ParametersJsonSchema)
                }).ToArray()
            }
        };

        object body = string.IsNullOrEmpty(systemPrompt)
            ? new
            {
                contents,
                tools = toolDefs,
                generationConfig = new { maxOutputTokens = 1024 }
            }
            : new
            {
                systemInstruction = new { parts = new object[] { new { text = systemPrompt } } },
                contents,
                tools = toolDefs,
                generationConfig = new { maxOutputTokens = 1024 }
            };

        using var resp = await SendChecked(BuildRequest(body), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseChatWithToolsResult(json);
    }

    // -----------------------------------------------------------------------
    // Response parsing
    // -----------------------------------------------------------------------

    private static ExtractedFields ParseExtraction(string responseJson, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var (inputTokens, outputTokens) = ReadUsage(root);
        var result = new ExtractedFields
        {
            ModelId = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() ?? "" : "",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TokensUsed = inputTokens + outputTokens
        };

        // A response cut off by maxOutputTokens (candidate finishReason="MAX_TOKENS") leaves the
        // functionCall args incomplete; flag it so the worker fails the draft instead of storing blanks.
        if (root.TryGetProperty("candidates", out var cands)
            && cands.ValueKind == JsonValueKind.Array
            && cands.GetArrayLength() > 0
            && cands[0].TryGetProperty("finishReason", out var fr)
            && LlmResponseParsing.IsTruncatedFinishReason(fr.GetString()))
        {
            result.Truncated = true;
            result.FailureReason = "response truncated (hit max output tokens)";
        }

        // Find the record_extraction functionCall.args object among the candidate's parts.
        JsonElement args = default;
        var found = false;
        foreach (var part in EnumerateParts(root))
        {
            if (part.TryGetProperty("functionCall", out var fc)
                && fc.TryGetProperty("args", out var a)
                && a.ValueKind == JsonValueKind.Object)
            {
                args = a.Clone();
                found = true;
                break;
            }
        }

        foreach (var f in fields)
        {
            string value = "";
            decimal conf = 0m;
            if (found && args.ValueKind == JsonValueKind.Object)
            {
                if (args.TryGetProperty(f.Name, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    value = f.Type == "array" && v.ValueKind == JsonValueKind.Array
                        ? v.GetRawText()
                        : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                }
                if (args.TryGetProperty(f.Name + "_confidence", out var c) && c.ValueKind == JsonValueKind.Number)
                {
                    conf = Math.Clamp(c.GetDecimal(), 0m, 1m);
                }
                else if (value.Length > 0) { conf = 0.6m; }
            }
            result.Fields[f.Name] = new FieldExtraction { Value = value, Confidence = conf };
        }

        return result;
    }

    private static LlmToolResult ParseChatWithToolsResult(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var (inputTokens, outputTokens) = ReadUsage(root);
        var modelId = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() ?? "" : "";

        var calls = new List<LlmToolCall>();
        var text = new StringBuilder();
        var i = 0;
        foreach (var part in EnumerateParts(root))
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var id = fc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(id)) id = $"{name}-{i}"; // Gemini may omit ids; synthesize one.
                var argsJson = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                calls.Add(new LlmToolCall(id, name, argsJson));
            }
            else if (part.TryGetProperty("text", out var t))
            {
                text.Append(t.GetString());
            }
            i++;
        }

        if (calls.Count > 0)
            return new LlmToolResult("tool_use", null, calls, inputTokens, outputTokens, modelId);

        return new LlmToolResult("end", text.Length > 0 ? text.ToString() : null,
            Array.Empty<LlmToolCall>(), inputTokens, outputTokens, modelId);
    }

    /// <summary>Yields the parts array of the first candidate, or nothing when absent.</summary>
    private static IEnumerable<JsonElement> EnumerateParts(JsonElement root)
    {
        if (root.TryGetProperty("candidates", out var cands)
            && cands.ValueKind == JsonValueKind.Array
            && cands.GetArrayLength() > 0
            && cands[0].TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parts.EnumerateArray())
                yield return p;
        }
    }

    private static string? ExtractText(JsonElement root)
    {
        foreach (var part in EnumerateParts(root))
            if (part.TryGetProperty("text", out var t))
                return t.GetString();
        return null;
    }

    private static (int input, int output) ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usageMetadata", out var u))
        {
            var input = u.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            var output = u.TryGetProperty("candidatesTokenCount", out var ctk) ? ctk.GetInt32() : 0;
            return (input, output);
        }
        return (0, 0);
    }

    // -----------------------------------------------------------------------
    // Schema building (Gemini function-declaration parameters: OpenAPI subset)
    // -----------------------------------------------------------------------

    // Every field PLUS a sibling "<name>_confidence" number 0–1. No additionalProperties
    // (unsupported by Gemini function-declaration schemas).
    private static object BuildInputSchema(IReadOnlyList<ExtractionFieldSpec> fields)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var f in fields)
        {
            props[f.Name] = BuildFieldSchema(f);
            props[f.Name + "_confidence"] = new
            {
                type = "number",
                description = $"Your calibrated confidence 0.0–1.0 that '{f.Name}' is correct. " +
                              "Use 1.0 only for values read verbatim and unambiguous; lower it when guessing or the source is unclear."
            };
            if (f.Required) { required.Add(f.Name); required.Add(f.Name + "_confidence"); }
        }

        return new { type = "object", properties = props, required = required.ToArray() };
    }

    private static object BuildFieldSchema(ExtractionFieldSpec f)
    {
        if (f.Type == "array" && f.ItemFields is { Count: > 0 })
        {
            var itemProps = new Dictionary<string, object>();
            var itemRequired = new List<string>();
            foreach (var item in f.ItemFields)
            {
                itemProps[item.Name] = BuildScalarSchema(item);
                if (item.Required) itemRequired.Add(item.Name);
            }
            return new
            {
                type = "array",
                description = f.Description,
                items = new { type = "object", properties = itemProps, required = itemRequired.ToArray() }
            };
        }
        return BuildScalarSchema(f);
    }

    private static object BuildScalarSchema(ExtractionFieldSpec f) => f.Type switch
    {
        "number" => new { type = "number", description = f.Description },
        "date" => new { type = "string", description = f.Description + " (ISO 8601 date)" },
        "enum" => new { type = "string", @enum = f.EnumValues ?? Array.Empty<string>(), description = f.Description },
        _ => new { type = "string", description = f.Description }
    };

    /// <summary>
    /// Parses a JSON object string into a <see cref="JsonElement"/> so it serialises as a JSON
    /// object (not an escaped string) when embedded in a request body. Empty object on failure.
    /// </summary>
    private static JsonElement ParseJsonObject(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    // -----------------------------------------------------------------------
    // HTTP
    // -----------------------------------------------------------------------

    private HttpRequestMessage BuildRequest(object body)
    {
        var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"v1beta/models/{_config.ModelId}:generateContent")
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            })
        };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", _config.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<HttpResponseMessage> SendChecked(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Gemini API error {Status}: {Body}", (int)resp.StatusCode, err);
            resp.EnsureSuccessStatusCode();
        }
        return resp;
    }

    private void WarnNoKeyOnce()
    {
        if (_warnedNoKey) return;
        _warnedNoKey = true;
        _logger.LogWarning(
            "AssistantConfig.ApiKey is not set — GeminiLlmProvider runs in deterministic no-op mode " +
            "(extractions return empty values with confidence 0; no network calls are made).");
    }
}
