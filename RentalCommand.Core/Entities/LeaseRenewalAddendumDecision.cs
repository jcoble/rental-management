using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>An explicit carry, incorporation, or end decision for an Addendum series at renewal.</summary>
public class LeaseRenewalAddendumDecision : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int RenewalAgreementId { get; set; }
    public Guid SourceAddendumSeriesPublicId { get; set; }
    public LeaseRenewalAddendumDecisionType Decision { get; set; }
    public int? ReplacementAddendumId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public LeaseAgreement? RenewalAgreement { get; set; }
    public LeaseAddendum? ReplacementAddendum { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
