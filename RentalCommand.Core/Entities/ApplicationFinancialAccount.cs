using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// The pre-tenancy financial account owned by exactly one rental application. It is deliberately
/// separate from TenantAccount: an applicant has not entered a lease relationship yet.
/// </summary>
public sealed class ApplicationFinancialAccount : IPortfolioScoped, IAuditable
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int RentalApplicationId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime OpenedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public RentalApplication? RentalApplication { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<ApplicationFinancialEntry> Entries { get; set; } = [];
}
