namespace RentalCommand.Data.Atomic; // Audit guard contracts.

internal sealed record AtomicRawDmlPermit(
    string TableName,
    AtomicRawDmlOperation Operation,
    bool AllowWithoutAttempt);

internal sealed record AtomicRawDmlTarget(
    string TableName,
    AtomicRawDmlOperation Operation);

internal enum AtomicRawDmlOperation
{
    Insert,
    Update,
    Delete,
}
