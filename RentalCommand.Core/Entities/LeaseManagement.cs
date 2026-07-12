using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One continuous household, possession, and tenant-account episode at a Unit.
/// Legal agreement versions and financial entries are separate durable records.
/// </summary>
public class LeaseManagement : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    /// <summary>The prior Unit relationship when this relationship was opened by a transfer.</summary>
    public int? TransferredFromLeaseManagementId { get; set; }
    public Guid? TransferPublicId { get; set; }
    public DateTime? TransferredAtUtc { get; set; }
    public string? TransferReason { get; set; }
    public string RelationshipNumber { get; set; } = string.Empty;
    public DateTime? PlannedPossessionAtUtc { get; set; }
    public DateTime? PossessionGivenAtUtc { get; set; }
    public string? PossessionAgreementExceptionReason { get; set; }
    public int? PossessionAgreementExceptionAuthorizedByUserId { get; set; }
    public DateTime? NoticeGivenAtUtc { get; set; }
    public DateTime? PlannedMoveOutAtUtc { get; set; }
    public DateTime? PossessionReturnedAtUtc { get; set; }
    public DateTime? AccountClosedAtUtc { get; set; }
    public DateTime? CanceledAtUtc { get; set; }
    public string? CancellationReasonCode { get; set; }
    public string? CancellationNote { get; set; }
    public LeaseManagementEndingDisposition EndingDisposition { get; set; } =
        LeaseManagementEndingDisposition.Undecided;
    public DateTime? EndingDispositionDecidedAtUtc { get; set; }
    public int? EndingDispositionDecidedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public Guid RowVersion { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public LeaseManagement? TransferredFromLeaseManagement { get; set; }
    public LeaseManagement? TransferredToLeaseManagement { get; set; }
    public ApplicationUser? PossessionAgreementExceptionAuthorizedByUser { get; set; }
    public ApplicationUser? EndingDispositionDecidedByUser { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<LeaseManagementParty> Parties { get; set; } = [];
    public List<LeaseAgreement> Agreements { get; set; } = [];
    public List<LeaseAddendum> Addenda { get; set; } = [];
    public List<LeaseRenewalAddendumDecision> RenewalAddendumDecisions { get; set; } = [];
    public List<UnitOperationalPeriod> SourceOperationalPeriods { get; set; } = [];
    public TenantAccount? TenantAccount { get; set; }
    public RentalApplication? PreparedFromApplication { get; set; }
}
