using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class WorkOrderMutationClockPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 21, 5, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public WorkOrderMutationClockPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(WorkOrderMutationClockPostgreSqlTests));
        _services = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task UpdateAuthorizedAsync_UsesInjectedBusinessClockForAtomicMutationRows()
    {
        var workOrder = await SeedWorkOrderAsync();
        var service = new WorkOrderService(
            _services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            _services.GetRequiredService<IAtomicUnitOfWork>(),
            _services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());

        var updated = await service.UpdateAuthorizedAsync(
            _scope,
            workOrder.Id,
            new UpdateWorkOrderRequest
            {
                Status = WorkOrderStatus.Cancelled,
                StatusNote = "Cancelled during frozen simulation run.",
            },
            "work-order-update-sim-clock");

        updated.Should().NotBeNull();
        updated!.UpdatedAt.Should().Be(BusinessNowUtc);

        _context.Db.ChangeTracker.Clear();
        var row = await (
            from persisted in _context.Db.WorkOrders.AsNoTracking()
            join statusEvent in _context.Db.WorkOrderStatusEvents.AsNoTracking()
                on persisted.Id equals statusEvent.WorkOrderId
            join audit in _context.Db.AtomicAuditLogs.AsNoTracking()
                on persisted.Id equals audit.EntityId
            join outbox in _context.Db.OutboxMessages.AsNoTracking()
                on persisted.PortfolioId equals outbox.PortfolioId
            where persisted.Id == workOrder.Id
                && statusEvent.FromStatus == WorkOrderStatus.New
                && statusEvent.ToStatus == WorkOrderStatus.Cancelled
                && audit.EntityType == nameof(WorkOrder)
                && audit.Operation == AuditLogOperation.Updated
                && outbox.IdempotencyKey == "work-order-update:work-order-update-sim-clock"
            select new
            {
                persisted.UpdatedAt,
                StatusEventCreatedAtUtc = statusEvent.CreatedAtUtc,
                AuditTimestamp = audit.Timestamp,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
            }).SingleAsync();

        row.UpdatedAt.Should().Be(BusinessNowUtc);
        row.StatusEventCreatedAtUtc.Should().Be(BusinessNowUtc);
        row.AuditTimestamp.Should().Be(BusinessNowUtc);
        row.OutboxCreatedAtUtc.Should().Be(BusinessNowUtc);
        row.OutboxNextAttemptAtUtc.Should().Be(BusinessNowUtc);
    }

    private async Task<WorkOrder> SeedWorkOrderAsync()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Frozen Clock Apartments",
            AddressLine1 = "100 Simulation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Title = "Replace sink trap",
            Description = "Trap is cracked under kitchen sink.",
            Category = "Plumbing",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.New,
            RequestedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return workOrder;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
