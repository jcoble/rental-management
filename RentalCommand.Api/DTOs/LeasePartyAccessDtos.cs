using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class LeaseLegalBasisRequest
{
    public bool SameRelationshipConfirmed { get; set; }
    public int? AgreementId { get; set; }
    public int? AddendumId { get; set; }
}

public sealed class AddEffectivePartyRequest
{
    public int TenantId { get; set; }
    public LeaseManagementPartyRole? Role { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public bool GuarantorLegalNoticeEligible { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public LeaseLegalBasisRequest LegalBasis { get; set; } = new();
}

public sealed class EndEffectivePartyRequest
{
    public DateOnly EffectiveThrough { get; set; }
    public TenantAccessDisposition? AccessDisposition { get; set; }
    public int? PrimarySuccessorPartyId { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public LeaseLegalBasisRequest LegalBasis { get; set; } = new();
}

public sealed class ChangeEffectivePartyRoleRequest
{
    public LeaseManagementPartyRole? NewRole { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public bool GuarantorLegalNoticeEligible { get; set; }
    public TenantAccessDisposition? AccessDisposition { get; set; }
    public int? CompanionPrimaryPartyId { get; set; }
    public LeaseManagementPartyRole? CompanionNewRole { get; set; }
    public bool CompanionGuarantorLegalNoticeEligible { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public LeaseLegalBasisRequest LegalBasis { get; set; } = new();
}

public sealed class GrantTenantUserAccessRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class RevokeTenantUserAccessRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed record LeasePartyMutationResponse(
    int LeaseManagementId,
    int PartyId,
    int? ReplacementPartyId,
    int? CompanionReplacementPartyId,
    IReadOnlyList<int> TenantUserAccessIds,
    bool Replayed)
{
    public static LeasePartyMutationResponse FromResult(
        LeasePartyMutationResult result,
        bool replayed) => new(
            result.LeaseManagementId,
            result.PartyId,
            result.ReplacementPartyId,
            result.CompanionReplacementPartyId,
            result.TenantUserAccessIds,
            replayed);
}
