using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Operations;

public sealed record RecordTechnicianWorkEntryCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    TechnicianWorkEntryKind Kind,
    string? Note,
    decimal? Quantity,
    string? Unit,
    int? StoredFileId,
    DateTime OccurredAtUtc,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record RecordTechnicianWorkEntryResult(
    int EntryId,
    int WorkOrderId,
    TechnicianWorkEntryKind Kind,
    DateTime CreatedAtUtc) : IAtomicResultData;

public sealed record SendTechnicianAssignmentMessageCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    string Body,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record SendTechnicianAssignmentMessageResult(
    int ConversationId,
    int MessageId,
    DateTime CreatedAtUtc) : IAtomicResultData;

public sealed record MarkTechnicianAssignmentConversationReadCommand(
    int PortfolioId,
    int ActorUserId,
    Guid ActorSessionId,
    int ActorAccessContextId,
    long ActorAccessRevision,
    int WorkOrderId,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record MarkTechnicianAssignmentConversationReadResult(
    int? ConversationId,
    bool Found) : IAtomicResultData;
