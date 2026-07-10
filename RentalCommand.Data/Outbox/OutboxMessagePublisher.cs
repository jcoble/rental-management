using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;

namespace RentalCommand.Data.Outbox;

/// <summary>
/// Stages a reliable outbox row in the shared DbContext. It intentionally never calls SaveChanges:
/// the application service or atomic command owns the commit that persists business state and every
/// related destination together.
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

    public Task PublishAsync<TPayload>(
        int portfolioId,
        string messageType,
        string idempotencyKey,
        TPayload payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var now = _timeProvider.UtcNow();
        _db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = messageType,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = idempotencyKey,
            AttemptCount = 0,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

        return Task.CompletedTask;
    }
}
