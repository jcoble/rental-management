using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Cash contributed by a specific owner. This increases owner equity; it is not rental income.
/// </summary>
public class OwnerContribution : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int OwnerEntityId { get; set; }
    public int? PropertyId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public DistributionMethod Method { get; set; } = DistributionMethod.Check;
    public OwnerDistributionStatus Status { get; set; } = OwnerDistributionStatus.Draft;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ApprovedBusinessDate { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public int? RejectedByUserId { get; set; }
    public string? RejectionReason { get; set; }
    public string? BankReference { get; set; }
    public string? ExportReference { get; set; }
    public DateTime? ExportedAt { get; set; }
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public OwnerEntity? OwnerEntity { get; set; }
    public Property? Property { get; set; }
    public ApplicationUser? ApprovedByUser { get; set; }
    public ApplicationUser? RejectedByUser { get; set; }
}
