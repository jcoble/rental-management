using FluentAssertions;
using Microsoft.Data.Sqlite;
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
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the work-order status timeline: an initial event on create, a From→To event on each
/// status change (in the same save), the detail timeline ordered oldest→newest, and the tenant
/// IDOR guard on the portal work-order-detail read.
/// </summary>
public class WorkOrderStatusTimelineTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly ServiceProvider _services;
    private readonly WorkOrderService _service;
    private readonly PortalService _portal;
    private readonly WorkspaceReadScope _scope;

    public WorkOrderStatusTimelineTests()
    {
        _conn = new SqliteConnection($"Data Source=work-order-status-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new AccountingServiceTestDbContext(options);
        _db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.SaveChanges();
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(WorkOrderStatusTimelineTests));
        _services = AtomicDomainTestKernel.CreateForWorkOrders(_conn.ConnectionString);

        _service = new WorkOrderService(
            _db,
            new NoopDataUpdateService(),
            new NoopMessagePublisher(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance,
            TimeProvider.System,
            _services.GetRequiredService<IAtomicUnitOfWork>());
        _portal = new PortalService(_db, new NoopLeaseQaService(), TimeProvider.System);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task CreateAsync_WritesInitialNullToCreatedStatusEvent()
    {
        var property = SeedProperty();

        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        created.Should().NotBeNull();

        var events = await _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == created!.Id)
            .ToListAsync();

        events.Should().ContainSingle();
        events[0].FromStatus.Should().BeNull();
        events[0].ToStatus.Should().Be(WorkOrderStatus.New);
        events[0].ChangedByUserId.Should().Be(_scope.UserId);
        events[0].ChangedByLabel.Should().Be("Staff");
    }

    [Fact]
    public async Task UpdateAsync_AppendsFromToEvent_WhenStatusChanges()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.InProgress, StatusNote = "Plumber on site" });

        var events = await _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == created.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();

        events.Should().HaveCount(2);
        var change = events[1];
        change.FromStatus.Should().Be(WorkOrderStatus.New);
        change.ToStatus.Should().Be(WorkOrderStatus.InProgress);
        change.Note.Should().Be("Plumber on site");
        change.ChangedByUserId.Should().Be(_scope.UserId);
    }

    [Fact]
    public async Task UpdateAsync_StampsCompletedAt_WhenStatusChangesToCompleted()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Sticky lock",
                Description = "Front door lock sticks",
                Status = WorkOrderStatus.InProgress,
            });

        var beforeUpdate = DateTime.UtcNow;

        var updated = await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest
            {
                Status = WorkOrderStatus.Completed,
                StatusNote = "Lock lubricated and verified with tenant.",
            });

        var afterUpdate = DateTime.UtcNow;

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkOrderStatus.Completed);
        updated.CompletedAt.Should().NotBeNull();
        updated.CompletedAt.Should().BeOnOrAfter(beforeUpdate);
        updated.CompletedAt.Should().BeOnOrBefore(afterUpdate);

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.CompletedAt.Should().NotBeNull("moving a work order to Completed should stamp completion time");
        entity.CompletedAt.Should().Be(updated.CompletedAt);
    }

    [Fact]
    public async Task UpdateAsync_DoesNotAppendEvent_WhenStatusUnchanged()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.New });

        var count = await _db.WorkOrderStatusEvents.CountAsync(e => e.WorkOrderId == created.Id);
        count.Should().Be(1, "only the initial create event should exist when status did not change");
    }

    [Fact]
    public async Task UpdateAsync_AppendsSameStatusEvent_WhenScheduleChanges()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.Scheduled,
            });

        var scheduledFor = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest { ScheduledFor = scheduledFor });

        var events = await _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == created.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();

        events.Should().HaveCount(2);
        var edit = events[1];
        edit.FromStatus.Should().Be(WorkOrderStatus.Scheduled);
        edit.ToStatus.Should().Be(WorkOrderStatus.Scheduled);
        edit.Note.Should().Be("Schedule updated.");
        edit.ChangedByUserId.Should().Be(_scope.UserId);
    }

    [Fact]
    public async Task UpdateAsync_AppendsSameStatusEvent_WhenDetailsChange()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest { Title = "Leaky kitchen faucet" });

        var events = await _db.WorkOrderStatusEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == created.Id)
            .OrderBy(e => e.Id)
            .ToListAsync();

        events.Should().HaveCount(2);
        var edit = events[1];
        edit.FromStatus.Should().Be(WorkOrderStatus.New);
        edit.ToStatus.Should().Be(WorkOrderStatus.New);
        edit.Note.Should().Be("Details updated.");
    }

    [Fact]
    public async Task UpdateAsync_RejectsTenant_WhenUnitChangesOutsideTenantLease()
    {
        var (property, occupiedUnit, otherUnit, tenant) = SeedPropertyWithTenantLease();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                UnitId = occupiedUnit.Id,
                TenantId = tenant.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        var result = await UpdateAsync(
            created!.Id,
            new UpdateWorkOrderRequest { UnitId = otherUnit.Id });

        result.Should().BeNull("the atomic reference validation hides an incompatible tenant/unit combination");
        (await _db.WorkOrders.AsNoTracking().SingleAsync(item => item.Id == created.Id)).UnitId
            .Should().Be(occupiedUnit.Id);
    }

    [Fact]
    public async Task GetAsync_ReturnsTimelineOrderedOldestToNewest()
    {
        var property = SeedProperty();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                Title = "Leaky faucet",
                Description = "Kitchen sink drips",
                Status = WorkOrderStatus.New,
            });

        await UpdateAsync(created!.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.Scheduled });
        await UpdateAsync(created.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.InProgress });
        await UpdateAsync(created.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.Completed });

        var detail = await _service.GetAsync(PortfolioId, created.Id);

        detail.Should().NotBeNull();
        detail!.Timeline.Should().HaveCount(4);
        detail.Timeline.Select(t => t.ToStatus).Should().ContainInOrder(
            WorkOrderStatus.New,
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.InProgress,
            WorkOrderStatus.Completed);
        detail.Timeline[0].FromStatus.Should().BeNull();
        detail.Timeline[3].FromStatus.Should().Be(WorkOrderStatus.InProgress);
    }

    [Fact]
    public async Task PortalGetWorkOrderDetail_ReturnsTimeline_ForOwningTenant()
    {
        var (property, tenant) = SeedPropertyAndTenant();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                TenantId = tenant.Id,
                Title = "No hot water",
                Description = "Water heater out",
                Status = WorkOrderStatus.New,
            });

        await UpdateAsync(created!.Id,
            new UpdateWorkOrderRequest { Status = WorkOrderStatus.InProgress });

        var detail = await _portal.GetWorkOrderDetailAsync(PortfolioId, tenant.Id, created.Id);

        detail.Should().NotBeNull();
        detail!.Id.Should().Be(created.Id);
        detail.Timeline.Should().HaveCount(2);
        detail.Timeline.Last().ToStatus.Should().Be(WorkOrderStatus.InProgress);
    }

    [Fact]
    public async Task PortalGetWorkOrderDetail_ReturnsNull_ForAnotherTenantsWorkOrder()
    {
        var (property, tenant) = SeedPropertyAndTenant();
        var created = await CreateAsync(
            new CreateWorkOrderRequest
            {
                PropertyId = property.Id,
                TenantId = tenant.Id,
                Title = "No hot water",
                Description = "Water heater out",
                Status = WorkOrderStatus.New,
            });

        var foreignTenantId = tenant.Id + 1000;
        var detail = await _portal.GetWorkOrderDetailAsync(PortfolioId, foreignTenantId, created!.Id);

        detail.Should().BeNull("a tenant may only read their own work order's timeline");
    }

    private Task<WorkOrderResponse?> CreateAsync(CreateWorkOrderRequest request) =>
        _service.CreateAuthorizedAsync(_scope, request, Guid.NewGuid().ToString("N"));

    private Task<WorkOrderResponse?> UpdateAsync(int id, UpdateWorkOrderRequest request) =>
        _service.UpdateAuthorizedAsync(_scope, id, request, Guid.NewGuid().ToString("N"));

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "General",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private (Property property, Tenant tenant) SeedPropertyAndTenant()
    {
        var property = SeedProperty();
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(unit, tenant);
        _db.SaveChanges();
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "Portal-work-order",
            PossessionGivenAtUtc = now.AddMonths(-1),
            PossessionAgreementExceptionReason = "Work-order fixture has no legal-document artifact.",
            PossessionAgreementExceptionAuthorizedByUserId = 1,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Portal work-order test fixture",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        });
        _db.SaveChanges();
        return (property, tenant);
    }

    private (Property property, Unit occupiedUnit, Unit otherUnit, Tenant tenant) SeedPropertyWithTenantLease()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Tenant-scoped",
            AddressLine1 = "10 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var occupiedUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "1A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherUnit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "2B",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Maria",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(property, occupiedUnit, otherUnit, tenant);
        _db.SaveChanges();
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = occupiedUnit.Id,
            RelationshipNumber = "WO-tenant-scope",
            PossessionGivenAtUtc = now.AddMonths(-1),
            PossessionAgreementExceptionReason = "Work-order fixture has no legal-document artifact.",
            PossessionAgreementExceptionAuthorizedByUserId = 1,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Tenant-scoped work-order fixture",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        });
        _db.SaveChanges();
        return (property, occupiedUnit, otherUnit, tenant);
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoopMessagePublisher : IMessagePublisher
    {
        public Task PublishAsync<TPayload>(int portfolioId, string messageType, string idempotencyKey, TPayload payload, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
