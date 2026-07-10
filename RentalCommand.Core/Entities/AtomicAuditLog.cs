using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Audit row owned exclusively by the opt-in atomic persistence kernel. The existing application
/// audit trail is intentionally separate until every write path has been converted.
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
