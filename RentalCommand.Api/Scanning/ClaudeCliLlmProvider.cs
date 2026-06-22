using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Scanning;

/// <summary>
/// DEV-ONLY <see cref="ILlmProvider"/> that shells out to the locally-installed Claude Code CLI
/// (<c>claude -p</c>) so document extraction can use the developer's own Claude subscription
/// (Sonnet/Opus) at no per-token cost while testing. Selected by <c>Assistant:Provider == "claude-cli"</c>.
///
/// NOT FOR PRODUCTION. It depends on an interactive Claude Code login on the host and on the
/// <c>claude</c> binary being on PATH; a deployed/customer-facing service must use an API-key provider
/// (openai / anthropic / gemini). The strategy-pattern registration keeps prod and dev on one config
/// switch with no shared fallback path. Because the CLI reads both images and PDFs natively, this
/// provider needs none of the per-API PDF/OCR special-casing the HTTP providers do.
/// </summary>
public sealed class ClaudeCliLlmProvider : ILlmProvider
{
    private const int DefaultTimeoutSeconds = 180;

    private readonly AssistantConfig _config;
    private readonly ILogger<ClaudeCliLlmProvider> _logger;

    public ClaudeCliLlmProvider(IOptions<AssistantConfig> config, ILogger<ClaudeCliLlmProvider> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    // Model alias passed to `claude --model` (e.g. "sonnet", "opus"); falls back to Sonnet.
    private string Model => string.IsNullOrWhiteSpace(_config.ModelId) ? "sonnet" : _config.ModelId!.Trim();

    // The CLI binary. Override with CLAUDE_CLI_PATH if `claude` isn't on the worker's PATH.
    private static string CliPath => Environment.GetEnvironmentVariable("CLAUDE_CLI_PATH") is { Length: > 0 } p ? p : "claude";

    public async Task<string> ChatAsync(string prompt, CancellationToken ct = default)
    {
        var (ok, output, _) = await RunClaudeAsync(prompt, allowFileRead: false, ct);
        return ok ? output.Trim() : string.Empty;
    }

    public async Task<ExtractedFields> ExtractAsync(
        byte[] documentBytes,
        string contentType,
        string instructions,
        IReadOnlyList<ExtractionFieldSpec> fields,
        string? groundingContext = null,
        CancellationToken ct = default)
    {
        // TEXT-FIRST routing — MIRRORS the production HTTP providers and keeps cost down. A born-digital
        // PDF is parsed to text locally (PdfPig) and only that TEXT is sent to the model — the PDF pages
        // are NEVER sent (expensive). Only scanned/photographed PDFs (no extractable text) and images
        // fall through to a vision read of the staged file. This keeps dev representative of prod's cost.
        var isPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || (documentBytes.Length >= 4 && documentBytes[0] == 0x25 && documentBytes[1] == 0x50
                        && documentBytes[2] == 0x44 && documentBytes[3] == 0x46); // %PDF
        var bornDigitalText = isPdf ? PdfTextExtractor.TryExtractText(documentBytes) : null;

        if (bornDigitalText is { Length: > 40 })
        {
            _logger.LogInformation(
                "claude-cli extraction: TEXT-FIRST (born-digital PDF, {Chars} chars of text — no PDF/vision sent).",
                bornDigitalText.Length);
            var textPrompt = BuildTextPrompt(bornDigitalText, instructions, fields, groundingContext);
            var (tok, tout, terr) = await RunClaudeAsync(textPrompt, allowFileRead: false, ct);
            return tok
                ? ParseExtraction(tout, fields)
                : Failed(fields, $"claude-cli text extraction failed: {Truncate(terr, 300)}");
        }

        // Vision fallback: a scanned/photographed PDF (no text) or an image — stage the file and let the
        // CLI read it. Same shape as a production vision call.
        _logger.LogInformation("claude-cli extraction: VISION read ({ContentType}, no extractable text).", contentType);
        var tempPath = Path.Combine(Path.GetTempPath(), $"rc-scan-{Guid.NewGuid():N}{ExtensionFor(contentType)}");
        try
        {
            await File.WriteAllBytesAsync(tempPath, documentBytes, ct);

            var prompt = BuildExtractionPrompt(tempPath, instructions, fields, groundingContext);
            var (ok, output, error) = await RunClaudeAsync(prompt, allowFileRead: true, ct);

            return ok
                ? ParseExtraction(output, fields)
                : Failed(fields, $"claude-cli extraction failed: {Truncate(error, 300)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "claude-cli extraction threw.");
            return Failed(fields, $"claude-cli extraction error: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    public async Task<LlmToolResult> ChatWithToolsAsync(
        string systemPrompt,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolSpec> tools,
        CancellationToken ct = default)
    {
        var prompt = tools.Count == 0
            ? BuildPlainChatPrompt(systemPrompt, messages)
            : messages.Any(m => m.Role == "tool")
                ? BuildToolAnswerPrompt(systemPrompt, messages)
                : BuildToolSelectionPrompt(systemPrompt, messages, tools);

        var (ok, output, error) = await RunClaudeAsync(prompt, allowFileRead: false, ct);
        if (!ok)
        {
            return new LlmToolResult(
                "error",
                $"AI is temporarily unavailable: {Truncate(error, 300)}",
                Array.Empty<LlmToolCall>(),
                0,
                0,
                $"claude-cli:{Model}");
        }

        if (tools.Count > 0 && !messages.Any(m => m.Role == "tool"))
        {
            return ParseToolProtocolResponse(
                output,
                tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
                $"claude-cli:{Model}");
        }

        return new LlmToolResult(ok ? "end" : "error", ok ? output.Trim() : null,
            Array.Empty<LlmToolCall>(), 0, 0, $"claude-cli:{Model}");
    }

    // ------------------------------------------------------------------ helpers

    private static string BuildPlainChatPrompt(string systemPrompt, IReadOnlyList<LlmChatMessage> messages)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) sb.AppendLine(systemPrompt).AppendLine();
        AppendConversation(sb, messages);
        return sb.ToString();
    }

    private static string BuildToolSelectionPrompt(
        string systemPrompt,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolSpec> tools)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) sb.AppendLine(systemPrompt).AppendLine();
        sb.AppendLine("You are running inside a local application server. You cannot call external tools,");
        sb.AppendLine("Claude Code tools, Notion tools, browser tools, shell commands, or databases directly.");
        sb.AppendLine("The only data access available to you is through the application tools listed below.");
        sb.AppendLine("If the user asks about live portfolio records, choose the relevant listed tool before answering.");
        sb.AppendLine();
        sb.AppendLine("Conversation:");
        AppendConversation(sb, messages);
        sb.AppendLine();
        sb.AppendLine("Available application tools:");
        foreach (var tool in tools)
        {
            sb.Append("- ").Append(tool.Name).Append(": ").AppendLine(tool.Description);
            sb.Append("  parameters JSON schema: ").AppendLine(tool.ParametersJsonSchema);
        }
        sb.AppendLine();
        sb.AppendLine("Return ONLY one compact JSON object, with no prose and no markdown fences.");
        sb.AppendLine("To call tools, use:");
        sb.AppendLine("""{"tool_calls":[{"id":"call-1","name":"tool_name","arguments":{}}]}""");
        sb.AppendLine("Use only listed tool names. arguments must be a JSON object.");
        sb.AppendLine("If no live application data is needed, use:");
        sb.AppendLine("""{"answer":"plain English answer"}""");
        return sb.ToString();
    }

    private static string BuildToolAnswerPrompt(string systemPrompt, IReadOnlyList<LlmChatMessage> messages)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) sb.AppendLine(systemPrompt).AppendLine();
        sb.AppendLine("Answer the latest user question using ONLY the application tool result messages below.");
        sb.AppendLine("Do not say you lack database access; the tool results are the database-backed app data.");
        sb.AppendLine("If the tool result is empty, say there were no matching records.");
        sb.AppendLine("Return plain English only, with no JSON and no markdown table.");
        sb.AppendLine();
        AppendConversation(sb, messages);
        return sb.ToString();
    }

    private static void AppendConversation(StringBuilder sb, IReadOnlyList<LlmChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                sb.Append(message.Role).Append(": ").AppendLine(message.Content);
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    sb.Append("assistant tool call ")
                        .Append(call.Id)
                        .Append(": ")
                        .Append(call.Name)
                        .Append(' ')
                        .AppendLine(call.ArgumentsJson);
                }
            }

            if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
            {
                sb.Append("tool_call_id: ").AppendLine(message.ToolCallId);
            }
        }
    }

    internal static LlmToolResult ParseToolProtocolResponse(
        string output,
        IReadOnlySet<string> allowedToolNames,
        string modelId)
    {
        var text = StripFences(output);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return new LlmToolResult("end", text.Trim(), Array.Empty<LlmToolCall>(), 0, 0, modelId);
        }

        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            var root = doc.RootElement;

            if (TryGetToolCalls(root, out var toolCallsElement))
            {
                var calls = new List<LlmToolCall>();
                var index = 1;
                foreach (var item in toolCallsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var name = nameElement.GetString();
                    if (string.IsNullOrWhiteSpace(name) ||
                        !allowedToolNames.Contains(name))
                    {
                        continue;
                    }

                    var id = item.TryGetProperty("id", out var idElement) &&
                             idElement.ValueKind == JsonValueKind.String &&
                             !string.IsNullOrWhiteSpace(idElement.GetString())
                        ? idElement.GetString()!
                        : $"call-{index}";

                    var argsJson = "{}";
                    if (item.TryGetProperty("arguments", out var argsElement))
                    {
                        argsJson = argsElement.ValueKind switch
                        {
                            JsonValueKind.Object => argsElement.GetRawText(),
                            JsonValueKind.String => NormalizeArgumentsJson(argsElement.GetString()),
                            _ => "{}",
                        };
                    }

                    calls.Add(new LlmToolCall(id, name, argsJson));
                    index++;
                }

                if (calls.Count > 0)
                {
                    return new LlmToolResult("tool_use", null, calls, 0, 0, modelId);
                }
            }

            if (root.TryGetProperty("answer", out var answerElement) &&
                answerElement.ValueKind == JsonValueKind.String)
            {
                return new LlmToolResult(
                    "end",
                    answerElement.GetString()?.Trim(),
                    Array.Empty<LlmToolCall>(),
                    0,
                    0,
                    modelId);
            }
        }
        catch (JsonException)
        {
            return new LlmToolResult("end", text.Trim(), Array.Empty<LlmToolCall>(), 0, 0, modelId);
        }

        return new LlmToolResult("end", text.Trim(), Array.Empty<LlmToolCall>(), 0, 0, modelId);
    }

    private static bool TryGetToolCalls(JsonElement root, out JsonElement toolCalls)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("tool_calls", out toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("toolCalls", out toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        toolCalls = default;
        return false;
    }

    private static string NormalizeArgumentsJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";

        var text = LlmResponseParsing.StripCodeFences(raw);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.GetRawText()
                : "{}";
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private string BuildExtractionPrompt(
        string filePath, string instructions, IReadOnlyList<ExtractionFieldSpec> fields, string? grounding)
    {
        var sb = new StringBuilder();
        sb.Append("Read the document file at ").Append(filePath)
          .AppendLine(" (it may be a multi-page PDF or a photographed scan) and extract the fields below.");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(instructions)) sb.AppendLine(instructions).AppendLine();

        sb.AppendLine("Fields (name — type — meaning):");
        foreach (var f in fields)
        {
            var en = f.EnumValues is { Count: > 0 } ? $" (one of: {string.Join(", ", f.EnumValues)})" : "";
            sb.Append("- ").Append(f.Name).Append(" — ").Append(f.Type).Append(en)
              .Append(" — ").AppendLine(f.Description);
        }

        if (!string.IsNullOrWhiteSpace(grounding))
        {
            sb.AppendLine();
            sb.AppendLine("Known records already in this portfolio you may match against (JSON):");
            sb.AppendLine(grounding);
        }

        sb.AppendLine();
        sb.AppendLine("Return ONLY a single compact JSON object — no prose, no markdown fences. For EACH field");
        sb.AppendLine("above include its value (use \"\" if absent) PLUS a sibling \"<name>_confidence\" number");
        sb.AppendLine("from 0.0 to 1.0 reflecting how sure you are. Use ISO 8601 (YYYY-MM-DD) for dates.");
        return sb.ToString();
    }

    // Text-first prompt: the PDF was already parsed to text locally, so we send only the text (no file
    // read, no vision) — the cheap path that mirrors how production extracts born-digital PDFs.
    private string BuildTextPrompt(
        string documentText, string instructions, IReadOnlyList<ExtractionFieldSpec> fields, string? grounding)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract the fields below from the following document text (already extracted from the PDF):");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(instructions)) sb.AppendLine(instructions).AppendLine();
        sb.AppendLine("--- DOCUMENT TEXT ---");
        sb.AppendLine(documentText);
        sb.AppendLine("--- END DOCUMENT TEXT ---");
        sb.AppendLine();
        sb.AppendLine("Fields (name — type — meaning):");
        foreach (var f in fields)
        {
            var en = f.EnumValues is { Count: > 0 } ? $" (one of: {string.Join(", ", f.EnumValues)})" : "";
            sb.Append("- ").Append(f.Name).Append(" — ").Append(f.Type).Append(en)
              .Append(" — ").AppendLine(f.Description);
        }
        if (!string.IsNullOrWhiteSpace(grounding))
        {
            sb.AppendLine();
            sb.AppendLine("Known records already in this portfolio you may match against (JSON):");
            sb.AppendLine(grounding);
        }
        sb.AppendLine();
        sb.AppendLine("Return ONLY a single compact JSON object — no prose, no markdown fences. For EACH field");
        sb.AppendLine("above include its value (use \"\" if absent) PLUS a sibling \"<name>_confidence\" number");
        sb.AppendLine("from 0.0 to 1.0. Use ISO 8601 (YYYY-MM-DD) for dates.");
        return sb.ToString();
    }

    private async Task<(bool ok, string output, string error)> RunClaudeAsync(
        string prompt, bool allowFileRead, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Run in a neutral dir so the CLI doesn't load this repo's CLAUDE.md/project memory into
            // every extraction (faster, and keeps the prompt focused). The temp document is read by
            // absolute path regardless.
            WorkingDirectory = Path.GetTempPath(),
        };
        // Prompt is positional FIRST (a trailing variadic flag like --allowed-tools would otherwise eat it).
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(Model);
        if (allowFileRead)
        {
            // Scope the spawned CLI to READ-ONLY: pre-approve only the Read tool so it can load the
            // staged temp document (which lives in the working dir set above) without an interactive
            // prompt — and CANNOT run bash, edit, or anything else. Deliberately NOT
            // --dangerously-skip-permissions (that would bypass all gates). Variadic flag goes last so
            // it doesn't swallow the positional prompt.
            psi.ArgumentList.Add("--allowed-tools");
            psi.ArgumentList.Add("Read");
        }

        try
        {
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return (false, "", "could not start the claude CLI");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "", $"claude CLI timed out after {DefaultTimeoutSeconds}s");
            }

            var output = await stdoutTask;
            var error = await stderrTask;
            return (proc.ExitCode == 0, output, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "claude CLI not found ({Cli}); set CLAUDE_CLI_PATH or install Claude Code.", CliPath);
            return (false, "", $"claude CLI not found ({CliPath}): {ex.Message}");
        }
    }

    private ExtractedFields ParseExtraction(string output, IReadOnlyList<ExtractionFieldSpec> fields)
    {
        var result = new ExtractedFields { ModelId = $"claude-cli:{Model}" };

        // claude -p prints just the model's answer; isolate the JSON object within it.
        var text = StripFences(output);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            foreach (var f in fields) result.Fields[f.Name] = new FieldExtraction { Value = "", Confidence = 0m };
            result.FailureReason = "claude-cli returned no JSON object";
            return result;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text.Substring(start, end - start + 1));
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            foreach (var f in fields) result.Fields[f.Name] = new FieldExtraction { Value = "", Confidence = 0m };
            result.FailureReason = "claude-cli JSON was not parseable";
            return result;
        }

        foreach (var f in fields)
        {
            var value = "";
            var conf = 0m;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(f.Name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                value = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.GetRawText();
            }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(f.Name + "_confidence", out var c) && c.ValueKind == JsonValueKind.Number)
            {
                conf = Math.Clamp(c.GetDecimal(), 0m, 1m);
            }
            else if (value.Length > 0)
            {
                conf = 0.9m; // Sonnet/Opus are reliable; default high when a value was returned without a score.
            }
            result.Fields[f.Name] = new FieldExtraction { Value = value, Confidence = conf };
        }
        return result;
    }

    private static ExtractedFields Failed(IReadOnlyList<ExtractionFieldSpec> fields, string reason)
    {
        var r = new ExtractedFields { ModelId = "claude-cli", FailureReason = reason };
        foreach (var f in fields) r.Fields[f.Name] = new FieldExtraction { Value = "", Confidence = 0m };
        return r;
    }

    private static string ExtensionFor(string? contentType)
    {
        var c = contentType?.ToLowerInvariant() ?? "";
        if (c.Contains("pdf")) return ".pdf";
        if (c.Contains("png")) return ".png";
        if (c.Contains("jpeg") || c.Contains("jpg")) return ".jpg";
        if (c.Contains("heic")) return ".heic";
        if (c.Contains("webp")) return ".webp";
        return ".bin";
    }

    private static string StripFences(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            if (s.EndsWith("```")) s = s[..^3];
        }
        return s.Trim();
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);
}
