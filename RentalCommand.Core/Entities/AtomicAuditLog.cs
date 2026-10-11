using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Sole append-only audit record for Rental Command. Business-command metadata ties each mutation
/// to the retry-safe receipt that committed it; explicitly classified infrastructure workflows use
/// a unique receiptless attempt identity because they are not caller-replayable business commands.
/// </summary>
public sealed class AtomicAuditLog
{
    public long Id { get; set; }
    public Guid AttemptId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string CommandIdempotencyKey { get; set; } = string.Empty;
    public long MutationOrdinal { get; set; }
    public int PortfolioId { get; set; }
    public int? UserId { get; set; }
    public string? ActorLabel { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public AuditLogOperation Operation { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? ChangeReason { get; set; }
    public DateTime Timestamp { get; set; }
    public string? IpAddress { get; set; }
}
