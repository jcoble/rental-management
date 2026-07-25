using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One durable, destination-specific outbound delivery. Producers insert this row in the same
/// transaction as their business mutation. Dispatchers lease it without holding a transaction
/// open across the provider call, then finalize only while they still own the fencing token.
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public int? PortfolioId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;

    /// <summary>Required stable identity for this logical delivery, unique across all producers.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }

    public string? ClaimOwner { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }

    /// <summary>The external provider accepted responsibility for this delivery.</summary>
    public DateTime? AcceptedAtUtc { get; set; }

    /// <summary>Optional later confirmation from a provider delivery webhook.</summary>
    public DateTime? DeliveredAtUtc { get; set; }

    /// <summary>Terminal failure. A blocked or dead-lettered row is never eligible to claim.</summary>
    public DateTime? DeadLetteredAtUtc { get; set; }

    public string? Provider { get; set; }
    public string? ProviderMessageId { get; set; }
    public OutboxFailureKind? FailureKind { get; set; }
    public string? LastError { get; set; }

    public Portfolio? Portfolio { get; set; }
}
