using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Owners;

public enum OwnerApprovalDecision
{
    Approved = 1,
    Declined = 2,
}

public sealed record DecideOwnerApprovalCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int NotificationId,
    OwnerApprovalDecision Decision,
    string? Note,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record ReplyToOwnerMessageCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int NotificationId,
    string Body,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record OwnerPortalCommandResult(
    int SourceNotificationId,
    int OwnerEntityId,
    IReadOnlyList<int> StaffNotificationIds,
    DateTime RecordedAtUtc) : IAtomicResultData;
