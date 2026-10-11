using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Leasing;

/// <summary>
/// Canonical cryptographic binding between one immutable legal draft revision and the exact
/// uploaded artifact admitted for issuance.
/// </summary>
public static class LegalDocumentIssuanceBinding
{
    public const string ContractVersion = "legal-document-issuance-v1";
    public const string AgreementUploadPurpose = "legal-agreement-issuance";
    public const string AddendumUploadPurpose = "legal-addendum-issuance";

    public static string Create(
        string legalKind,
        int portfolioId,
        int leaseManagementId,
        int legalDocumentId,
        int draftRevision,
        int documentSourceVersionId,
        int termsSchemaVersion,
        string termsPayload,
        string artifactContentSha256,
        long artifactByteLength,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legalKind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseManagementId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(legalDocumentId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draftRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentSourceVersionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(termsSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifactByteLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!IsSha256(artifactContentSha256))
            throw new ArgumentException("The artifact content hash must be lowercase SHA-256.", nameof(artifactContentSha256));

        var canonicalTerms = CanonicalizeJsonObject(termsPayload);
        var payload = JsonSerializer.Serialize(new
        {
            contract = ContractVersion,
            legalKind,
            portfolioId,
            leaseManagementId,
            legalDocumentId,
            draftRevision,
            documentSourceVersionId,
            termsSchemaVersion,
            termsPayload = canonicalTerms,
            artifactContentSha256,
            artifactByteLength,
            fileName,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string CreateAddendum(
        int portfolioId,
        int leaseManagementId,
        int leaseAddendumId,
        int draftRevision,
        int documentSourceVersionId,
        int termsSchemaVersion,
        string termsPayload,
        IReadOnlyList<LegalDocumentIssuanceFinancialEffect> financialEffects,
        string artifactContentSha256,
        long artifactByteLength,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(financialEffects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseManagementId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseAddendumId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draftRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentSourceVersionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(termsSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifactByteLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!IsSha256(artifactContentSha256))
            throw new ArgumentException("The artifact content hash must be lowercase SHA-256.", nameof(artifactContentSha256));

        var canonicalTerms = CanonicalizeJsonObject(termsPayload);
        var canonicalEffects = financialEffects
            .OrderBy(effect => effect.EffectType)
            .ThenBy(effect => effect.EffectiveFromOn)
            .ThenBy(effect => effect.EffectiveThroughOn)
            .ThenBy(effect => effect.DueOn)
            .ThenBy(effect => effect.ChargeCode, StringComparer.Ordinal)
            .ThenBy(effect => effect.Currency, StringComparer.Ordinal)
            .ThenBy(effect => effect.Amount)
            .ThenBy(effect => effect.Description, StringComparer.Ordinal)
            .ThenBy(effect => effect.LeaseAddendumFinancialEffectId)
            .Select(effect => new
            {
                id = effect.LeaseAddendumFinancialEffectId,
                effectType = effect.EffectType.ToString(),
                effect.Amount,
                effect.Currency,
                effect.ChargeCode,
                effect.EffectiveFromOn,
                effect.EffectiveThroughOn,
                effect.DueOn,
                effect.Description,
            })
            .ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            contract = ContractVersion,
            legalKind = "LeaseAddendum",
            portfolioId,
            leaseManagementId,
            legalDocumentId = leaseAddendumId,
            draftRevision,
            documentSourceVersionId,
            termsSchemaVersion,
            termsPayload = canonicalTerms,
            financialEffects = canonicalEffects,
            artifactContentSha256,
            artifactByteLength,
            fileName,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool Matches(string suppliedFingerprint, string expectedFingerprint)
    {
        if (!IsSha256(suppliedFingerprint) || !IsSha256(expectedFingerprint)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(suppliedFingerprint),
            Convert.FromHexString(expectedFingerprint));
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            (character is >= '0' and <= '9') || (character is >= 'a' and <= 'f'));

    private static string CanonicalizeJsonObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Legal terms must be a JSON object.", nameof(json));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, document.RootElement);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// One authorized, server-read legal draft snapshot used to prepare immutable issuance bytes.
/// Every selector and authorization predicate is applied by the production reader in SQL.
/// </summary>
public sealed record LegalDocumentIssuanceDraftSnapshot(
    string LegalKind,
    int PortfolioId,
    int LeaseManagementId,
    int LegalDocumentId,
    int DraftRevision,
    bool MatchesExpectedDraftRevision,
    bool IsOpenDraft,
    int DocumentSourceVersionId,
    int TermsSchemaVersion,
    string TermsPayload,
    int PropertyId,
    string DocumentNumber,
    DateOnly EffectiveFromOn,
    DateOnly? EffectiveThroughOn,
    decimal BaseRentAmount,
    decimal SecurityDepositObligation,
    decimal LateFeeAmount,
    short RentDueDay,
    string LandlordName,
    string TenantName,
    string TenantEmail,
    string PropertyName,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string? UnitNumber,
    int? YearBuilt)
{
    public IReadOnlyList<LegalDocumentIssuanceFinancialEffect> FinancialEffects { get; init; } = [];
}

/// <summary>Canonical typed Addendum effect included in exact rendering and issuance binding.</summary>
public sealed record LegalDocumentIssuanceFinancialEffect(
    int LeaseAddendumFinancialEffectId,
    LeaseAddendumFinancialEffectType EffectType,
    decimal Amount,
    string Currency,
    string ChargeCode,
    DateOnly? EffectiveFromOn,
    DateOnly? EffectiveThroughOn,
    DateOnly? DueOn,
    string Description);

public interface ILegalDocumentIssuanceDraftReader
{
    Task<LegalDocumentIssuanceDraftSnapshot?> ReadAgreementAsync(
        RentalCommand.Core.Authorization.WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAgreementId,
        int expectedDraftRevision,
        DateTime securityNowUtc,
        CancellationToken ct = default);

    Task<LegalDocumentIssuanceDraftSnapshot?> ReadAddendumAsync(
        RentalCommand.Core.Authorization.WorkspaceReadScope scope,
        int leaseManagementId,
        int leaseAddendumId,
        int expectedDraftRevision,
        DateTime securityNowUtc,
        CancellationToken ct = default);
}
