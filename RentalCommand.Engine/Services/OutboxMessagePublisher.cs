using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// DB-outbox implementation of <see cref="IMessagePublisher"/>: enqueues an
/// <see cref="OutboxMessage"/> row that the <see cref="Workers.OutboxDispatchWorker"/> later
/// dispatches. Writing the message and the business transaction in the same DbContext gives
/// the usual transactional-outbox guarantee (no message is "sent" unless the work committed).
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
