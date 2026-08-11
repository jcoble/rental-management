using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class UnitConditionPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime Now =
        new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<CapturedCommand> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public UnitConditionPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task ConditionFamilies_ProjectIndependentlyInPostgreSql()
    {
        var property = await SeedPropertyAsync("Independent Conditions");
        var unit = await SeedUnitAsync(property, "1A");
        var relationship = await SeedPossessionWithoutAgreementAsync(property, unit);
        var account = await SeedTenantAccountAsync(relationship);

        _commands.Clear();
        var dashboard = await DashboardService().GetDashboardAsync(
            PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.CurrentLease.Should().BeNull();
        dashboard.OccupancyPossession.Status.Should().Be("Occupied");
        dashboard.OccupancyPossession.LeaseManagementId.Should().Be(relationship.Id);
        dashboard.MarketingAvailability.Status.Should().Be("NotAvailable");
        dashboard.TenantAccountCondition.TenantAccountId.Should().Be(account.Id);
        dashboard.TenantAccountCondition.Status.Should().Be("Current");
        dashboard.LegalNoticeCondition.Status.Should().Be("NoGoverningAgreement");
        dashboard.MaintenanceTurnover.Status.Should().NotBeNullOrWhiteSpace();

        var dashboardSql = _commands.Single(command =>
            command.Sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
        dashboardSql.Sql.Should().Contain("vw_lease_management_lifecycle");
        dashboardSql.Sql.Should().Contain("NoticeDrafts");
        Capture("CONDITION_FAMILIES", [dashboardSql]);
    }

    [Fact]
    public async Task TurnoverSummary_AggregatesInPostgreSql()
    {
        var property = await SeedPropertyAsync("Turnover Aggregate");
        var unit = await SeedUnitAsync(property, "2A");
        var sourceRelationship = await SeedPossessionWithoutAgreementAsync(property, unit);
        sourceRelationship.PossessionReturnedAtUtc = Now.AddDays(-4);
        sourceRelationship.UpdatedAtUtc = Now.AddDays(-4);
        await _context.Db.SaveChangesAsync();
        _context.Db.UnitOperationalPeriods.Add(new UnitOperationalPeriod
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Type = UnitOperationalPeriodType.Turnover,
            StartedAtUtc = Now.AddDays(-4),
            SourceLeaseManagementId = sourceRelationship.Id,
            Reason = "Make-ready",
            CreatedAtUtc = Now.AddDays(-4),
            CreatedByUserId = 1,
        });
        var open = WorkOrder(unit, "Paint", WorkOrderStatus.InProgress, 200m, null);
        var completed = WorkOrder(unit, "Clean", WorkOrderStatus.Completed, 100m, 80m);
        _context.Db.AddRange(open, completed);
        await _context.Db.SaveChangesAsync();
        _context.Db.Expenses.Add(new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = ExpenseOperationalScope.WorkOrder,
            PropertyId = property.Id,
            UnitId = unit.Id,
            WorkOrderId = open.Id,
            Description = "Paint supplies",
            Category = ScheduleECategory.Repairs,
            Status = ExpenseStatus.Paid,
            Amount = 50m,
            IncurredAt = Now.AddDays(-1),
            PaidAt = Now.AddDays(-1),
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await _context.Db.SaveChangesAsync();

        _commands.Clear();
        var dashboard = await DashboardService().GetDashboardAsync(
            PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        dashboard!.Turnover.TotalTaskCount.Should().Be(2);
        dashboard.Turnover.OpenTaskCount.Should().Be(1);
        dashboard.Turnover.CompletedTaskCount.Should().Be(1);
        dashboard.Turnover.EstimatedCost.Should().Be(300m);
        dashboard.Turnover.ActualCost.Should().Be(130m);
        dashboard.MaintenanceTurnover.Status.Should().Be("InProgress");
        var aggregateCommand = _commands.Should().ContainSingle(command =>
            command.Sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase)).Subject;
        aggregateCommand.Sql.Should().Contain("FROM \"WorkOrders\"");
        aggregateCommand.Sql.Should().Contain("FROM \"Expenses\"");
        aggregateCommand.Sql.ToLowerInvariant().Should().Contain("sum(");
        Capture("TURNOVER_AGGREGATES", [aggregateCommand]);
    }

    [Fact]
    public async Task UnitDashboard_RecordsSqlAndTotalDatabaseQueryCount()
    {
        var property = await SeedPropertyAsync("Dashboard Evidence");
        var unit = await SeedUnitAsync(property, "3A");

        _commands.Clear();
        var dashboard = await DashboardService().GetDashboardAsync(
            PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        _commands.Should().NotBeEmpty();
        _commands.Should().OnlyContain(command =>
            command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"UNIT_DASHBOARD_TOTAL_QUERY_COUNT={_commands.Count}");
        Capture("UNIT_DASHBOARD_SQL", _commands);
        // L11 intentionally records this total without imposing a dashboard query cap.
    }

    [Fact]
    public async Task UnitDashboard_UsesAtMostThreeSqlStatements()
    {
        var property = await SeedPropertyAsync("Unit dashboard budget");
        var unit = await SeedUnitAsync(property, "6A");

        _commands.Clear();
        var dashboard = await DashboardService().GetDashboardAsync(
            PortfolioId, unit.Id, CancellationToken.None);

        dashboard.Should().NotBeNull();
        _output.WriteLine($"UNIT_DASHBOARD_QUERY_BUDGET_COUNT={_commands.Count}");
        Capture("UNIT_DASHBOARD_QUERY_BUDGET", _commands);
        _commands.Should().HaveCountLessThanOrEqualTo(3,
            "Unit Command Center initial load must use no more than three PostgreSQL statements");
    }

    [Fact]
    public async Task RootDashboard_UsesAtMostThreeSqlStatements()
    {
        var property = await SeedPropertyAsync("Root dashboard budget");
        await SeedUnitAsync(property, "7A");
        var scope = await SeedAdministratorScopeAsync();
        await _context.ActivateApiScopeAsync(scope);
        var service = new DashboardService(_context.Db, new AuditDescriber(), TimeProvider.System);

        _commands.Clear();
        var dashboard = await service.GetDashboardAsync(scope, CancellationToken.None);

        dashboard.Should().NotBeNull();
        _output.WriteLine($"ROOT_DASHBOARD_QUERY_BUDGET_COUNT={_commands.Count}");
        Capture("ROOT_DASHBOARD_QUERY_BUDGET", _commands);
        _commands.Should().HaveCountLessThanOrEqualTo(3,
            "Root Dashboard initial load must use no more than three PostgreSQL statements");
    }

    [Fact]
    public async Task UnitInspections_PageFiltersAuthorizesSortsAndPagesInPostgreSql()
    {
        var property = await SeedPropertyAsync("Inspection Paging");
        var unit = await SeedUnitAsync(property, "4A");
        var decoy = await SeedUnitAsync(property, "4B");
        var scope = await SeedAdministratorScopeAsync();
        _context.Db.Inspections.AddRange(
            Inspection(unit, "Target inspection", Now.AddDays(1)),
            Inspection(unit, "Target inspection second", Now.AddDays(2)),
            Inspection(decoy, "Target inspection decoy", Now.AddDays(3)));
        await _context.Db.SaveChangesAsync();

        var service = new InspectionService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IFileStorage>(),
            Mock.Of<IInspectionReportPdfGenerator>(),
            NullLogger<InspectionService>.Instance,
            TimeProvider.System);
        _commands.Clear();
        var page = await service.ListPageAuthorizedAsync(
            scope,
            propertyId: null,
            new InspectionListQuery
            {
                UnitId = unit.Id,
                Search = "Target inspection",
                Sort = "-scheduledFor",
                Skip = 1,
                Take = 1,
            });

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();
        page.Items[0].UnitId.Should().Be(unit.Id);
        var listCommands = SelectsFrom("Inspections");
        listCommands.Should().HaveCount(2);
        listCommands.Should().OnlyContain(command =>
            command.Sql.Contains("UnitId", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("MembershipRoleAssignments", StringComparison.OrdinalIgnoreCase));
        listCommands.Should().Contain(command =>
            command.Sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"UNIT_INSPECTIONS_TOTAL_QUERY_COUNT={listCommands.Count}");
        Capture("UNIT_INSPECTIONS_COUNT_AND_PAGE", listCommands);
    }

    [Fact]
    public async Task UnitRecurringMaintenance_PageFiltersAuthorizesSortsAndPagesInPostgreSql()
    {
        var property = await SeedPropertyAsync("Recurring Paging");
        var unit = await SeedUnitAsync(property, "5A");
        var decoy = await SeedUnitAsync(property, "5B");
        var scope = await SeedAdministratorScopeAsync();
        _context.Db.RecurringMaintenanceTasks.AddRange(
            Recurring(unit, "Target HVAC", Now.AddDays(1)),
            Recurring(unit, "Target HVAC second", Now.AddDays(2)),
            Recurring(decoy, "Target HVAC decoy", Now.AddDays(3)));
        await _context.Db.SaveChangesAsync();

        var service = new RecurringMaintenanceTaskService(
            _context.Db, TimeProvider.System, Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();
        var page = await service.ListPageAuthorizedAsync(
            scope,
            propertyId: null,
            activeOnly: null,
            new RecurringMaintenanceTaskListQuery
            {
                UnitId = unit.Id,
                Search = "Target HVAC",
                Sort = "-nextDueDate",
                Skip = 1,
                Take = 1,
            });

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();
        page.Items[0].UnitId.Should().Be(unit.Id);
        var listCommands = SelectsFrom("RecurringMaintenanceTasks");
        listCommands.Should().HaveCount(2);
        listCommands.Should().OnlyContain(command =>
            command.Sql.Contains("UnitId", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("MembershipRoleAssignments", StringComparison.OrdinalIgnoreCase));
        listCommands.Should().Contain(command =>
            command.Sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"UNIT_RECURRING_TOTAL_QUERY_COUNT={listCommands.Count}");
        Capture("UNIT_RECURRING_COUNT_AND_PAGE", listCommands);
    }

    private UnitDashboardService DashboardService() =>
        new(
            _context.Db,
            new AuditDescriber(),
            new AuditDiffBuilder(),
            TimeProvider.System);

    private async Task<Property> SeedPropertyAsync(string name)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = "100 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        return property;
    }

    private async Task<Unit> SeedUnitAsync(Property property, string number)
    {
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = number,
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1200m,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Units.Add(unit);
        await _context.Db.SaveChangesAsync();
        return unit;
    }

    private async Task<LeaseManagement> SeedPossessionWithoutAgreementAsync(
        Property property,
        Unit unit)
    {
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{Guid.NewGuid():N}"[..20],
            PossessionGivenAtUtc = Now.AddMonths(-1),
            PossessionAgreementExceptionReason = "Verified imported possession.",
            PossessionAgreementExceptionAuthorizedByUserId = 1,
            CreatedAtUtc = Now.AddMonths(-1),
            CreatedByUserId = 1,
            UpdatedAtUtc = Now,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(relationship);
        await _context.Db.SaveChangesAsync();
        return relationship;
    }

    private async Task<TenantAccount> SeedTenantAccountAsync(LeaseManagement relationship)
    {
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            AccountNumber = $"TA-{Guid.NewGuid():N}"[..20],
            Currency = "USD",
            OpenedAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now.AddMonths(-1),
            CreatedByUserId = 1,
        };
        _context.Db.TenantAccounts.Add(account);
        await _context.Db.SaveChangesAsync();
        return account;
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var authNow = DateTime.UtcNow;
        var email = $"unit-condition-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Unit condition verifier",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = authNow,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = authNow,
            LastSeenAtUtc = authNow,
            ExpiresAtUtc = authNow.AddHours(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, context.Id, context.AccessRevision);
    }

    private static WorkOrder WorkOrder(
        Unit unit,
        string title,
        WorkOrderStatus status,
        decimal estimated,
        decimal? actual) =>
        new()
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Title = title,
            Description = title,
            Category = "Turnover",
            Priority = WorkOrderPriority.Normal,
            Status = status,
            RequestedAt = Now.AddDays(-3),
            CompletedAt = status == WorkOrderStatus.Completed ? Now.AddDays(-1) : null,
            EstimatedCost = estimated,
            ActualCost = actual,
            UpdatedAt = Now,
        };

    private static Inspection Inspection(Unit unit, string notes, DateTime scheduledFor) =>
        new()
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Type = InspectionType.Routine,
            Status = InspectionStatus.Scheduled,
            ScheduledFor = scheduledFor,
            Notes = notes,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private static RecurringMaintenanceTask Recurring(
        Unit unit,
        string title,
        DateTime nextDue) =>
        new()
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Title = title,
            Category = "HVAC",
            RecurrenceInterval = RecurrenceInterval.Monthly,
            NextDueDate = nextDue,
            IsActive = true,
            Priority = WorkOrderPriority.Normal,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

    private List<CapturedCommand> SelectsFrom(string table) =>
        _commands.Where(command =>
            command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && command.Sql.Contains($"\"{table}\"", StringComparison.OrdinalIgnoreCase)).ToList();

    private void Capture(string label, IReadOnlyCollection<CapturedCommand> commands)
    {
        _output.WriteLine($"--- {label} ({commands.Count}) ---");
        foreach (var command in commands)
        {
            _output.WriteLine(command.Sql);
        }
    }

    private sealed record CapturedCommand(string Sql);

    private sealed class QueryRecorder(List<CapturedCommand> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(new CapturedCommand(command.CommandText));
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(new CapturedCommand(command.CommandText));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
