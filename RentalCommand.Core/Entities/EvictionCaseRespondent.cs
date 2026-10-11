using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Explicit party named as a respondent in an eviction case.</summary>
public class EvictionCaseRespondent : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int EvictionCaseId { get; set; }
    public int LeaseManagementPartyId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public EvictionCase? EvictionCase { get; set; }
    public LeaseManagementParty? LeaseManagementParty { get; set; }
}
