using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class AppointmentMutationClockPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public AppointmentMutationClockPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(AppointmentMutationClockPostgreSqlTests));
        _services = AtomicDomainTestKernel.CreateForAppointmentsPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task CreateUpdateAndDeleteAuthorizedAsync_UseInjectedBusinessClockForAtomicMutationRows()
    {
        var property = await SeedPropertyAsync();
        var service = Service(_services);

        var created = await service.CreateAuthorizedAsync(
            _scope,
            Request(property.Id),
            "appointment-create-sim-clock");

        created.Should().NotBeNull();
        created!.CreatedAt.Should().Be(BusinessNowUtc);
        created.UpdatedAt.Should().Be(BusinessNowUtc);

        _context.Db.ChangeTracker.Clear();
        var createdRow = await MutationRowAsync(
            created.Id,
            AuditLogOperation.Created,
            "appointment-create:appointment-create-sim-clock");
        createdRow.CreatedAt.Should().Be(BusinessNowUtc);
        createdRow.UpdatedAt.Should().Be(BusinessNowUtc);
        createdRow.AuditTimestamp.Should().Be(BusinessNowUtc);
        createdRow.OutboxCreatedAtUtc.Should().Be(BusinessNowUtc);
        createdRow.OutboxNextAttemptAtUtc.Should().Be(BusinessNowUtc);

        var updated = await service.UpdateAuthorizedAsync(
            _scope,
            created.Id,
            new UpdateAppointmentRequest
            {
                Title = "Frozen-clock move-in walkthrough follow-up",
                Status = AppointmentStatus.Confirmed,
            },
            "appointment-update-sim-clock");

        updated.Should().NotBeNull();
        updated!.CreatedAt.Should().Be(BusinessNowUtc);
        updated.UpdatedAt.Should().Be(BusinessNowUtc);

        _context.Db.ChangeTracker.Clear();
        var updatedRow = await MutationRowAsync(
            created.Id,
            AuditLogOperation.Updated,
            "appointment-update:appointment-update-sim-clock");
        updatedRow.CreatedAt.Should().Be(BusinessNowUtc);
        updatedRow.UpdatedAt.Should().Be(BusinessNowUtc);
        updatedRow.AuditTimestamp.Should().Be(BusinessNowUtc);
        updatedRow.OutboxCreatedAtUtc.Should().Be(BusinessNowUtc);
        updatedRow.OutboxNextAttemptAtUtc.Should().Be(BusinessNowUtc);

        var deleted = await service.DeleteAuthorizedAsync(
            _scope,
            created.Id,
            property.Id,
            "appointment-delete-sim-clock");

        deleted.Should().BeTrue();

        _context.Db.ChangeTracker.Clear();
        var deleteRow = await (
            from audit in _context.Db.AtomicAuditLogs.AsNoTracking()
            join outbox in _context.Db.OutboxMessages.AsNoTracking()
                on audit.PortfolioId equals outbox.PortfolioId
            where audit.EntityType == nameof(Appointment)
                && audit.EntityId == created.Id
                && audit.Operation == AuditLogOperation.Deleted
                && outbox.IdempotencyKey == "appointment-delete:appointment-delete-sim-clock"
            select new
            {
                AuditTimestamp = audit.Timestamp,
                OutboxCreatedAtUtc = outbox.CreatedAtUtc,
                OutboxNextAttemptAtUtc = outbox.NextAttemptAtUtc,
            }).SingleAsync();

        deleteRow.AuditTimestamp.Should().Be(BusinessNowUtc);
        deleteRow.OutboxCreatedAtUtc.Should().Be(BusinessNowUtc);
        deleteRow.OutboxNextAttemptAtUtc.Should().Be(BusinessNowUtc);
        (await _context.Db.Appointments.AsNoTracking().AnyAsync(item => item.Id == created.Id))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CreateAuthorizedAsync_RollsBackAppointmentAndAuditWhenOutboxInsertFails()
    {
        var property = await SeedPropertyAsync();
        await using var failingServices = AtomicDomainTestKernel.CreateForAppointmentsPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new ThrowOnOutboxInsertInterceptor()]);
        var service = Service(failingServices);

        var request = Request(property.Id);
        request.Title = "Outbox failure appointment";
        var act = async () => await service.CreateAuthorizedAsync(
            _scope,
            request,
            "appointment-create-outbox-failure");

        await act.Should().ThrowAsync<DbUpdateException>()
            .Where(exception => exception.InnerException is InjectedOutboxFailure);

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Appointments.AsNoTracking()
                .CountAsync(item => item.Title == "Outbox failure appointment"))
            .Should().Be(0);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
                .CountAsync(item => item.EntityType == nameof(Appointment) &&
                    item.ChangeReason == "Created appointment."))
            .Should().Be(0);
        (await _context.Db.OutboxMessages.AsNoTracking()
                .CountAsync(item => item.IdempotencyKey == "appointment-create:appointment-create-outbox-failure"))
            .Should().Be(0);
    }

    private AppointmentService Service(ServiceProvider services) => new(
        services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());

    private async Task<Property> SeedPropertyAsync()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Frozen Clock Apartments {Guid.NewGuid():N}",
            AddressLine1 = "100 Simulation Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return property;
    }

    private static CreateAppointmentRequest Request(int propertyId) => new()
    {
        PropertyId = propertyId,
        Title = "Frozen-clock move-in walkthrough",
        Type = AppointmentType.MoveIn,
        Status = AppointmentStatus.Scheduled,
        ScheduledStart = new DateTime(2027, 1, 25, 15, 0, 0, DateTimeKind.Utc),
        ScheduledEnd = new DateTime(2027, 1, 25, 16, 0, 0, DateTimeKind.Utc),
        AssignedTo = "Leasing Agent",
    };

    private Task<MutationRow> MutationRowAsync(
        int appointmentId,
        AuditLogOperation operation,
        string outboxKey) =>
        (
            from appointment in _context.Db.Appointments.AsNoTracking()
            join audit in _context.Db.AtomicAuditLogs.AsNoTracking()
                on appointment.Id equals audit.EntityId
            join outbox in _context.Db.OutboxMessages.AsNoTracking()
                on appointment.PortfolioId equals outbox.PortfolioId
            where appointment.Id == appointmentId
                && audit.EntityType == nameof(Appointment)
                && audit.Operation == operation
                && outbox.IdempotencyKey == outboxKey
            select new MutationRow(
                appointment.CreatedAt,
                appointment.UpdatedAt,
                audit.Timestamp,
                outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc))
        .SingleAsync();

    private sealed record MutationRow(
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime AuditTimestamp,
        DateTime OutboxCreatedAtUtc,
        DateTime OutboxNextAttemptAtUtc);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnOutboxInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfOutboxInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOutboxInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfOutboxInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase))
            {
                throw new InjectedOutboxFailure();
            }
        }
    }

    private sealed class InjectedOutboxFailure : Exception;
}
