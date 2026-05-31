using System.Text.Json;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
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

    public OutboxMessagePublisher(RentalCommandDbContext db)
    {
        _db = db;
    }

    public async Task PublishAsync<TPayload>(
        int portfolioId,
        string messageType,
        TPayload payload,
        CancellationToken ct = default)
    {
        var message = new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = messageType,
            Payload = JsonSerializer.Serialize(payload),
            RetryCount = 0,
            CreatedAt = DateTime.UtcNow,
        };

        _db.OutboxMessages.Add(message);
        await _db.SaveChangesAsync(ct);
    }
}
