using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.DTOs;

public sealed class GivePossessionRequest
{
    public int UnitId { get; set; }
}

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
    public DateOnly EffectiveOn { get; set; }
    public List<ReturnPossessionPartyRequest> Parties { get; set; } = [];
    public List<ReturnPossessionAccessRequest> Accesses { get; set; } = [];
    public string TurnoverReason { get; set; } = string.Empty;
}

public sealed record ReturnPossessionResponse(
    int LeaseManagementId,
    int UnitId,
    int TurnoverPeriodId,
    DateTime PossessionReturnedAtUtc,
    bool Replayed);

public sealed class CompleteTurnoverRequest
{
    public int TurnoverPeriodId { get; set; }
}

public sealed record CompleteTurnoverResponse(
    int UnitId,
    int TurnoverPeriodId,
    DateTime CompletedAtUtc,
    bool Replayed);
