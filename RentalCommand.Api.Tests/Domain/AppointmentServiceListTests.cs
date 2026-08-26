using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public class AppointmentServiceListTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private AppointmentService _sut = null!;
    private WorkspaceReadScope _scope;

    public AppointmentServiceListTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(AppointmentServiceListTests));
        _sut = new AppointmentService(_ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ListPageAsync_FiltersSortsAndPagesInSql()
    {
        SeedAppointment("A-100", "Cedar Point Flats", AppointmentType.Showing, AppointmentStatus.Scheduled);
        SeedAppointment("B-200", "Elm Ridge Homes", AppointmentType.Showing, AppointmentStatus.Scheduled);
        SeedAppointment("C-300", "Harbor View Apartments", AppointmentType.Showing, AppointmentStatus.Scheduled);
        SeedAppointment("D-400", "West Market Lofts", AppointmentType.Inspection, AppointmentStatus.Scheduled);
        SeedAppointment("E-500", "York House", AppointmentType.Showing, AppointmentStatus.Cancelled);

        await _ctx.ActivateApiScopeAsync(_scope);
        _commands.Clear();
        var result = await _sut.ListPageAuthorizedAsync(_scope, new AppointmentListQuery
        {
            Type = AppointmentType.Showing,
            Status = AppointmentStatus.Scheduled,
            Sort = "propertyName",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(a => a.PropertyName).Should().Equal("Elm Ridge Homes", "Harbor View Apartments");
        result.Items.Should().OnlyContain(a => a.Type == AppointmentType.Showing);
        result.Items.Should().OnlyContain(a => a.Status == AppointmentStatus.Scheduled);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Appointments\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Properties", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedAppointment(string title, string propertyName, AppointmentType type, AppointmentStatus status)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Appointments.Add(new Appointment
        {
            PortfolioId = PortfolioId,
            Property = new Property
            {
                PortfolioId = PortfolioId,
                Name = propertyName,
                AddressLine1 = "100 Test Street",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            },
            Title = title,
            Type = type,
            Status = status,
            ScheduledStart = now.AddDays(1),
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
