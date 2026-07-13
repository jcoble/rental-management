using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class GivePossessionRequest
{
    public int UnitId { get; set; }
}

public sealed record PrepareMoveInContextResponse(DateOnly BusinessDate);

public sealed record GivePossessionResponse(
    int LeaseManagementId,
    int UnitId,
    DateTime PossessionGivenAtUtc,
    bool Replayed);

public sealed class ReturnPossessionPartyRequest
{
    public int LeaseManagementPartyId { get; set; }
    public ReturnPartyDisposition? Disposition { get; set; }
}

public sealed class ReturnPossessionAccessRequest
{
    public int TenantUserAccessId { get; set; }
    public ReturnAccessDisposition? Disposition { get; set; }
}

public sealed class ReturnPossessionRequest
{
    public int UnitId { get; set; }
    public List<ReturnPossessionPartyRequest> Parties { get; set; } = [];
    public List<ReturnPossessionAccessRequest> Accesses { get; set; } = [];
    public string TurnoverReason { get; set; } = string.Empty;
}

public sealed class ReturnPossessionContextResponse
{
    public IReadOnlyList<LeaseManagementPartyResponse> Parties { get; init; } = [];
    public IReadOnlyList<ActiveTenantUserAccessResponse> ActiveTenantUserAccesses { get; init; } = [];
}

public sealed record ReturnPossessionResponse(
    int LeaseManagementId,
    int UnitId,
    int TurnoverPeriodId,
    DateTime PossessionReturnedAtUtc,
    bool Replayed);

public sealed class CancelPlannedRelationshipAccessRequest
{
    public int TenantUserAccessId { get; set; }
    public CancelPlannedAccessDisposition? Disposition { get; set; }
}

public sealed class CancelPlannedRelationshipRequest
{
    public int UnitId { get; set; }
    public string CancellationReasonCode { get; set; } = string.Empty;
    public string? CancellationNote { get; set; }
    public string DraftCancellationReason { get; set; } = string.Empty;
    public List<CancelPlannedRelationshipAccessRequest> Accesses { get; set; } = [];
}

public sealed record CancelPlannedRelationshipResponse(
    int LeaseManagementId,
    int UnitId,
    DateTime CanceledAtUtc,
    DateTime AccountClosedAtUtc,
    IReadOnlyList<int> CanceledAgreementDraftIds,
    IReadOnlyList<int> CanceledAddendumDraftIds,
    IReadOnlyList<int> RevokedAccessIds,
    IReadOnlyList<int> RetainedAccessIds,
    bool Replayed);

public sealed class TransferLeaseManagementRequest
{
    public int SourceUnitId { get; set; }
    public int DestinationUnitId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateTime? PlannedDestinationPossessionAtUtc { get; set; }
    public bool GiveDestinationPossessionNow { get; set; }
    public string? PossessionAgreementExceptionReason { get; set; }
    public int DestinationDocumentTemplateId { get; set; }
    public bool CarryTenantBalance { get; set; } = true;
    public bool CarrySecurityDeposit { get; set; } = true;
    public string TransferReason { get; set; } = string.Empty;
}

public sealed record TransferLeaseManagementResponse(
    Guid TransferPublicId,
    int SourceLeaseManagementId,
    int SourceUnitId,
    int DestinationLeaseManagementId,
    int DestinationUnitId,
    int DestinationTenantAccountId,
    int DestinationAgreementId,
    int? DestinationSecurityDepositAccountId,
    int TurnoverPeriodId,
    DateTime SourcePossessionReturnedAtUtc,
    DateTime? DestinationPossessionGivenAtUtc,
    decimal CarriedTenantBalance,
    decimal CarriedSecurityDeposit,
    IReadOnlyList<int> DestinationPartyIds,
    IReadOnlyList<int> DestinationSignerIds,
    IReadOnlyList<int> DestinationAccessIds,
    bool DestinationAgreementRequiresSignature,
    bool Replayed);

public sealed record CompleteTurnoverResponse(
    int UnitId,
    int TurnoverPeriodId,
    DateTime CompletedAtUtc,
    bool Replayed);
