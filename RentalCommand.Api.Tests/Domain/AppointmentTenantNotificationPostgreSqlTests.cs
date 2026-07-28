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

        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(row => row.EntityType == nameof(Notification)
                    && row.EntityId == notification.Id
                    && row.ChangeReason == "Appointment lifecycle tenant notification committed."))
            .Should().Be(1);
        _commands.Should().Contain(command =>
            command.Contains("YS-187 appointment lifecycle tenant notification recipients", StringComparison.Ordinal)
            && command.Contains("\"TenantUserAccesses\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"LeaseManagementParties\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("\"Portfolios\"", StringComparison.OrdinalIgnoreCase)
            && command.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpdateAuthorizedAsync_NotifiesTenantForUpdateAndCancellationLifecycle()
    {
        var scenario = await SeedLeasedTenantAppointmentScenarioAsync();
        var service = Service(_services);
        var created = await service.CreateAuthorizedAsync(
            _scope,
            Request(scenario),
            "appointment-create-before-update-notification");

        await service.UpdateAuthorizedAsync(
            _scope,
            created!.Id,
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
            new UpdateAppointmentRequest { Status = AppointmentStatus.Cancelled },
            "appointment-cancel-tenant-notification");

        _context.Db.ChangeTracker.Clear();
        var notificationTypes = await _context.Db.Notifications.AsNoTracking()
            .Where(row => row.RelatedEntityType == nameof(Appointment)
                && row.RelatedEntityId == created.Id)
            .OrderBy(row => row.Type)
            .Select(row => row.Type)
            .ToListAsync();
        notificationTypes.Should().BeEquivalentTo(
            "TenantAppointmentScheduled",
            "TenantAppointmentUpdated",
            "TenantAppointmentCancelled");
        var updatedMessage = await _context.Db.Notifications.AsNoTracking()
            .Where(row => row.RelatedEntityType == nameof(Appointment)
                && row.RelatedEntityId == created.Id
                && row.Type == "TenantAppointmentUpdated")
            .Select(row => row.Message)
            .SingleAsync();
        updatedMessage.Should().Contain("Jan 26, 2027 at 10:00 AM America/New_York (UTC-05:00)");
        updatedMessage.Should().NotContain("3:00 PM UTC");
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
        (await _context.Db.AtomicCommandReceipts.AsNoTracking()
                .CountAsync(row => row.CommandType == "appointment.create"))
            .Should().Be(1);
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
        _context.Db,
        Mock.Of<IDataUpdateService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        services.GetRequiredService<IAtomicUnitOfWork>());

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
        _context.Db.ChangeTracker.Clear();
        return new TenantAppointmentScenario(
            property.Id,
            unit.Id,
            relationship.Id,
            tenant.Id,
            tenantUser.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static CreateAppointmentRequest Request(TenantAppointmentScenario scenario) => new()
    {
        PropertyId = scenario.PropertyId,
        UnitId = scenario.UnitId,
        LeaseManagementId = scenario.LeaseManagementId,
        TenantId = scenario.TenantId,
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
