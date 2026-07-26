using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class ReportsServicePostgreSqlTests(MigratedPostgreSqlFixture postgres)
{
    [Fact]
    public async Task GeneralLedger_ExecutesOneAuthorizedWindowQueryOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(GeneralLedger_ExecutesOneAuthorizedWindowQueryOnPostgreSql));
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Maple",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Db.Properties.Add(property);
        await context.Db.SaveChangesAsync();
        context.Db.Expenses.Add(new Expense
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            OperationalScope = ExpenseOperationalScope.Property,
            Category = ScheduleECategory.Repairs,
            Description = "Plumbing repair",
            Status = ExpenseStatus.Paid,
            Amount = 125m,
            IncurredAt = now,
            PaidAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await context.Db.SaveChangesAsync();
        await context.Db.Database.OpenConnectionAsync();
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);

        var sut = new ReportsService(
            context.Db,
            new OwnerStatementService(context.Db, TimeProvider.System),
            new ScheduleEService(context.Db),
            new PropertyDispositionService(context.Db, TimeProvider.System),
            TimeProvider.System);

        var report = await sut.GetGeneralLedgerAsync(scope, new ReportRangeQuery
        {
            From = now.AddDays(-1),
            To = now.AddDays(1),
        });

        var entry = report.Entries.Should().ContainSingle().Subject;
        entry.Description.Should().Be("Plumbing repair");
        entry.Amount.Should().Be(-125m);
        entry.RunningBalance.Should().Be(-125m);
        report.TotalIncome.Should().Be(0m);
        report.TotalExpense.Should().Be(125m);
        report.ClosingBalance.Should().Be(-125m);
    }

    [Fact]
    public async Task RentLedger_ExecutesCanonicalCapabilityScopePipelineOnPostgreSql()
    {
        await using var context = await postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            1, nameof(RentLedger_ExecutesCanonicalCapabilityScopePipelineOnPostgreSql));
        await ActivateApiScopeAsync(context, scope);
        var sut = new ReportsService(
            context.Db,
            new OwnerStatementService(context.Db, TimeProvider.System),
            new ScheduleEService(context.Db),
            new PropertyDispositionService(context.Db, TimeProvider.System),
            TimeProvider.System);
        var access = new LeaseManagementReadContext(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision);

        var report = await sut.GetRentLedgerAsync(access, new ReportRangeQuery());

        report.Leases.Should().BeEmpty();
        report.TotalCharged.Should().Be(0m);
        report.TotalCredits.Should().Be(0m);
        report.TotalBalance.Should().Be(0m);
    }

    private static async Task ActivateApiScopeAsync(
        MigratedPostgreSqlTestContext context,
        RentalCommand.Core.Authorization.WorkspaceReadScope scope)
    {
        await context.Db.Database.OpenConnectionAsync();
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
    }
}
