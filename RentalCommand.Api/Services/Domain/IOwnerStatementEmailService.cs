using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>Result of an attempt to email an owner statement.</summary>
public record StatementEmailResult(bool Sent, string? Reason = null);

/// <summary>
/// Renders an owner's annual statement as plain text and enqueues it for delivery
/// via the DB outbox. The actual send is handled by the Engine's OutboxDispatchWorker.
/// </summary>
public interface IOwnerStatementEmailService : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    Task<StatementEmailResult> SendOwnerStatementAsync(
        WorkspaceReadScope scope,
        int ownerId,
        int year,
        string idempotencyKey,
        CancellationToken ct = default);
}
