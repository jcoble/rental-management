using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services;

/// <summary>
/// API-side DB-outbox implementation of <see cref="IMessagePublisher"/>. Mirrors the Engine's
/// <c>OutboxMessagePublisher</c> but lives in the API process so scoped services (e.g.
/// <c>OwnerStatementEmailService</c>) can enqueue messages within the same request/scope.
/// The Engine's <c>OutboxDispatchWorker</c> picks up and dispatches these rows.
/// </summary>
public sealed class OutboxMessagePublisher : IMessagePublisher
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public OutboxMessagePublisher(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task PublishAsync<TPayload>(
        int portfolioId,
        string messageType,
        string idempotencyKey,
        TPayload payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var now = _timeProvider.UtcNow();
        var message = new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = messageType,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = idempotencyKey,
            AttemptCount = 0,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };

        _db.OutboxMessages.Add(message);
        await _db.SaveChangesAsync(ct);
    }
}
