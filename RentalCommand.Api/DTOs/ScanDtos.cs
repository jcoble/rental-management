using System.Text.Json;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

/// <summary>One extracted field surfaced to the review UI.</summary>
public sealed record ScanFieldDto(string Name, string Value, decimal Confidence);

/// <summary>Draft as seen by the review page.</summary>
public sealed record ScanDraftResponse(
    int Id, int PortfolioId, string TargetEntityType, string Status,
    string FileUrl, IReadOnlyList<ScanFieldDto> Fields,
    string? ModelId, int? TokensUsed, decimal? CostUsd,
    DateTime CreatedAt, DateTime? ReviewedAt, DateTime? ConfirmedAt)
{
    /// <summary>
    /// Builds a <see cref="ScanDraftResponse"/> from a <see cref="ScanDraft"/> entity,
    /// deserializing <see cref="ScanDraft.ExtractedFields"/> JSON
    /// <c>{name:{value,confidence}}</c> into the <see cref="Fields"/> list.
    /// Returns an empty list when <paramref name="d"/>.ExtractedFields is null or Status is Pending.
    /// </summary>
    public static ScanDraftResponse FromEntity(ScanDraft d)
    {
        var fields = ParseFields(d.ExtractedFields);
        var fileUrl = $"/api/v1/scans/{d.Id}/file";

        return new ScanDraftResponse(
            d.Id, d.PortfolioId, d.TargetEntityType, d.Status,
            fileUrl, fields,
            d.ModelId, d.TokensUsed, d.CostUsd,
            d.CreatedAt, d.ReviewedAt, d.ConfirmedAt);
    }

    private static IReadOnlyList<ScanFieldDto> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return [];

            var result = new List<ScanFieldDto>();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var valueEl = prop.Value.TryGetProperty("value", out var v) ? v : default;
                var confEl = prop.Value.TryGetProperty("confidence", out var c) ? c : default;

                var value = valueEl.ValueKind == JsonValueKind.String
                    ? valueEl.GetString() ?? string.Empty
                    : valueEl.ValueKind != JsonValueKind.Undefined
                        ? valueEl.ToString()
                        : string.Empty;

                var confidence = confEl.ValueKind == JsonValueKind.Number &&
                                 confEl.TryGetDecimal(out var confDec)
                    ? confDec
                    : 0m;

                result.Add(new ScanFieldDto(prop.Name, value, confidence));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }
}

public sealed record ScanCreatedResponse(int DraftId, string Status, string FileUrl);
public sealed record ConfirmScanRequest(string? OverridesJson);
public sealed record RejectScanRequest(string? Reason);
