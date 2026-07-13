using System.Text.Json;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class PrepareMoveInPartyRequest
{
    public int TenantId { get; set; }
    public LeaseManagementPartyRole? Role { get; set; }
    public bool GuarantorLegalNoticeEligible { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public bool IsAgreementSigner { get; set; }
    public short? SigningOrder { get; set; }
    public bool IsRequiredSigner { get; set; } = true;
}

public sealed class PrepareMoveInRequest
{
    public int ApplicationId { get; set; }
    public int UnitId { get; set; }
    public DateTime? PlannedPossessionAtUtc { get; set; }
    public DateOnly PartyEffectiveFrom { get; set; }
    public List<PrepareMoveInPartyRequest> Parties { get; set; } = [];
    public int DocumentTemplateId { get; set; }
    public LeaseAgreementTermType? TermType { get; set; }
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public decimal BaseRentAmount { get; set; }
    public short RentDueDay { get; set; }
    public decimal SecurityDepositObligation { get; set; }
    public decimal LateFeeAmount { get; set; }
    public short GracePeriodDays { get; set; }
    public int TermsSchemaVersion { get; set; } = 1;
    public JsonElement TermsPayload { get; set; }
    public bool CreateSecurityDepositAccount { get; set; }
    /// <summary>
    /// Optional signed balance brought into Rental Command. Positive means owed; negative means a
    /// tenant credit. It is posted once to the new TenantAccount, never stored as a mutable lease row.
    /// </summary>
    public decimal? OpeningBalanceAmount { get; set; }
    public DateOnly? OpeningBalanceEffectiveOn { get; set; }
    public string? OpeningBalanceNote { get; set; }
}

public sealed record PrepareMoveInResponse(
    int ApplicationId,
    int LeaseManagementId,
    int TenantAccountId,
    int LeaseAgreementId,
    long? OpeningBalanceLedgerEntryId,
    int? SecurityDepositAccountId,
    IReadOnlyList<int> LeaseManagementPartyIds,
    IReadOnlyList<int> LeaseAgreementSignerIds,
    bool Replayed)
{
    public static PrepareMoveInResponse FromResult(PrepareMoveInResult result, bool replayed) => new(
        result.ApplicationId,
        result.LeaseManagementId,
        result.TenantAccountId,
        result.LeaseAgreementId,
        result.OpeningBalanceLedgerEntryId,
        result.SecurityDepositAccountId,
        result.LeaseManagementPartyIds,
        result.LeaseAgreementSignerIds,
        replayed);
}
