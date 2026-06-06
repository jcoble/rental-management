using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Voice;

public sealed class VoiceIntakeService : IVoiceIntakeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RentalCommandDbContext _db;
    private readonly ILlmProvider _llm;
    private readonly IAudioTranscriptionService _transcriber;
    private readonly IFileStorage _storage;
    private readonly ILogger<VoiceIntakeService> _logger;

    public VoiceIntakeService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        IAudioTranscriptionService transcriber,
        IFileStorage storage,
        ILogger<VoiceIntakeService> logger)
    {
        _db = db;
        _llm = llm;
        _transcriber = transcriber;
        _storage = storage;
        _logger = logger;
    }

    public async Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] audioBytes,
        string? contentType,
        string? providedTranscript,
        CancellationToken ct = default)
    {
        var transcript = string.IsNullOrWhiteSpace(providedTranscript)
            ? await _transcriber.TranscribeAsync(audioBytes, contentType ?? "application/octet-stream", "voice.webm", ct)
            : providedTranscript.Trim();

        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new ArgumentException("A transcript or non-empty transcribable audio is required.");
        }

        var filePath = $"voice://{Guid.NewGuid():N}";
        if (audioBytes.Length > 0)
        {
            filePath = await _storage.UploadAsync(
                new MemoryStream(audioBytes),
                $"voice-{DateTime.UtcNow:yyyyMMddHHmmss}.webm",
                contentType ?? "application/octet-stream",
                ct);

            _db.StoredFiles.Add(new StoredFile
            {
                PortfolioId = portfolioId,
                FileName = "voice.webm",
                FilePath = filePath,
                ContentType = contentType ?? "application/octet-stream",
                FileSize = audioBytes.Length,
                EntityType = "ScanDraft",
                EntityId = null,
                UploadedAt = DateTime.UtcNow,
            });
        }

        var classification = await ClassifyTranscriptAsync(portfolioId, transcript, ct);

        var draft = new ScanDraft
        {
            PortfolioId = portfolioId,
            FilePath = filePath,
            TargetEntityType = classification.TargetEntityType,
            Status = "Reviewing",
            ExtractedFields = classification.ExtractedFieldsJson,
            ModelId = classification.ModelId,
            TokensUsed = classification.TokensUsed,
            CreatedAt = DateTime.UtcNow,
            ReviewedAt = DateTime.UtcNow,
        };

        _db.ScanDrafts.Add(draft);
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<ScanDraft> AnswerAsync(
        int portfolioId,
        int draftId,
        byte[] audioBytes,
        string? contentType,
        string? providedTranscript,
        CancellationToken ct = default)
    {
        var draft = await _db.ScanDrafts
            .FirstOrDefaultAsync(d => d.Id == draftId && d.PortfolioId == portfolioId, ct)
            ?? throw new KeyNotFoundException($"Voice draft {draftId} not found.");

        var answer = string.IsNullOrWhiteSpace(providedTranscript)
            ? await _transcriber.TranscribeAsync(audioBytes, contentType ?? "application/octet-stream", "voice.webm", ct)
            : providedTranscript.Trim();

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("A transcript or non-empty transcribable audio is required.");
        }

        // Append the answer to the running transcript and re-classify the whole
        // conversation. The model now sees the full context (e.g. the amount it
        // was missing), so a single re-classify fills the new slot.
        var prior = ExtractTranscript(draft.ExtractedFields);
        var combined = string.IsNullOrWhiteSpace(prior) ? answer : $"{prior} {answer}".Trim();

        var classification = await ClassifyTranscriptAsync(portfolioId, combined, ct);
        draft.ExtractedFields = MergeFields(draft.ExtractedFields, classification.ExtractedFieldsJson);
        draft.TargetEntityType = classification.TargetEntityType;
        draft.ModelId = classification.ModelId;
        draft.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>Reads the accumulated transcript from a draft's extracted-fields JSON.</summary>
    private static string? ExtractTranscript(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(fieldsJson);
            if (doc.RootElement.TryGetProperty("transcript", out var t)
                && t.TryGetProperty("value", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        catch
        {
            // Malformed JSON — treat as no prior transcript.
        }

        return null;
    }

    /// <summary>
    /// Merges a freshly-classified field set over the prior one: a new non-empty
    /// value wins; a field present before but dropped/blanked by the new pass is
    /// preserved, so an answered slot is never lost on a later turn.
    /// </summary>
    private static string MergeFields(string? priorJson, string newJson)
    {
        var merged = ParseFieldMap(priorJson);
        foreach (var (name, value) in ParseFieldMap(newJson))
        {
            if (!string.IsNullOrWhiteSpace(value.Value) || !merged.ContainsKey(name))
            {
                merged[name] = value;
            }
        }

        var shaped = merged.ToDictionary(
            kv => kv.Key,
            kv => (object)new { value = kv.Value.Value, confidence = kv.Value.Confidence });
        return JsonSerializer.Serialize(shaped, JsonOptions);
    }

    private static Dictionary<string, FieldValue> ParseFieldMap(string? json)
    {
        var map = new Dictionary<string, FieldValue>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return map;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var value = prop.Value.TryGetProperty("value", out var v)
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString())
                    : string.Empty;
                var confidence = prop.Value.TryGetProperty("confidence", out var c)
                    && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var dec)
                    ? dec
                    : 0m;
                map[prop.Name] = new FieldValue(value, confidence);
            }
        }
        catch
        {
            // Malformed JSON — return whatever parsed.
        }

        return map;
    }

    private sealed record FieldValue(string Value, decimal Confidence);

    private async Task<VoiceClassification> ClassifyTranscriptAsync(int portfolioId, string transcript, CancellationToken ct)
    {
        var grounding = await BuildGroundingContextAsync(portfolioId, ct);
        var prompt =
            """
            Convert this spoken rental-management note into one human-review draft.
            Choose targetEntityType as exactly one of: WorkOrder, Expense, Payment.
            Return only JSON in this shape:
            {
              "targetEntityType": "WorkOrder",
              "fields": {
                "title": {"value": "...", "confidence": 0.0},
                "description": {"value": "...", "confidence": 0.0}
              }
            }
            For WorkOrder include property_id when known or strongly matched, optional unit_id, tenant_id, title, description, category, priority, estimated_cost.
            For Expense/Payment use the same snake_case field names as receipt/payment scan drafts: vendor_name, amount/total, transaction_date, category, notes, payer_name, check_number, lease_id. Also include property_id when the speaker names a property that matches one in Known records (otherwise omit it).
            Known records:
            """ + "\n" + grounding + "\n\nTranscript:\n" + transcript;

        var raw = await _llm.ChatAsync(prompt, ct);
        return ParseClassification(raw, transcript);
    }

    private async Task<string> BuildGroundingContextAsync(int portfolioId, CancellationToken ct)
    {
        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Name, p.AddressLine1, p.City, p.State })
            .Take(100)
            .ToListAsync(ct);

        var units = await _db.Units
            .AsNoTracking()
            .Where(u => u.Property != null && u.Property.PortfolioId == portfolioId && u.DeletedAt == null)
            .Select(u => new { u.Id, u.PropertyId, u.UnitNumber })
            .Take(200)
            .ToListAsync(ct);

        var tenants = await _db.Tenants
            .AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId && t.DeletedAt == null)
            .Select(t => new { t.Id, Name = (t.FirstName + " " + t.LastName).Trim(), t.Phone })
            .Take(200)
            .ToListAsync(ct);

        return JsonSerializer.Serialize(new { properties, units, tenants }, JsonOptions);
    }

    private VoiceClassification ParseClassification(string raw, string transcript)
    {
        try
        {
            var cleaned = ExtractJsonObject(raw);
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var target = root.TryGetProperty("targetEntityType", out var targetEl)
                ? targetEl.GetString()
                : root.TryGetProperty("target_entity_type", out var snakeTargetEl)
                    ? snakeTargetEl.GetString()
                    : null;
            target = NormalizeTarget(target);

            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["transcript"] = new { value = transcript, confidence = 1m },
                ["target_entity_type"] = new { value = target, confidence = 1m },
            };

            if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fieldsEl.EnumerateObject())
                {
                    fields[prop.Name] = NormalizeFieldValue(prop.Value);
                }
            }

            return new VoiceClassification(
                target,
                JsonSerializer.Serialize(fields, JsonOptions),
                "voice-intake",
                null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse voice-intake LLM JSON; creating fallback WorkOrder draft.");
            var fallback = new Dictionary<string, object?>
            {
                ["transcript"] = new { value = transcript, confidence = 1m },
                ["target_entity_type"] = new { value = "WorkOrder", confidence = 0.2m },
                ["title"] = new { value = transcript.Length <= 80 ? transcript : transcript[..80], confidence = 0.2m },
                ["description"] = new { value = transcript, confidence = 0.2m },
            };
            return new VoiceClassification("WorkOrder", JsonSerializer.Serialize(fallback, JsonOptions), "voice-intake-fallback", null);
        }
    }

    private static object NormalizeFieldValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out _))
        {
            return JsonSerializer.Deserialize<object>(value.GetRawText(), JsonOptions)!;
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return new { value = raw ?? string.Empty, confidence = 0.5m };
    }

    private static string NormalizeTarget(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "expense" => "Expense",
        "payment" => "Payment",
        _ => "WorkOrder",
    };

    private static string ExtractJsonObject(string raw)
    {
        var text = raw.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private sealed record VoiceClassification(
        string TargetEntityType,
        string ExtractedFieldsJson,
        string ModelId,
        int? TokensUsed);
}
