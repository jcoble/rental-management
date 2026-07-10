using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Outbox;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Services;

public sealed class OutboxMessagePublisherTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task PublishAsync_StagesWithoutCommitting()
    {
        var publisher = new OutboxMessagePublisher(_ctx.Db, TimeProvider.System);

        await publisher.PublishAsync(
            1,
            "email",
            "test:stage-only",
            new { to = "owner@example.test", subject = "Test", body = "Body" });

        _ctx.Db.ChangeTracker.Entries<OutboxMessage>()
            .Should().ContainSingle(entry => entry.State == EntityState.Added);
        (await _ctx.Db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);

        await _ctx.Db.SaveChangesAsync();

        (await _ctx.Db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(1);
    }
}
