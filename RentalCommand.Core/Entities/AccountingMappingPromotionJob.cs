namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable continuation for bounded parked-accounting promotion. Each command processes at most
/// one fixed batch; a completed or superseded row is retained as recovery evidence.
/// </summary>
public sealed class AccountingMappingPromotionJob
{
    public Guid Id { get; set; }
    public int PortfolioId { get; set; }
    public int AccountingConnectionId { get; set; }
    public int AccountingEntityMappingId { get; set; }
    public long MappingRevision { get; set; }
    public int PromotedCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public AccountingConnection? AccountingConnection { get; set; }
    public AccountingEntityMapping? AccountingEntityMapping { get; set; }
    public Portfolio? Portfolio { get; set; }
}
