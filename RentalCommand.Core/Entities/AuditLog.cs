using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Append-only audit trail. <see cref="UserId"/> is nullable because system/AI
/// actors (e.g. "ai-scan", "engine:RentChargeWorker") have no <see cref="ApplicationUser"/>;
/// those are identified by <see cref="ActorLabel"/> instead.
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>FK → <see cref="ApplicationUser.Id"/>; null for non-user actors.</summary>
    public int? UserId { get; set; }

    /// <summary>Label for non-user actors (e.g. "ai-scan", "engine:RentChargeWorker").</summary>
    public string? ActorLabel { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public AuditLogOperation Operation { get; set; }

    /// <summary>JSON snapshot of prior values.</summary>
    public string? OldValues { get; set; }

    /// <summary>JSON snapshot of new values.</summary>
    public string? NewValues { get; set; }

    public string? ChangeReason { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }

    public ApplicationUser? User { get; set; }
}
