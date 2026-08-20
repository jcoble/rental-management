using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AppointmentTenantNotificationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public AppointmentTenantNotificationPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        await _context.Db.Database.MigrateAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(AppointmentTenantNotificationPostgreSqlTests));
        var portfolio = await _context.Db.Portfolios.SingleAsync(row => row.Id == PortfolioId);
        portfolio.TimeZone = "America/New_York";
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        _services = AtomicDomainTestKernel.CreateForAppointmentsPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new RecordingCommandInterceptor(_commands)]);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task CreateAuthorizedAsync_LinkedTenantAppointmentCreatesOneTenantNotificationInSameAtomicCommand()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var request = Request(scenario);

        _commands.Clear();
        var created = await Service(_services).CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-tenant-notification");

        created.Should().NotBeNull();
        _context.Db.ChangeTracker.Clear();
        var notification = await _context.Db.Notifications.AsNoTracking()
            .SingleAsync(row => row.RelatedEntityType == nameof(Appointment)
                && row.RelatedEntityId == created!.Id);
        notification.UserId.Should().Be(scenario.TenantUserId);
        notification.Type.Should().Be("TenantAppointmentScheduled");
        notification.Title.Should().Be("Appointment scheduled");
        notification.Message.Should().Contain("Jan 26, 2027 at 9:00 AM America/New_York (UTC-05:00)");
        notification.Message.Should().NotContain("2:00 PM UTC");
        notification.NavigationExperience.Should().Be(NavigationExperience.Tenant);
        notification.NavigationDestination.Should().Be(NavigationDestination.Home);
        notification.NavigationAccessContextId.Should().Be(scenario.AccessContextId);
        notification.NavigationAccessRevision.Should().Be(scenario.AccessRevision);
        notification.NavigationResourceKind.Should().BeNull();
        notification.NavigationResourceId.Should().BeNull();
        notification.NavigationFallbackDestination.Should().Be(NavigationDestination.Home);
        notification.CreatedAt.Should().Be(BusinessNowUtc);
        created.WorkOrderId.Should().Be(scenario.WorkOrderId);

        var appointment = await _context.Db.Appointments.AsNoTracking()
            .SingleAsync(row => row.Id == created.Id);
        appointment.WorkOrderId.Should().Be(scenario.WorkOrderId);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey == "appointment-create:appointment-create-tenant-notification"))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.EntityType == nameof(Appointment)
                    && row.EntityId == created.Id
                    && row.Operation == AuditLogOperation.Created
                    && row.ChangeReason == "Created appointment."))
            .Should().Be(1);

        await AssertEveryAppointmentNotificationHasExactlyOneCreatedAuditAsync(created.Id);
        _commands.Should().Contain(command =>
            command.Contains("YS-187 appointment lifecycle tenant notification recipients", StringComparison.Ordinal)
            && command.Contains("\"TenantUserAccesses\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"LeaseManagementParties\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"Portfolios\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(command =>
            command.Contains("YS-266 appointment work order authorized context match", StringComparison.Ordinal)
            && command.Contains("\"WorkOrders\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAuthorizedAsync_WorkOrderContextMismatchDeniesAndCreatesNoRows()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var mismatch = await SeedWorkOrderAsync(
            scenario.PropertyId,
            scenario.UnitId,
            tenantId: null,
            scenario.LeaseManagementId,
            "Mismatched maintenance visit");
        var request = Request(scenario);
        request.WorkOrderId = mismatch;
        request.Title = "Mismatched work order appointment";

        var created = await Service(_services).CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-work-order-mismatch");

        created.Should().BeNull();
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Appointments.AsNoTracking()
                .CountAsync(row => row.Title == "Mismatched work order appointment"))
            .Should().Be(0);
        (await _context.Db.Notifications.AsNoTracking()
                .CountAsync(row => row.RelatedEntityType == nameof(Appointment)
                    && row.Type == "TenantAppointmentScheduled"))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(row => row.IdempotencyKey == "appointment-create:appointment-create-work-order-mismatch"))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.EntityType == nameof(Appointment)
                    && row.ChangeReason == "Created appointment."))
            .Should().Be(0);
    }

    [Fact]
    public async Task UpdateAuthorizedAsync_DistinctUpdateCommandsNotifyAndReplayDoesNotDuplicate()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var service = Service(_services);
        var created = await service.CreateAuthorizedAsync(
            _scope,
            Request(scenario),
            "appointment-create-before-update-notification");

        var firstUpdate = await service.UpdateAuthorizedAsync(
            _scope,
            created!.Id,
            new UpdateAppointmentRequest
            {
                Title = "Move-in walkthrough rescheduled",
                ScheduledStart = new DateTime(2027, 1, 26, 15, 0, 0, DateTimeKind.Utc),
                ScheduledEnd = new DateTime(2027, 1, 26, 16, 0, 0, DateTimeKind.Utc),
            },
            "appointment-update-tenant-notification");
        var firstReplay = await service.UpdateAuthorizedAsync(
            _scope,
            created.Id,
            new UpdateAppointmentRequest
            {
                Title = "Move-in walkthrough rescheduled",
                ScheduledStart = new DateTime(2027, 1, 26, 15, 0, 0, DateTimeKind.Utc),
                ScheduledEnd = new DateTime(2027, 1, 26, 16, 0, 0, DateTimeKind.Utc),
            },
            "appointment-update-tenant-notification");
        await service.UpdateAuthorizedAsync(
            _scope,
            created.Id,
            new UpdateAppointmentRequest
            {
                Title = "Move-in walkthrough confirmed",
                ScheduledStart = new DateTime(2027, 1, 26, 16, 0, 0, DateTimeKind.Utc),
                ScheduledEnd = new DateTime(2027, 1, 26, 17, 0, 0, DateTimeKind.Utc),
            },
            "appointment-second-update-tenant-notification");

        firstReplay!.Id.Should().Be(firstUpdate!.Id);
        _context.Db.ChangeTracker.Clear();
        (await CountAppointmentNotificationsAsync(created.Id, "TenantAppointmentScheduled"))
            .Should().Be(1);
        (await CountAppointmentNotificationsAsync(created.Id, "TenantAppointmentUpdated"))
            .Should().Be(2);
        (await _context.Db.Notifications.AsNoTracking()
                .CountAsync(row => row.RelatedEntityType == nameof(Appointment)
                    && row.RelatedEntityId == created.Id
                    && row.Type == "TenantAppointmentUpdated"
                    && row.Message.Contains("Jan 26, 2027 at 10:00 AM America/New_York (UTC-05:00)")))
            .Should().Be(1);
        (await _context.Db.Notifications.AsNoTracking()
                .CountAsync(row => row.RelatedEntityType == nameof(Appointment)
                    && row.RelatedEntityId == created.Id
                    && row.Type == "TenantAppointmentUpdated"
                    && row.Message.Contains("Jan 26, 2027 at 11:00 AM America/New_York (UTC-05:00)")))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "appointment.update"))
            .Should().Be(2);
        await AssertEveryAppointmentNotificationHasExactlyOneCreatedAuditAsync(created.Id);
    }

    [Fact]
    public async Task UpdateAuthorizedAsync_ExactStateNewKeyNoopsAndReplayReturnsCurrentSnapshotWithoutSideEffects()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var service = Service(_services);
        var created = await service.CreateAuthorizedAsync(
            _scope,
            Request(scenario),
            "appointment-create-before-noop-update");

        _context.Db.ChangeTracker.Clear();
        var baseline = await _context.Db.Appointments.AsNoTracking()
            .Where(row => row.Id == created!.Id)
            .Select(row => new
            {
                AppointmentUpdatedAt = row.UpdatedAt,
                AppointmentAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                    .Count(audit => audit.EntityType == nameof(Appointment)
                        && audit.EntityId == row.Id),
                NotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == row.Id),
                OutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.PortfolioId == row.PortfolioId),
                UpdateReceiptCount = _context.Db.AtomicCommandReceipts.AsNoTracking()
                    .Count(receipt => receipt.CommandType == "appointment.update"),
            })
            .SingleAsync();

        var request = new UpdateAppointmentRequest
        {
            AssignedTo = "Leasing Agent",
        };

        var noOp = await service.UpdateAuthorizedAsync(
            _scope,
            created!.Id,
            request,
            "appointment-noop-assigned-to-current");
        var replay = await service.UpdateAuthorizedAsync(
            _scope,
            created.Id,
            request,
            "appointment-noop-assigned-to-current");

        noOp.Should().NotBeNull();
        replay.Should().BeEquivalentTo(noOp);
        noOp!.UpdatedAt.Should().Be(baseline.AppointmentUpdatedAt);
        _context.Db.ChangeTracker.Clear();
        var after = await _context.Db.Appointments.AsNoTracking()
            .Where(row => row.Id == created.Id)
            .Select(row => new
            {
                AppointmentUpdatedAt = row.UpdatedAt,
                AppointmentAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                    .Count(audit => audit.EntityType == nameof(Appointment)
                        && audit.EntityId == row.Id),
                NotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == row.Id),
                OutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.PortfolioId == row.PortfolioId),
                NoOpUpdateOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.IdempotencyKey == "appointment-update:appointment-noop-assigned-to-current"),
                UpdateReceiptCount = _context.Db.AtomicCommandReceipts.AsNoTracking()
                    .Count(receipt => receipt.CommandType == "appointment.update"),
            })
            .SingleAsync();

        after.AppointmentUpdatedAt.Should().Be(baseline.AppointmentUpdatedAt);
        after.AppointmentAuditCount.Should().Be(baseline.AppointmentAuditCount);
        after.NotificationCount.Should().Be(baseline.NotificationCount);
        after.OutboxCount.Should().Be(baseline.OutboxCount);
        after.NoOpUpdateOutboxCount.Should().Be(0, "a pure no-op update must not stage an update outbox message");
        after.UpdateReceiptCount.Should().Be(baseline.UpdateReceiptCount + 1);
        (await CountAppointmentNotificationsAsync(created.Id, "TenantAppointmentUpdated"))
            .Should().Be(0);
    }

    [Fact]
    public async Task CreateAuthorizedAsync_ReplayDoesNotDuplicateTenantNotification()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var service = Service(_services);
        var request = Request(scenario);

        var first = await service.CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-tenant-notification-replay");
        var replay = await service.CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-tenant-notification-replay");

        replay!.Id.Should().Be(first!.Id);
        (await _context.Db.Notifications.AsNoTracking()
                .CountAsync(row => row.RelatedEntityType == nameof(Appointment)
                    && row.RelatedEntityId == first.Id
                    && row.Type == "TenantAppointmentScheduled"))
            .Should().Be(1);
        (await _context.Db.Appointments.AsNoTracking()
                .CountAsync(row => row.WorkOrderId == scenario.WorkOrderId))
            .Should().Be(1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.EntityType == nameof(Appointment)
                    && row.EntityId == first.Id
                    && row.Operation == AuditLogOperation.Created
                    && row.ChangeReason == "Created appointment."))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "appointment.create"))
            .Should().Be(1);
        await AssertEveryAppointmentNotificationHasExactlyOneCreatedAuditAsync(first.Id);
    }

    [Fact]
    public async Task CreateAuthorizedAsync_RollsBackAppointmentAuditAndOutboxWhenTenantNotificationInsertFails()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        await using var failingServices = AtomicDomainTestKernel.CreateForAppointmentsPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new ThrowOnNotificationInsertInterceptor()]);
        var request = Request(scenario);
        request.Title = "Notification failure appointment";

        var act = async () => await Service(failingServices).CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-notification-failure");

        await act.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedNotificationFailure);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Appointments.AsNoTracking()
                .CountAsync(item => item.Title == "Notification failure appointment"))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(item => item.EntityType == nameof(Appointment)
                    && item.ChangeReason == "Created appointment."))
            .Should().Be(0);
        (await _context.Db.Notifications.AsNoTracking()
                .CountAsync(item => item.Type == "TenantAppointmentScheduled"))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(item => item.IdempotencyKey == "appointment-create:appointment-create-notification-failure"))
            .Should().Be(0);
    }

    private AppointmentService Service(ServiceProvider services) => new(
        services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());

    private async Task<int> CountAppointmentNotificationsAsync(int appointmentId, string type)
    {
        return await _context.Db.Notifications.AsNoTracking()
            .CountAsync(row => row.RelatedEntityType == nameof(Appointment)
                && row.RelatedEntityId == appointmentId
                && row.Type == type);
    }

    private async Task AssertEveryAppointmentNotificationHasExactlyOneCreatedAuditAsync(int appointmentId)
    {
        var notifications = _context.Db.Notifications.AsNoTracking()
            .Where(row => row.RelatedEntityType == nameof(Appointment)
                && row.RelatedEntityId == appointmentId);
        var notificationCount = await notifications.CountAsync();
        var notificationsWithOneCreatedAudit = await notifications.CountAsync(notification =>
            _context.Db.AtomicAuditLogs.AsNoTracking()
                .Count(audit => audit.EntityType == nameof(Notification)
                    && audit.EntityId == notification.Id
                    && audit.Operation == AuditLogOperation.Created) == 1);

        notificationsWithOneCreatedAudit.Should().Be(notificationCount);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(audit => audit.EntityType == nameof(Notification)
                    && audit.ChangeReason == "Appointment lifecycle tenant notification committed."
                    && _context.Db.Notifications.AsNoTracking().Any(notification =>
                        notification.Id == audit.EntityId
                        && notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == appointmentId)))
            .Should().Be(0);
    }

    private async Task<TenantAppointmentScenario> SeedLeasedTenantAppointmentScenarioAsync()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Tenant Appointment Apartments {Guid.NewGuid():N}",
            AddressLine1 = "100 Simulation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = $"A{Guid.NewGuid():N}"[..8],
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Jordan",
            LastName = "Miles",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var tenantEmail = $"appointment-tenant-{Guid.NewGuid():N}@example.test";
        var tenantUser = new ApplicationUser
        {
            UserName = tenantEmail,
            NormalizedUserName = tenantEmail.ToUpperInvariant(),
            Email = tenantEmail,
            NormalizedEmail = tenantEmail.ToUpperInvariant(),
            DisplayName = "Appointment Tenant",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = SeededAtUtc,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            PublicId = Guid.NewGuid(),
            RelationshipNumber = $"LM-APPT-{Guid.NewGuid():N}",
            CreatedByUserId = _scope.UserId,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(SeededAtUtc.AddDays(-1)),
            ChangeReason = "Appointment tenant notification fixture",
            CreatedByUserId = _scope.UserId,
            CreatedAtUtc = SeededAtUtc,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = tenantUser,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var tenantAccess = new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            AccessContext = accessContext,
            ApplicationUser = tenantUser,
            LeaseManagementParty = party,
            GrantedAtUtc = SeededAtUtc,
            GrantedByUserId = _scope.UserId,
            Reason = "Appointment tenant notification fixture",
        };

        _context.Db.Add(tenantAccess);
        await _context.Db.SaveChangesAsync();
        var workOrderId = await SeedWorkOrderAsync(
            property.Id,
            unit.Id,
            tenant.Id,
            relationship.Id,
            "Move-in walkthrough maintenance visit");
        _context.Db.ChangeTracker.Clear();
        return new TenantAppointmentScenario(
            property.Id,
            unit.Id,
            relationship.Id,
            tenant.Id,
            workOrderId,
            tenantUser.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private async Task<int> SeedWorkOrderAsync(
        int propertyId,
        int? unitId,
        int? tenantId,
        int? leaseManagementId,
        string title)
    {
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            TenantId = tenantId,
            LeaseManagementId = leaseManagementId,
            Title = title,
            Description = "Appointment-linked maintenance visit fixture",
            Category = "Maintenance",
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.Scheduled,
            RequestedAt = SeededAtUtc,
            ScheduledFor = new DateTime(2027, 1, 26, 14, 0, 0, DateTimeKind.Utc),
            ScheduledWindowEnd = new DateTime(2027, 1, 26, 15, 0, 0, DateTimeKind.Utc),
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        return workOrder.Id;
    }

    private static CreateAppointmentRequest Request(TenantAppointmentScenario scenario) => new()
    {
        PropertyId = scenario.PropertyId,
        UnitId = scenario.UnitId,
        LeaseManagementId = scenario.LeaseManagementId,
        TenantId = scenario.TenantId,
        WorkOrderId = scenario.WorkOrderId,
        Title = "Frozen-clock move-in walkthrough",
        Type = AppointmentType.MoveIn,
        Status = AppointmentStatus.Scheduled,
        ScheduledStart = new DateTime(2027, 1, 26, 14, 0, 0, DateTimeKind.Utc),
        ScheduledEnd = new DateTime(2027, 1, 26, 15, 0, 0, DateTimeKind.Utc),
        AssignedTo = "Leasing Agent",
    };

    private sealed record TenantAppointmentScenario(
        int PropertyId,
        int UnitId,
        int LeaseManagementId,
        int TenantId,
        int WorkOrderId,
        int TenantUserId,
        int AccessContextId,
        long AccessRevision);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnNotificationInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfNotificationInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfNotificationInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfNotificationInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"Notifications\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedNotificationFailure();
            }
        }
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedNotificationFailure : Exception;
}
