using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>One portfolio-scoped general-ledger account.</summary>
public sealed class LedgerAccount : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType AccountType { get; set; }
    public NormalBalance NormalBalance { get; set; }
    public int? ParentAccountId { get; set; }
    public string? SystemKey { get; set; }
    public ScheduleECategory? ScheduleECategory { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LedgerAccount? ParentAccount { get; set; }
    public List<LedgerAccount> ChildAccounts { get; set; } = [];
    public List<JournalLine> JournalLines { get; set; } = [];
}
