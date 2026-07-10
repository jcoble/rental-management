using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Durable outcome of one atomic command. A receipt is inserted as Pending inside the owner
/// transaction and becomes Completed in the same transaction as the business mutation and audit
/// rows. Therefore a visible Pending receipt is an invariant violation, while a Completed receipt
/// is safe to replay after an unknown commit result.
/// </summary>
public sealed class AtomicCommandReceipt
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public AtomicCommandReceiptStatus Status { get; set; }
    public string ResultContract { get; set; } = string.Empty;
    public string? ResultJson { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
