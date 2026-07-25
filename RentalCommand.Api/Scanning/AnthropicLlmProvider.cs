using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
public sealed class AnthropicLlmProvider :
    ILlmProvider,
    ILlmCredentialProbe,
    IWorkspaceLlmExtractionProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const string ToolName = "record_extraction";

    private readonly HttpClient _http;
    private readonly AssistantConfig _config;
    private readonly ILogger<AnthropicLlmProvider> _logger;
    private readonly IImageTextExtractor _imageTextExtractor;
    private bool _warnedNoKey;

    public string ProviderKey => "anthropic";

    public AnthropicLlmProvider(
        HttpClient http,
        IOptions<AssistantConfig> config,
        ILogger<AnthropicLlmProvider> logger,
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
            model = _config.ModelId,
            max_tokens = 1024,
            messages = new[] { new { role = "user", content = prompt } }
        };
        using var resp = await SendAsync(body, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
        return text ?? string.Empty;
    }

    public async Task<LlmCredentialProbeResult> TestCredentialAsync(
        string apiKey,
        string modelId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelId))
        {
            return new(false, ProviderKey, modelId, "API key and model are required.");
        }

        var body = new
        {
            model = modelId.Trim(),
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "Reply OK." } }
        };
        using var request = BuildRequest(body, apiKey.Trim());
        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? new(true, ProviderKey, modelId.Trim())
            : new(false, ProviderKey, modelId.Trim(),
                $"Anthropic rejected the credential ({(int)response.StatusCode}).");
    }

    public Task<ExtractedFields> ExtractAsync(
        byte[] documentBytes,
        string contentType,
        string instructions,
        IReadOnlyList<ExtractionFieldSpec> fields,
        string? groundingContext = null,
        CancellationToken ct = default) =>
        ExtractCoreAsync(
            _config.ApiKey,
            _config.ModelId,
            documentBytes,
            contentType,
            instructions,
            fields,
            groundingContext,
            ct);

    public Task<ExtractedFields> ExtractWorkspaceAsync(
        WorkspaceLlmRuntimeCredential credential,
        byte[] documentBytes,
        string contentType,
        string instructions,
        IReadOnlyList<ExtractionFieldSpec> fields,
        string? groundingContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!string.Equals(credential.Provider, ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The workspace credential selects {credential.Provider}, not {ProviderKey}.");
        }

        return ExtractCoreAsync(
            credential.ApiKey,
            credential.ModelId,
            documentBytes,
            contentType,
            instructions,
            fields,
            groundingContext,
            ct);
    }

    private async Task<ExtractedFields> ExtractCoreAsync(
        string? apiKey,
        string? modelId,
        byte[] documentBytes,
        string contentType,
        string instructions,
        IReadOnlyList<ExtractionFieldSpec> fields,
        string? groundingContext,
        CancellationToken ct)
    {
        // --- Deterministic no-op fallback (offline / unconfigured) ---
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelId))
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
            // For images (or scanned PDFs that yielded no born-digital text): optionally OCR locally
            // first. Legacy text-only mode replaces the vision call; hybrid sends OCR as a hint while
            // keeping the image authoritative for receipt tables and lease labels local OCR often misses.
            string? ocrText = null;
            var ocrMode = !isPdf ? ImageOcrRouting.Resolve(_config) : ImageOcrRoutingMode.Disabled;
            if (ocrMode != ImageOcrRoutingMode.Disabled)
                ocrText = _imageTextExtractor.TryExtractText(documentBytes, contentType);

            if (ocrMode == ImageOcrRoutingMode.TextOnly && ImageOcrRouting.IsUseful(ocrText))
            {
                userContent = new object[]
                {
                    new { type = "text", text = "Document text follows. Extract the fields.\n\n" + ocrText }
                };
            }
            else
            {
                var mediaType = isPdf ? "application/pdf" : contentType;
                var blockType = isPdf ? "document" : "image";
                var promptText = ocrMode == ImageOcrRoutingMode.Hybrid && ImageOcrRouting.IsUseful(ocrText)
                    ? ImageOcrRouting.BuildHybridHint(ocrText!)
                    : "Extract the fields from the attached document.";
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
                    new { type = "text", text = promptText }
                };
            }
        }

        var body = new
        {
            model = modelId,
            // The receipt schema asks for ~24 fields, a confidence per field, AND a line-items
            // array — at 1500 the tool_use JSON gets truncated (the model hits the cap before
            // finishing the tool call), yielding an empty extraction. 4096 gives ample room.
            max_tokens = 4096,
            system = systemPrompt.ToString(),
            tools = new[] { BuildTool(fields) },
            tool_choice = new { type = "tool", name = ToolName },
            messages = new[] { new { role = "user", content = userContent } }
        };

        using var request = BuildRequest(body, apiKey);
        using var resp = await SendChecked(request, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseToolResult(json, fields);
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

        // Build Anthropic-style tool definitions.
        var toolDefs = tools.Select(t => new
        {
            name         = t.Name,
            description  = t.Description,
            input_schema = ParseJsonSchema(t.ParametersJsonSchema)
        }).ToArray();

        // Convert messages to Anthropic content blocks.
        var msgList = new List<object>();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    msgList.Add(new
                    {
                        role    = "user",
                        content = new object[] { new { type = "text", text = m.Content } }
                    });
                    break;

                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    msgList.Add(new
                    {
                        role    = "assistant",
                        content = m.ToolCalls.Select(tc => (object)new
                        {
                            type  = "tool_use",
                            id    = tc.Id,
                            name  = tc.Name,
                            input = ParseJsonNode(tc.ArgumentsJson)
                        }).ToArray()
                    });
                    break;

                case "assistant":
                    msgList.Add(new
                    {
                        role    = "assistant",
                        content = new object[] { new { type = "text", text = m.Content } }
                    });
                    break;

                case "tool":
                    // Tool results are sent as user turns in the Anthropic protocol.
                    msgList.Add(new
                    {
                        role    = "user",
                        content = new object[]
                        {
                            new { type = "tool_result", tool_use_id = m.ToolCallId, content = m.Content }
                        }
                    });
                    break;

                default:
                    msgList.Add(new
                    {
                        role    = m.Role,
                        content = new object[] { new { type = "text", text = m.Content } }
                    });
                    break;
            }
        }

        var body = new
        {
            model      = _config.ModelId,
            max_tokens = 1024,
            system     = systemPrompt,
            messages   = msgList,
            tools      = toolDefs
        };

        using var resp = await SendAsync(body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseChatWithToolsResult(json);
    }

    private static LlmToolResult ParseChatWithToolsResult(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens",  out var it)) inputTokens  = it.GetInt32();
            if (u.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }
        var modelId    = root.TryGetProperty("model",       out var m)  ? m.GetString()  ?? "" : "";
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";

        if (!root.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return new LlmToolResult("error", null, Array.Empty<LlmToolCall>(),
                inputTokens, outputTokens, modelId);
        }

        var toolCalls = new List<LlmToolCall>();
        var textParts = new List<string>();

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeEl)) continue;
            var blockType = typeEl.GetString();

            if (blockType == "tool_use")
            {
                var id   = block.TryGetProperty("id",   out var bid)  ? bid.GetString()  ?? "" : "";
                var name = block.TryGetProperty("name", out var bname) ? bname.GetString() ?? "" : "";
                // Anthropic returns "input" as a JSON object; capture it as a raw JSON string.
                var argsJson = block.TryGetProperty("input", out var inp)
                    ? inp.GetRawText()
                    : "{}";
                toolCalls.Add(new LlmToolCall(id, name, argsJson));
            }
            else if (blockType == "text")
            {
                if (block.TryGetProperty("text", out var t))
                    textParts.Add(t.GetString() ?? "");
            }
        }

        if (toolCalls.Count > 0)
            return new LlmToolResult("tool_use", null, toolCalls, inputTokens, outputTokens, modelId);

        var finalText = textParts.Count > 0 ? string.Join("\n", textParts) : null;
        // Normalise Anthropic's "end_turn" to "end".
        var resolvedStop = stopReason is "end_turn" or "end" ? "end" : stopReason;
        return new LlmToolResult(resolvedStop, finalText, Array.Empty<LlmToolCall>(),
            inputTokens, outputTokens, modelId);
    }

    /// <summary>
    /// Parses a JSON Schema string into a <see cref="JsonElement"/> for embedding as a
    /// JSON object in a request body. Falls back to an empty object on parse failure.
    /// </summary>
    private static JsonElement ParseJsonSchema(string jsonSchema)
    {
        try
        {
            return JsonDocument.Parse(jsonSchema).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    /// <summary>
    /// Parses a JSON string into a <see cref="JsonElement"/> so that it serialises as a
    /// JSON object (used for Anthropic's <c>input</c> field on <c>tool_use</c> blocks).
    /// Falls back to an empty object on parse failure.
    /// </summary>
    private static JsonElement ParseJsonNode(string json)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    // ---- Anthropic tool schema: every field PLUS a sibling "<name>_confidence" number 0–1 ----
    private static object BuildTool(IReadOnlyList<ExtractionFieldSpec> fields)
    {
        var props = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var f in fields)
        {
            object schema = BuildFieldSchema(f);
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
        "date"   => new { type = "string", description = f.Description + " (ISO 8601 date)" },
        "enum"   => new { type = "string", @enum = f.EnumValues ?? Array.Empty<string>(), description = f.Description },
        _        => new { type = "string", description = f.Description }
    };

    private static ExtractedFields ParseToolResult(string responseJson, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("input_tokens",  out var it)) inputTokens  = it.GetInt32();
            if (u.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
        }
        var result = new ExtractedFields
        {
            ModelId      = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            TokensUsed   = inputTokens + outputTokens
        };

        // A response cut off by max_tokens (stop_reason="max_tokens") leaves the tool_use input
        // incomplete; flag it so the worker fails the draft instead of storing blanks.
        if (root.TryGetProperty("stop_reason", out var sr)
            && LlmResponseParsing.IsTruncatedFinishReason(sr.GetString()))
        {
            result.Truncated = true;
            result.FailureReason = "response truncated (hit max output tokens)";
        }

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
                    // For array fields: store the raw JSON of the returned array so it round-trips as a JSON string.
                    value = f.Type == "array" && v.ValueKind == JsonValueKind.Array
                        ? v.GetRawText()
                        : v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
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
        return SendChecked(BuildRequest(body), ct);
    }

    private HttpRequestMessage BuildRequest(object body, string? apiKey = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey ?? _config.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
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

    private static string? TryExtractPdfText(byte[] bytes) =>
        PdfTextExtractor.TryExtractText(bytes);

    private void WarnNoKeyOnce()
    {
        if (_warnedNoKey) return;
        _warnedNoKey = true;
        _logger.LogWarning(
            "AssistantConfig.ApiKey is not set — AnthropicLlmProvider runs in deterministic no-op mode " +
            "(extractions return empty values with confidence 0; no network calls are made).");
    }
}
