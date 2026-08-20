using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AppointmentMutationAuthorizationTests : IDisposable
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2027, 1, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly ServiceProvider _services;
    private readonly AppointmentService _sut;

    public AppointmentMutationAuthorizationTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _services = AtomicDomainTestKernel.CreateForAppointments(
            _ctx.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(SeededAtUtc)),
            [new RecordingCommandInterceptor(_commands)]);
        _sut = new AppointmentService(
            _services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
            Mock.Of<IDataUpdateService>(),
            new FixedTimeProvider(new DateTimeOffset(SeededAtUtc)),
            _services.GetRequiredService<IRequestWriteExecutor>());
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task CreateAuthorizedAsync_AllowsPropertyManagerMaintenanceVisitAndAuthorizedReplay()
    {
        var scenario = SeedLeasedUnit();
        var scope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, scenario.PropertyId, nameof(CreateAuthorizedAsync_AllowsPropertyManagerMaintenanceVisitAndAuthorizedReplay));
        var request = MaintenanceVisitRequest(scenario);

        _commands.Clear();
        var created = await _sut.CreateAuthorizedAsync(scope, request, "zenith-maintenance-appointment");
        var replayed = await _sut.CreateAuthorizedAsync(scope, request, "zenith-maintenance-appointment");

        created.Should().NotBeNull();
        replayed.Should().NotBeNull();
        replayed!.Id.Should().Be(created!.Id);
        replayed.Type.Should().Be(AppointmentType.MaintenanceVisit);
        replayed.TenantId.Should().Be(scenario.TenantId);
        replayed.LeaseManagementId.Should().Be(scenario.LeaseManagementId);

        var updated = await _sut.UpdateAuthorizedAsync(
            scope,
            created.Id,
            new UpdateAppointmentRequest
            {
                Title = "Roof flashing leak follow-up - Zenith Unit A",
                Notes = "Confirm flashing seal and interior drywall moisture.",
            },
            "zenith-maintenance-appointment-update");
        var updateReplay = await _sut.UpdateAuthorizedAsync(
            scope,
            created.Id,
            new UpdateAppointmentRequest
            {
                Title = "Roof flashing leak follow-up - Zenith Unit A",
                Notes = "Confirm flashing seal and interior drywall moisture.",
            },
            "zenith-maintenance-appointment-update");

        updated.Should().NotBeNull();
        updateReplay.Should().NotBeNull();
        updateReplay!.Id.Should().Be(created.Id);
        updateReplay.Title.Should().Be("Roof flashing leak follow-up - Zenith Unit A");

        (await _ctx.Db.Appointments.CountAsync(item => item.PortfolioId == PortfolioId))
            .Should().Be(1);
        _commands.Should().Contain(command =>
            command.Contains("LeaseManagementParties", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(command =>
            command.Contains("RoleProfileCapabilities", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("CapabilityDefinitions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateAuthorizedAsync_RejectsTenantOutsideSelectedLeaseManagement()
    {
        var scenario = SeedLeasedUnit();
        var unrelatedTenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Wrong",
            LastName = "Tenant",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _ctx.Db.Tenants.Add(unrelatedTenant);
        await _ctx.Db.SaveChangesAsync();
        var scope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, scenario.PropertyId, nameof(CreateAuthorizedAsync_RejectsTenantOutsideSelectedLeaseManagement));
        var request = MaintenanceVisitRequest(scenario);
        request.TenantId = unrelatedTenant.Id;

        var created = await _sut.CreateAuthorizedAsync(scope, request, "zenith-maintenance-wrong-tenant");

        created.Should().BeNull();
        (await _ctx.Db.Appointments.CountAsync(item => item.PortfolioId == PortfolioId))
            .Should().Be(0);
    }

    [Fact]
    public async Task CreateAndUpdateAuthorizedAsync_RejectAnEndThatIsNotAfterTheStart()
    {
        var scenario = SeedLeasedUnit();
        var scope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, scenario.PropertyId, nameof(CreateAndUpdateAuthorizedAsync_RejectAnEndThatIsNotAfterTheStart));
        var request = MaintenanceVisitRequest(scenario);
        request.ScheduledEnd = request.ScheduledStart;

        var create = () => _sut.CreateAuthorizedAsync(
            scope, request, "appointment-equal-end-create");
        await create.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("The end time must be after the start time");

        request.ScheduledEnd = request.ScheduledStart.AddHours(1);
        var created = await _sut.CreateAuthorizedAsync(scope, request, "appointment-equal-end-seed");
        created.Should().NotBeNull();

        var update = () => _sut.UpdateAuthorizedAsync(
            scope,
            created!.Id,
            new UpdateAppointmentRequest
            {
                ScheduledStart = request.ScheduledStart,
                ScheduledEnd = request.ScheduledStart,
            },
            "appointment-equal-end-update");
        await update.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("The end time must be after the start time");
    }

    private Scenario SeedLeasedUnit()
    {
        var actorEmail = $"appointment-fixture-{Guid.NewGuid():N}@example.test";
        var actor = new ApplicationUser
        {
            UserName = actorEmail,
            NormalizedUserName = actorEmail.ToUpperInvariant(),
            Email = actorEmail,
            NormalizedEmail = actorEmail.ToUpperInvariant(),
            DisplayName = "Appointment Fixture Actor",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Zenith",
            AddressLine1 = "100 Zenith Way",
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
            UnitNumber = "A",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Nolan",
            LastName = "Flores",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var leaseManagement = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            PublicId = Guid.NewGuid(),
            RelationshipNumber = $"LM-{Guid.NewGuid():N}",
            CreatedByUser = actor,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        leaseManagement.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(SeededAtUtc),
            ChangeReason = "Regression fixture.",
            CreatedByUser = actor,
            CreatedAtUtc = SeededAtUtc,
        });

        _ctx.Db.LeaseManagements.Add(leaseManagement);
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
        return new Scenario(property.Id, unit.Id, leaseManagement.Id, tenant.Id);
    }

    private static CreateAppointmentRequest MaintenanceVisitRequest(Scenario scenario) => new()
    {
        PropertyId = scenario.PropertyId,
        UnitId = scenario.UnitId,
        LeaseManagementId = scenario.LeaseManagementId,
        TenantId = scenario.TenantId,
        Title = "Roof flashing leak visit - Zenith Unit A",
        Type = AppointmentType.MaintenanceVisit,
        Status = AppointmentStatus.Scheduled,
        ScheduledStart = new DateTime(2027, 1, 25, 15, 0, 0, DateTimeKind.Utc),
        ScheduledEnd = new DateTime(2027, 1, 25, 16, 0, 0, DateTimeKind.Utc),
        AssignedTo = "Casey Technician / Summit Roofing coordination",
    };

    private sealed record Scenario(
        int PropertyId,
        int UnitId,
        int LeaseManagementId,
        int TenantId);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
}
