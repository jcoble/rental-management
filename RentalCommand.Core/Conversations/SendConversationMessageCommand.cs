using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Conversations;

/// <summary>
/// Persists one landlord message and every durable recipient destination intent as one command.
/// A null conversation id opens a new thread; otherwise the message is appended to that thread.
/// </summary>
public sealed record SendConversationMessageCommand(
    int PortfolioId,
    int? ConversationId,
    int TenantId,
    string Subject,
    string Body,
    ConversationSenderRole SenderRole,
    IReadOnlyList<string> RequestedChannels,
    [property: AtomicFingerprintIgnore] DateTime CreatedAtUtc,
    int? PropertyId = null,
    ConversationManagementAccess? ManagementAccess = null) : IAtomicCommandData;

/// <summary>
/// Server-derived management authorization coordinates. They are revalidated by the atomic handler
/// against the current database session, revision, capability, and property scope before a landlord
/// message is written or a completed receipt is replayed.
/// </summary>
public sealed record ConversationManagementAccess(
    Guid SessionId,
    int UserId,
    int AccessContextId,
    long AccessRevision,
    string? RequiredCapabilityKey = null) : IAtomicCommandData;

public enum SendConversationMessageOutcome
{
    Applied,
    NotFound,
}

/// <summary>Receipt-safe identity of the committed message package.</summary>
public sealed record SendConversationMessageResult(
    SendConversationMessageOutcome Outcome,
    int ConversationId,
    int MessageId,
    IReadOnlyList<int> NotificationIds);
