using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable, provider-neutral receipt for one verified inbound provider event. Claim fields lease
/// processing to one worker, while terminal updates are fenced by <see cref="ClaimToken"/>.
/// </summary>
public sealed class ProviderInboxEvent
{
    public long Id { get; set; }
    public int? PortfolioId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string? ProviderObjectId { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }

    public string? ClaimOwner { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }

    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime? DeadLetteredAtUtc { get; set; }
    public ProviderInboxFailureKind? FailureKind { get; set; }
    public string? LastError { get; set; }
}
