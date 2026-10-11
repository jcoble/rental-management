using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class EvictionCase : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int? LeaseAgreementId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public EvictionCaseStatus Status { get; set; } = EvictionCaseStatus.Draft;
    public DateTime? FiledOnDate { get; set; }
    public DateTime? HearingDate { get; set; }
    public DateTime? ResolvedOnDate { get; set; }
    public string? CourtName { get; set; }
    public string? CaseNumber { get; set; }
    public string? Resolution { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
    public List<EvictionCaseRespondent> Respondents { get; set; } = [];
    public List<EvictionCaseEvent> Events { get; set; } = [];
}
