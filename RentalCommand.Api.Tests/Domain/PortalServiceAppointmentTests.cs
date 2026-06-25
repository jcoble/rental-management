using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class PortalServiceAppointmentTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly PortalService _sut;

    public PortalServiceAppointmentTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new PortalService(_ctx.Db, new NoopLeaseQaService());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetAppointmentsAsync_ReturnsUpcomingTenantAppointmentsFromSingleLimitedSqlProjection()
    {
        var target = SeedTenant("Blake", "Hayes");
        var other = SeedTenant("Other", "Tenant");
        SeedAppointment(
            target,
            "Pass 51 service visit date fixed",
            "Riverside Flats",
            "2B",
            DateTime.UtcNow.AddDays(3),
            AppointmentStatus.Scheduled);
        SeedAppointment(
            other,
            "Other tenant visit",
            "Other Property",
            "1A",
            DateTime.UtcNow.AddDays(4),
            AppointmentStatus.Scheduled);
        SeedAppointment(
            target,
            "Cancelled tenant visit",
            "Riverside Flats",
            "2B",
            DateTime.UtcNow.AddDays(5),
            AppointmentStatus.Cancelled);
        SeedAppointment(
            target,
            "Past tenant visit",
            "Riverside Flats",
            "2B",
            DateTime.UtcNow.AddDays(-1),
            AppointmentStatus.Scheduled);

        _commands.Clear();

        var result = await _sut.GetAppointmentsAsync(PortfolioId, target.Id);

        result.Should().ContainSingle();
        var appointment = result.Single();
        appointment.Title.Should().Be("Pass 51 service visit date fixed");
        appointment.TenantId.Should().Be(target.Id);
        appointment.TenantName.Should().Be("Blake Hayes");
        appointment.PropertyName.Should().Be("Riverside Flats");
        appointment.UnitNumber.Should().Be("2B");

        var appointmentQueries = _commands
            .Where(sql => sql.Contains("FROM \"Appointments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        appointmentQueries.Should().ContainSingle("portal appointments should be projected in one limited DB query");
        appointmentQueries[0].Should().Contain("\"TenantId\"");
        appointmentQueries[0].Should().Contain("ORDER BY");
        appointmentQueries[0].Should().Contain("LIMIT");
    }

    [Fact]
    public async Task GetAppointmentsAsync_FallsBackToActiveLeaseUnitForTenantScopedAppointments()
    {
        var target = SeedTenant("Blake", "Hayes");
        var activeLease = SeedActiveLease(target, "Riverside Flats", "2B");
        SeedTenantScopedAppointmentWithoutUnit(
            target,
            activeLease.Property!,
            "Pass 51 service visit date fixed",
            DateTime.UtcNow.AddDays(3),
            AppointmentStatus.Scheduled);

        _commands.Clear();

        var result = await _sut.GetAppointmentsAsync(PortfolioId, target.Id);

        result.Should().ContainSingle();
        var appointment = result.Single();
        appointment.UnitId.Should().BeNull();
        appointment.PropertyName.Should().Be("Riverside Flats");
        appointment.UnitNumber.Should().Be("2B");

        var appointmentQueries = _commands
            .Where(sql => sql.Contains("FROM \"Appointments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        appointmentQueries.Should().ContainSingle("tenant appointment unit fallback should remain in the appointment projection query");
        appointmentQueries[0].Should().Contain("FROM \"Leases\"", "the fallback unit should be projected SQL-side from the tenant's active lease");
    }

    private Tenant SeedTenant(string firstName, string lastName)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private void SeedAppointment(
        Tenant tenant,
        string title,
        string propertyName,
        string unitNumber,
        DateTime scheduledStart,
        AppointmentStatus status)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "1188 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43201",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Appointments.Add(new Appointment
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Property = property,
            Unit = new Unit
            {
                Property = property,
                UnitNumber = unitNumber,
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1200m,
                CreatedAt = now,
                UpdatedAt = now,
            },
            Title = title,
            Type = AppointmentType.MaintenanceVisit,
            Status = status,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledStart.AddMinutes(45),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private Lease SeedActiveLease(Tenant tenant, string propertyName, string unitNumber)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "1188 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43201",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-PORTAL-001",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-3),
            EndDate = now.AddMonths(9),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private void SeedTenantScopedAppointmentWithoutUnit(
        Tenant tenant,
        Property property,
        string title,
        DateTime scheduledStart,
        AppointmentStatus status)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Appointments.Add(new Appointment
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Property = property,
            Title = title,
            Type = AppointmentType.MaintenanceVisit,
            Status = status,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledStart.AddMinutes(45),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
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
