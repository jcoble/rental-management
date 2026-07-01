using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
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
    private readonly TimeProvider _timeProvider;
    private static readonly char[] TranscriptSeparators =
    [
        ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '|'
    ];

    private static readonly HashSet<string> LowInformationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "uh", "um", "umm", "hmm", "hm", "ah", "er", "like", "maybe", "left", "right",
        "ok", "okay", "yeah", "yep", "nope", "yes", "no", "test", "testing", "hello"
    };

    public VoiceIntakeService(
        RentalCommandDbContext db,
        ILlmProvider llm,
        IAudioTranscriptionService transcriber,
        IFileStorage storage,
        ILogger<VoiceIntakeService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _llm = llm;
        _transcriber = transcriber;
        _storage = storage;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] audioBytes,
        string? contentType,
        string? providedTranscript,
        CancellationToken ct = default)
    {
        // Whisper picks its decoder from the upload's file extension, so name the
        // file from the real recording format (Safari/iOS sends audio/mp4, not webm).
        var voiceFileName = TranscriptionFileName(contentType);
        var transcript = string.IsNullOrWhiteSpace(providedTranscript)
            ? await _transcriber.TranscribeAsync(audioBytes, contentType ?? "application/octet-stream", voiceFileName, ct)
            : providedTranscript.Trim();

        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new ArgumentException("A transcript or non-empty transcribable audio is required.");
        }

        if (!LooksLikeUsableInitialTranscript(transcript))
        {
            throw new ArgumentException(
                "Record a clearer voice note with what happened, where it happened, and any amount or date you know.");
        }

        var filePath = $"voice://{Guid.NewGuid():N}";
        if (audioBytes.Length > 0)
        {
            filePath = await _storage.UploadAsync(
                new MemoryStream(audioBytes),
                $"voice-{_timeProvider.UtcNow():yyyyMMddHHmmss}{Path.GetExtension(voiceFileName)}",
                contentType ?? "application/octet-stream",
                ct);

            _db.StoredFiles.Add(new StoredFile
            {
                PortfolioId = portfolioId,
                FileName = voiceFileName,
                FilePath = filePath,
                ContentType = contentType ?? "application/octet-stream",
                FileSize = audioBytes.Length,
                EntityType = "ScanDraft",
                EntityId = null,
                UploadedAt = _timeProvider.UtcNow(),
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
            CreatedAt = _timeProvider.UtcNow(),
            ReviewedAt = _timeProvider.UtcNow(),
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
            ? await _transcriber.TranscribeAsync(audioBytes, contentType ?? "application/octet-stream", TranscriptionFileName(contentType), ct)
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
        draft.ReviewedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>
    /// Filename to hand the transcriber for a recorded note. Whisper selects its
    /// audio decoder from the file extension, so it must match the bytes' real
    /// container — Safari/iOS records <c>audio/mp4</c> while other browsers record
    /// <c>audio/webm</c>. Derive the extension from the content-type the browser
    /// sent (codec parameters stripped), falling back to <c>.webm</c>.
    /// </summary>
    private static string TranscriptionFileName(string? contentType)
    {
        var mime = contentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var ext = mime switch
        {
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/mp4" => ".mp4",
            "audio/x-m4a" or "audio/m4a" => ".m4a",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            _ => ".webm",
        };
        return $"voice{ext}";
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

    private static bool LooksLikeUsableInitialTranscript(string transcript)
    {
        if (transcript.Count(char.IsLetterOrDigit) < 6)
        {
            return false;
        }

        var meaningfulWords = transcript
            .Split(TranscriptSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(word => word.Length >= 2 && !LowInformationWords.Contains(word));

        return meaningfulWords >= 2;
    }

    /// <summary>
    /// Merges a freshly-classified field set over the prior one: a new non-empty
    /// value wins; a field present before but dropped/blanked by the new pass is
    /// preserved, so an answered slot is never lost on a later turn.
    /// </summary>
    private static string MergeFields(string? priorJson, string newJson)
    {
        var merged = ParseFieldMap(priorJson);
        var next = ParseFieldMap(newJson);

        // These are server-owned per-turn classification flags, not user slots.
        // If the latest full-conversation classification no longer marks the
        // draft ambiguous, clear the stale prior flags so the flow can recover.
        foreach (var transient in new[] { "voice_ambiguous", "voice_intent" })
        {
            if (!next.ContainsKey(transient))
            {
                merged.Remove(transient);
            }
        }

        foreach (var (name, value) in next)
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
            Convert this spoken rental-management note into one human-review draft for the voice Expense v1 flow.
            V1 only saves expenses by voice. Choose targetEntityType as exactly one of: Expense, Unsupported.
            Return only JSON in this shape:
            {
              "targetEntityType": "Expense",
              "fields": {
                "vendor_name": {"value": "...", "confidence": 0.0},
                "amount": {"value": "...", "confidence": 0.0},
                "notes": {"value": "...", "confidence": 0.0}
              }
            }
            Use Expense only when the transcript is trying to log a cost, receipt, bill, invoice, purchase, repair charge, utility charge, or similar expense.
            Use Unsupported when the transcript is clearly asking for a payment, work order, tenant message, lease, appointment, or any non-expense record.
            For Expense use the same snake_case field names as receipt scan drafts: vendor_name, amount/total, transaction_date, category, notes. Also include property_id when the speaker names a property that matches one in Known records (otherwise omit it).
            For Unsupported include notes summarizing what the speaker asked for.
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
            var voiceIntent = NormalizeVoiceIntent(target);
            var ambiguous = voiceIntent != "Expense";
            target = "Expense";

            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["transcript"] = new { value = transcript, confidence = 1m },
            };

            if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in fieldsEl.EnumerateObject())
                {
                    fields[prop.Name] = NormalizeFieldValue(prop.Value);
                }
            }

            fields.Remove("target_entity_type");
            fields.Remove("voice_intent");
            fields.Remove("voice_ambiguous");
            fields["target_entity_type"] = new { value = target, confidence = 1m };
            if (ambiguous)
            {
                fields["voice_intent"] = new { value = voiceIntent, confidence = 1m };
                fields["voice_ambiguous"] = new { value = "true", confidence = 1m };
            }

            return new VoiceClassification(
                target,
                JsonSerializer.Serialize(fields, JsonOptions),
                "voice-intake",
                null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse voice-intake LLM JSON; creating fallback Expense draft.");
            var fallback = new Dictionary<string, object?>
            {
                ["transcript"] = new { value = transcript, confidence = 1m },
                ["target_entity_type"] = new { value = "Expense", confidence = 0.2m },
                ["notes"] = new { value = transcript, confidence = 0.2m },
            };
            return new VoiceClassification("Expense", JsonSerializer.Serialize(fallback, JsonOptions), "voice-intake-fallback", null);
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

    private static string NormalizeVoiceIntent(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "expense" => "Expense",
        "payment" => "Payment",
        "workorder" or "work_order" or "work order" => "WorkOrder",
        _ => "Unsupported",
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
