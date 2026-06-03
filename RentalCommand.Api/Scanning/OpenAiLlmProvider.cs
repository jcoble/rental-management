using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// OpenAI Chat Completions API implementation of <see cref="ILlmProvider"/>. Routes born-digital
/// PDFs through text-only extraction (cheaper, more accurate) and images through a vision
/// image_url block. Forces a function-call response so the model returns structured JSON plus a
/// self-reported 0–1 confidence per field. Scanned PDFs that yield no extractable text are sent
/// with a plain-text notice (OpenAI Chat Completions does not accept PDF binaries via image_url).
/// Falls back to a deterministic no-op (no network call, all confidence 0) when no API key is
/// configured, so the app runs offline.
/// </summary>
public sealed class OpenAiLlmProvider : ILlmProvider
{
    private const string ToolName = "record_extraction";

    private readonly HttpClient _http;
    private readonly AssistantConfig _config;
    private readonly ILogger<OpenAiLlmProvider> _logger;
    private bool _warnedNoKey;

    public OpenAiLlmProvider(HttpClient http, IOptions<AssistantConfig> config, ILogger<OpenAiLlmProvider> logger)
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
            max_completion_tokens = 1024,
            messages = new[] { new { role = "user", content = prompt } }
        };
        using var resp = await SendChecked(BuildRequest(body), ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
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
        // (no vision tokens). Scanned/handwritten PDFs yield little text and fall through below.
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
            // Born-digital PDF — send as plain text (no vision tokens needed).
            userContent = "Document text follows. Extract the fields.\n\n" + bornDigitalText;
        }
        else if (isPdf)
        {
            // Scanned PDF: OpenAI Chat Completions does not accept PDF binaries via image_url.
            // Send a best-effort notice; the model can still extract from context/grounding.
            userContent = "The attached document is a scanned PDF with no extractable text. " +
                          "Please extract whatever fields you can from the available context.";
        }
        else
        {
            // Image — optionally OCR locally first (cheap — avoids vision tokens); fall back to
            // the vision image_url block if OCR is disabled, unavailable, or yields too little text.
            string? ocrText = null;
            if (_config.UseImageOcr)
                ocrText = ImageTextExtractor.TryExtractText(documentBytes, contentType);

            if (ocrText is { Length: > 0 })
            {
                userContent = "Document text follows. Extract the fields.\n\n" + ocrText;
            }
            else
            {
                var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(documentBytes)}";
                var imageDetail = string.IsNullOrWhiteSpace(_config.ImageDetail) ? "low" : _config.ImageDetail;
                userContent = new object[]
                {
                    new { type = "text", text = "Extract the fields from the attached document." },
                    new
                    {
                        type = "image_url",
                        image_url = new { url = dataUri, detail = imageDetail }
                    }
                };
            }
        }

        var functionTool = new
        {
            type = "function",
            function = new
            {
                name = ToolName,
                description = "Return the extracted fields and a self-reported confidence for each.",
                parameters = BuildInputSchema(fields)
            }
        };

        var body = new
        {
            model = _config.ModelId,
            // The receipt schema asks for ~20 fields, a confidence per field, AND a line-items
            // array — at 1500 the tool-call JSON gets truncated (the model hits the cap before
            // finishing the function call), yielding an empty extraction. 4096 gives ample room.
            max_completion_tokens = 4096,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt.ToString() },
                new { role = "user",   content = userContent }
            },
            tools = new[] { functionTool },
            tool_choice = new { type = "function", function = new { name = ToolName } }
        };

        using var resp = await SendChecked(BuildRequest(body), ct);
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

        // Build the messages array for chat/completions.
        var msgList = new List<object>();

        if (!string.IsNullOrEmpty(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "user":
                    msgList.Add(new { role = "user", content = m.Content });
                    break;

                case "assistant" when m.ToolCalls is { Count: > 0 }:
                    msgList.Add(new
                    {
                        role = "assistant",
                        tool_calls = m.ToolCalls.Select(tc => new
                        {
                            id   = tc.Id,
                            type = "function",
                            function = new { name = tc.Name, arguments = tc.ArgumentsJson }
                        }).ToArray()
                    });
                    break;

                case "assistant":
                    msgList.Add(new { role = "assistant", content = m.Content });
                    break;

                case "tool":
                    msgList.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content });
                    break;

                default:
                    msgList.Add(new { role = m.Role, content = m.Content });
                    break;
            }
        }

        // Build the tools array. Parse the JSON-schema string so it serialises as a JSON object.
        var toolDefs = tools.Select(t => new
        {
            type     = "function",
            function = new
            {
                name        = t.Name,
                description = t.Description,
                parameters  = ParseJsonSchema(t.ParametersJsonSchema)
            }
        }).ToArray();

        var body = new
        {
            model                 = _config.ModelId,
            max_completion_tokens = 1024,
            messages              = msgList,
            tools                 = toolDefs,
            tool_choice           = "auto"
        };

        using var resp = await SendChecked(BuildRequest(body), ct);
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
            if (u.TryGetProperty("prompt_tokens",     out var pt))  inputTokens  = pt.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var ct2)) outputTokens = ct2.GetInt32();
        }
        var modelId = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return new LlmToolResult("error", null, Array.Empty<LlmToolCall>(),
                inputTokens, outputTokens, modelId);
        }

        var message = choices[0].GetProperty("message");

        // Tool calls path.
        if (message.TryGetProperty("tool_calls", out var toolCallsEl)
            && toolCallsEl.ValueKind == JsonValueKind.Array
            && toolCallsEl.GetArrayLength() > 0)
        {
            var calls = new List<LlmToolCall>();
            foreach (var tc in toolCallsEl.EnumerateArray())
            {
                var id       = tc.TryGetProperty("id",       out var tid)  ? tid.GetString()  ?? "" : "";
                var fn       = tc.GetProperty("function");
                var name     = fn.TryGetProperty("name",      out var tn)   ? tn.GetString()   ?? "" : "";
                var argsJson = fn.TryGetProperty("arguments", out var targs) ? targs.GetString() ?? "{}" : "{}";
                calls.Add(new LlmToolCall(id, name, argsJson));
            }
            return new LlmToolResult("tool_use", null, calls, inputTokens, outputTokens, modelId);
        }

        // Text-answer path.
        var text = message.TryGetProperty("content", out var c) ? c.GetString() : null;
        return new LlmToolResult("end", text, Array.Empty<LlmToolCall>(),
            inputTokens, outputTokens, modelId);
    }

    /// <summary>
    /// Parses a JSON Schema string into a <see cref="JsonElement"/> so it serialises
    /// as a JSON object rather than an escaped string when embedded in a request body.
    /// Falls back to an empty object on parse failure.
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

    // ---- OpenAI function schema: every field PLUS a sibling "<name>_confidence" number 0–1 ----
    private static object BuildInputSchema(IReadOnlyList<ExtractionFieldSpec> fields)
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
            type = "object",
            properties = props,
            required = required.ToArray(),
            additionalProperties = false
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
            if (u.TryGetProperty("prompt_tokens",     out var pt))  inputTokens  = pt.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var ct2)) outputTokens = ct2.GetInt32();
        }
        var result = new ExtractedFields
        {
            ModelId      = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            InputTokens  = inputTokens,
            OutputTokens = outputTokens,
            TokensUsed   = inputTokens + outputTokens
        };

        // Find tool_calls[0].function.arguments — a JSON *string* containing the input object.
        JsonElement input = default;
        var found = false;
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var msg = choices[0].GetProperty("message");
            if (msg.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array
                && toolCalls.GetArrayLength() > 0)
            {
                var argsJson = toolCalls[0]
                    .GetProperty("function")
                    .GetProperty("arguments")
                    .GetString();

                if (!string.IsNullOrEmpty(argsJson))
                {
                    // arguments is a JSON string — parse it and clone so it outlives the using block.
                    using var argsDoc = JsonDocument.Parse(argsJson);
                    input = argsDoc.RootElement.Clone();
                    found = true;
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

    private HttpRequestMessage BuildRequest(object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<HttpResponseMessage> SendChecked(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("OpenAI API error {Status}: {Body}", (int)resp.StatusCode, err);
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
            "AssistantConfig.ApiKey is not set — OpenAiLlmProvider runs in deterministic no-op mode " +
            "(extractions return empty values with confidence 0; no network calls are made).");
    }
}
