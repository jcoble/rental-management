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
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the editable Costs &amp; timing round-trip on a work order (RequestedAt / ScheduledFor /
/// CompletedAt / EstimatedCost / ActualCost — including on a Completed order, which has no reopen
/// workflow) and the display-name + unit-aggregate projections that the detail/list views read.
/// </summary>
[Collection(MigratedPostgreSqlCollection.Name4)]
public class WorkOrderCostsTimingAndProjectionTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private RentalCommandDbContext _db = null!;
    private RentalCommandDbContext _serviceDb = null!;
    private ServiceProvider _services = null!;
    private WorkOrderService _workOrders = null!;
    private WorkspaceReadScope _scope;

    public WorkOrderCostsTimingAndProjectionTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _db = _ctx.Db;
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(WorkOrderCostsTimingAndProjectionTests));
        _services = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(_ctx.ConnectionString);
        _serviceDb = _services.GetRequiredService<RentalCommandDbContext>();

        _workOrders = new WorkOrderService(
            _serviceDb,
            new NoopDataUpdate(),
            new NoopMessagePublisher(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance,
            TimeProvider.System,
            _services.GetRequiredService<RentalCommand.Core.Atomic.IWriteExecutor>());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task UpdateAsync_RoundTripsAllFiveCostsAndTimingFields()
    {
        var property = SeedProperty("Maple Court");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Leaky faucet",
            Description = "Kitchen sink drips",
            Status = WorkOrderStatus.New,
        });

        var requested = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        // ScheduledFor is offset-bearing on the wire (DateTimeOffset); it is stored as the UTC instant.
        var scheduled = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var completed = new DateTime(2026, 1, 12, 0, 0, 0, DateTimeKind.Utc);

        var updated = await UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            RequestedAt = requested,
            ScheduledFor = scheduled,
            CompletedAt = completed,
            EstimatedCost = 150.00m,
            ActualCost = 175.50m,
        });

        updated.Should().NotBeNull();

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.RequestedAt.Should().Be(requested);
        entity.ScheduledFor.Should().Be(scheduled.UtcDateTime);
        entity.CompletedAt.Should().Be(completed);
        entity.EstimatedCost.Should().Be(150.00m);
        entity.ActualCost.Should().Be(175.50m);
    }

    [Fact]
    public async Task UpdateAsync_EditsCostsAndTiming_OnCompletedOrder()
    {
        // Direct edit on a Completed order must work — there is deliberately no reopen workflow.
        var property = SeedProperty("Birch Lane");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Replace water heater",
            Description = "Old unit failed",
            Status = WorkOrderStatus.Completed,
        });

        var updated = await UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            ActualCost = 980.25m,
            CompletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkOrderStatus.Completed, "the edit must not change status");

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.ActualCost.Should().Be(980.25m);
        entity.CompletedAt.Should().Be(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateAsync_RejectsCompletedBeforeScheduled_WhenBothTimingFieldsAreSubmitted()
    {
        var property = SeedProperty("Sycamore Place");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Repair vanity leak",
            Description = "Supply line leak under the bathroom vanity",
            Status = WorkOrderStatus.InProgress,
        });

        var act = () => UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            ScheduledFor = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc),
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Be("The completion date can't be before the scheduled visit.");
    }

    [Fact]
    public async Task UpdateAsync_RejectsCompletedBeforeExistingScheduled_WhenCompletionIsSubmitted()
    {
        var property = SeedProperty("Spruce Court");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Repair tub drain",
            Description = "Tub drains slowly",
            Status = WorkOrderStatus.InProgress,
            ScheduledFor = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero),
        });

        var act = () => UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            CompletedAt = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc),
        });

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Be("The completion date can't be before the scheduled visit.");
    }

    [Fact]
    public async Task UpdateAsync_CompletesFutureScheduledWorkOrder_StampsCompletedAt_WithoutThrowing()
    {
        // A work order scheduled for the future must still one-click Complete (the vendor came early, or
        // the visit is later today). The status→Completed transition auto-stamps CompletedAt = now, which
        // is EXEMPT from the "before scheduled visit" guard — only a user-typed CompletedAt is range-checked.
        // Regression for BUG-1 (future-scheduled WO previously 400'd with a date the user never entered).
        var property = SeedProperty("Hawthorn Way");
        var futureVisit = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Fix porch light",
            Description = "Light out by the front door",
            Status = WorkOrderStatus.New,
            ScheduledFor = futureVisit,
        });

        var before = DateTime.UtcNow;
        var updated = await UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            Status = WorkOrderStatus.Completed,
        });

        updated.Should().NotBeNull("completing a future-scheduled work order must not throw");
        updated!.Status.Should().Be(WorkOrderStatus.Completed);

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.Status.Should().Be(WorkOrderStatus.Completed);
        entity.CompletedAt.Should().NotBeNull("the status→Completed transition auto-stamps the completion time");
        entity.CompletedAt!.Value.Should().BeOnOrAfter(before);
        entity.CompletedAt!.Value.Should().BeBefore(
            futureVisit.UtcDateTime, "the WO was completed early, before its future scheduled visit");
    }

    [Fact]
    public async Task UpdateAsync_NullCostsAndTiming_LeaveExistingValuesUnchanged()
    {
        var property = SeedProperty("Cedar Ave");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Paint hallway",
            Description = "Scuffed walls",
            Status = WorkOrderStatus.New,
            EstimatedCost = 200m,
            RequestedAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc),
        });

        // Only touch the title; every cost/timing field is null → unchanged.
        await UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            Title = "Paint upstairs hallway",
        });

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.EstimatedCost.Should().Be(200m);
        entity.RequestedAt.Should().Be(new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc));
        entity.Title.Should().Be("Paint upstairs hallway");
    }

    [Fact]
    public async Task UpdateAsync_ClearCostFlags_NullExistingCosts()
    {
        var property = SeedProperty("Willow Drive");
        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Replace disposal",
            Description = "Disposal motor seized",
            Status = WorkOrderStatus.New,
            EstimatedCost = 275m,
            ActualCost = 310m,
        });

        await UpdateAsync(created!.Id, new UpdateWorkOrderRequest
        {
            ClearEstimatedCost = true,
            ClearActualCost = true,
        });

        var entity = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == created.Id);
        entity.EstimatedCost.Should().BeNull();
        entity.ActualCost.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ProjectsPropertyAndVendorAndTenantNames()
    {
        var property = SeedProperty("Oak Terrace");
        var vendor = SeedVendor("Ace Plumbing");
        var tenant = SeedTenant("Maria", "Tenant");
        SeedActiveLease(property, tenant);

        var created = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            VendorId = vendor.Id,
            TenantId = tenant.Id,
            Title = "No hot water",
            Description = "Water heater out",
            Status = WorkOrderStatus.New,
        });

        await _serviceDb.Database.OpenConnectionAsync();
        await _serviceDb.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {_scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {_scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {_scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {_scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {_scope.AccessRevision.ToString()}, false);
            """);
        var detail = await _workOrders.GetAuthorizedAsync(_scope, created!.Id);

        detail.Should().NotBeNull();
        detail!.PropertyName.Should().Be("Oak Terrace");
        detail.VendorName.Should().Be("Ace Plumbing");
        detail.TenantName.Should().Be("Maria Tenant");
    }

    [Fact]
    public async Task GetAsync_HasActiveDispatch_TrueOnlyWithAnOpenDispatch_NotMereVendorAssignment()
    {
        // The detail page's "vendor has the job … closes on DONE" banner is driven by HasActiveDispatch,
        // which must reflect a REAL open VendorDispatch — not a mere vendor assignment. Regression for BUG-2.
        var property = SeedProperty("Magnolia Bend");
        var vendor = SeedVendor("Rapid HVAC");

        // (a) Vendor assigned but NO dispatch ever sent → false (the BUG-2 case).
        var assignedOnly = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            VendorId = vendor.Id,
            Title = "AC not cooling",
            Description = "Upstairs warm",
            Status = WorkOrderStatus.New,
        });

        // (b) Vendor assigned AND an OPEN (Dispatched) dispatch exists → true.
        var dispatched = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            VendorId = vendor.Id,
            Title = "Furnace dead",
            Description = "No heat",
            Status = WorkOrderStatus.New,
        });
        SeedDispatch(dispatched!.Id, vendor.Id, VendorDispatchStatus.Dispatched);

        // (c) A CLOSED (Completed) dispatch is not "open" → false.
        var closed = await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            VendorId = vendor.Id,
            Title = "Old job",
            Description = "Already done",
            Status = WorkOrderStatus.InProgress,
        });
        SeedDispatch(closed!.Id, vendor.Id, VendorDispatchStatus.Completed);

        await _serviceDb.Database.OpenConnectionAsync();
        await _serviceDb.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {_scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {_scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {_scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {_scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {_scope.AccessRevision.ToString()}, false);
            """);
        (await _workOrders.GetAuthorizedAsync(_scope, assignedOnly!.Id))!.HasActiveDispatch
            .Should().BeFalse("assigning a vendor without dispatching must not claim the job was sent");
        (await _workOrders.GetAuthorizedAsync(_scope, dispatched.Id))!.HasActiveDispatch
            .Should().BeTrue("an open dispatch means the job really is out with the vendor");
        (await _workOrders.GetAuthorizedAsync(_scope, closed.Id))!.HasActiveDispatch
            .Should().BeFalse("a completed dispatch is no longer awaiting a DONE reply");
    }

    [Fact]
    public async Task ListAsync_ProjectsPropertyName()
    {
        var property = SeedProperty("Pine Hollow");
        await CreateAsync(new CreateWorkOrderRequest
        {
            PropertyId = property.Id,
            Title = "Gutter cleaning",
            Description = "Leaves clogging",
            Status = WorkOrderStatus.New,
        });

        await _serviceDb.Database.OpenConnectionAsync();
        await _serviceDb.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {_scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {_scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {_scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {_scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {_scope.AccessRevision.ToString()}, false);
            """);
        var list = await _workOrders.ListAuthorizedAsync(_scope, propertyId: null, unitId: null, vendorId: null, new ListQuery());

        list.Should().ContainSingle();
        list[0].PropertyName.Should().Be("Pine Hollow");
    }

    private Property SeedProperty(string name, PropertyType type = PropertyType.SingleFamily)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            PropertyType = type,
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

    private Task<WorkOrderResponse?> CreateAsync(CreateWorkOrderRequest request) =>
        _workOrders.CreateAuthorizedAsync(_scope, request, Guid.NewGuid().ToString("N"));

    private Task<WorkOrderResponse?> UpdateAsync(int id, UpdateWorkOrderRequest request) =>
        _workOrders.UpdateAuthorizedAsync(_scope, id, request, Guid.NewGuid().ToString("N"));

    private Vendor SeedVendor(string name)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Vendors.Add(vendor);
        _db.SaveChanges();
        return vendor;
    }

    private VendorDispatch SeedDispatch(int workOrderId, int vendorId, VendorDispatchStatus status)
    {
        var dispatch = new VendorDispatch
        {
            PortfolioId = PortfolioId,
            WorkOrderId = workOrderId,
            VendorId = vendorId,
            Status = status,
            DispatchedAtUtc = DateTime.UtcNow,
        };
        _db.VendorDispatches.Add(dispatch);
        _db.SaveChanges();
        return dispatch;
    }

    private Tenant SeedTenant(string first, string last)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = first,
            LastName = last,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();
        return tenant;
    }

    private void SeedActiveLease(Property property, Tenant tenant)
    {
        var now = DateTime.UtcNow;
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = $"U-{tenant.Id}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Units.Add(unit);
        _db.SaveChanges();

        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"WO-{tenant.Id}",
            PlannedPossessionAtUtc = now.AddMonths(-1),
            PossessionGivenAtUtc = now.AddMonths(-1),
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
            ChangeReason = "Work-order test fixture",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        });
        _db.SaveChanges();
    }

    private sealed class NoopDataUpdate : IDataUpdateService
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
