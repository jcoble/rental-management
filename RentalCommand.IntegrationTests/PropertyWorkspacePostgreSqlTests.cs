using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class PropertyWorkspacePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public PropertyWorkspacePostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact(Skip = "RS-B10 harness bug: seeded access context is inactive under the current security clock or scope; receipt #rs-b10-access-seed")]
    public async Task MultiRentalAreas_PageInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var property = NewProperty($"L09 MultiRental {suffix}", RentalStructure.MultiRental, now);
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = $"L09 Owner {suffix}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var units = new List<Unit>();
        for (var index = 0; index < 25; index++)
        {
            units.Add(NewUnit(property, $"{index:D2}", now));
        }

        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = units[12],
            Title = $"L09 workspace repair {suffix}",
            Description = "Representative Property Work record",
            RequestedAt = now,
            UpdatedAt = now,
        };
        _context.Db.AddRange(
            property,
            owner,
            new PropertyOwnership
            {
                PortfolioId = PortfolioId,
                Property = property,
                OwnerEntity = owner,
                OwnershipSharePercent = 100m,
                EffectiveFromUtc = now.AddDays(-1),
                StatementRecipientName = owner.Name,
                PayeeName = owner.Name,
            },
            workOrder,
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.WorkOrder,
                Property = property,
                Unit = units[12],
                WorkOrder = workOrder,
                Category = ScheduleECategory.Repairs,
                Description = "Representative Property Finances record",
                Status = ExpenseStatus.Paid,
                Amount = 125m,
                IncurredAt = now,
                PaidAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        _context.Db.Units.AddRange(units);
        await _context.Db.SaveChangesAsync();

        var propertyService = NewService();
        _commands.Clear();
        var detail = await propertyService.GetAsync(scope, property.Id);
        var detailSql = _commands.Single();
        Capture("MULTIRENTAL_WORKSPACE_DETAIL", detailSql);

        detail.Should().NotBeNull();
        detail!.RentalStructure.Should().Be(RentalStructure.MultiRental);
        detail.UnitCount.Should().Be(25);
        detail.Ownerships.Should().ContainSingle(ownership => ownership.OwnerEntityId == owner.Id);
        detail.WorkspaceEntry.Destination.Should().Be(PropertyWorkspaceDestination.Property);
        detail.WorkspaceEntry.Areas.Should().Equal(
            PropertyWorkspaceArea.Summary,
            PropertyWorkspaceArea.Rentals,
            PropertyWorkspaceArea.OwnershipManagement,
            PropertyWorkspaceArea.PropertyWork,
            PropertyWorkspaceArea.PropertyFinances,
            PropertyWorkspaceArea.DocumentsHistory);
        detailSql.Should().Contain("AuthSessions");
        detailSql.Should().Contain("RentalStructure");
        detailSql.Should().Contain("PropertyOwnerships");
        detailSql.Should().Contain("OwnerEntities");
        detailSql.Should().Contain("Units");
        detailSql.Should().ContainEquivalentOf("join");

        var unitService = new UnitService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();
        var unitPage = await unitService.ListWithHealthPageAsync(scope, new UnitHealthListQuery
        {
            PropertyId = property.Id,
            Sort = "unitNumber",
            Skip = 10,
            Take = 10,
        });

        unitPage.TotalCount.Should().Be(25);
        unitPage.Items.Should().HaveCount(10);
        unitPage.Items.Should().OnlyContain(item => item.PropertyId == property.Id);
        unitPage.Items.Should().ContainSingle(item =>
            item.Id == units[12].Id && item.OpenWorkOrderCount == 1);
        CaptureAndAssertAreaPageSql(
            "MULTIRENTAL_UNITS",
            true,
            "vw_unit_occupancy",
            "WorkOrders");

        var workOrderService = new WorkOrderService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance,
            TimeProvider.System);
        _commands.Clear();
        var workOrderPage = await workOrderService.ListPageAuthorizedAsync(
            scope,
            new WorkOrderListQuery
            {
                PropertyId = property.Id,
                Sort = "-updatedAt",
                Skip = 0,
                Take = 10,
            });

        workOrderPage.TotalCount.Should().Be(1);
        workOrderPage.Items.Should().ContainSingle(item => item.Id == workOrder.Id);
        CaptureAndAssertAreaPageSql(
            "MULTIRENTAL_WORK",
            false,
            "WorkOrders",
            "Properties");

        var expenseService = new ExpenseService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();
        var expensePage = await expenseService.ListPageAsync(
            scope,
            property.Id,
            null,
            null,
            workOrderLinkedOnly: false,
            new ExpenseListQuery
            {
                Sort = "-incurredAt",
                Skip = 0,
                Take = 10,
            });

        expensePage.TotalCount.Should().Be(1);
        expensePage.Items.Should().ContainSingle(item =>
            item.PropertyId == property.Id && item.WorkOrderId == workOrder.Id);
        CaptureAndAssertAreaPageSql(
            "MULTIRENTAL_FINANCES",
            false,
            "Expenses",
            "WorkOrders");
    }

    [Fact(Skip = "RS-B10 harness bug: seeded access context is inactive under the current security clock or scope; receipt #rs-b10-access-seed")]
    public async Task PersistedRentalStructure_DrivesWorkspaceWithoutInference()
    {
        var scope = await SeedAdministratorScopeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var single = NewProperty($"L09 SingleRental {suffix}", RentalStructure.SingleRental, now);
        var multi = NewProperty($"L09 Multi with one Unit {suffix}", RentalStructure.MultiRental, now);
        var singleUnit = NewUnit(single, "Rental", now);
        _context.Db.AddRange(single, multi, singleUnit, NewUnit(multi, "A", now));
        await _context.Db.SaveChangesAsync();

        var service = NewService();
        _commands.Clear();
        var singleResult = await service.GetAsync(scope, single.Id);
        var singleSql = _commands.Single();
        Capture("SINGLERENTAL_DETAIL", singleSql);

        singleResult.Should().NotBeNull();
        singleResult!.UnitCount.Should().Be(1);
        singleResult.WorkspaceEntry.Destination.Should().Be(PropertyWorkspaceDestination.Unit);
        singleResult.WorkspaceEntry.UnitId.Should().Be(singleUnit.Id);
        singleSql.Should().Contain("RentalStructure");
        singleSql.Should().Contain("Units");
        singleSql.Should().Contain("AuthSessions");

        _commands.Clear();
        var multiResult = await service.GetAsync(scope, multi.Id);
        Capture("MULTIRENTAL_ONE_UNIT_DETAIL", _commands.Single());
        multiResult.Should().NotBeNull();
        multiResult!.UnitCount.Should().Be(1);
        multiResult.WorkspaceEntry.Destination.Should().Be(PropertyWorkspaceDestination.Property);
        multiResult.WorkspaceEntry.UnitId.Should().BeNull();
    }

    private PropertyService NewService() =>
        new(_context.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System);

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var now = DateTime.UtcNow;
        var email = $"property-workspace-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Property workspace verifier",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        return new WorkspaceReadScope(
            PortfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static Property NewProperty(string name, RentalStructure structure, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Name = name,
        RentalStructure = structure,
        AddressLine1 = "1 Property Workspace Way",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Unit NewUnit(Property property, string unitNumber, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        Property = property,
        UnitNumber = unitNumber,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private void Capture(string label, string sql)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(sql);
    }

    private void CaptureAndAssertAreaPageSql(
        string label,
        bool expectUnitDocumentAggregate,
        params string[] joinedRelations)
    {
        var countSql = _commands.Single(IsTopLevelCountCommand);
        var pageSql = _commands.Single(IsBoundedPageCommand);
        var unitDocumentAggregateSql = _commands.SingleOrDefault(
            IsUnitDocumentAggregateCommand);

        Capture($"{label}_COUNT", countSql);
        Capture($"{label}_PAGE", pageSql);
        countSql.Should().Contain("AuthSessions");
        countSql.Should().Contain("WHERE");
        countSql.Should().ContainEquivalentOf("join");
        pageSql.Should().Contain("AuthSessions");
        pageSql.Should().Contain("WHERE");
        foreach (var relation in joinedRelations)
        {
            pageSql.Should().Contain(relation);
        }
        pageSql.Should().ContainEquivalentOf("join");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("OFFSET");

        if (expectUnitDocumentAggregate)
        {
            unitDocumentAggregateSql.Should().NotBeNull(
                "the returned Unit page must use one page-scoped aggregate for document badges");
            Capture($"{label}_DOCUMENT_AGGREGATE", unitDocumentAggregateSql!);
            unitDocumentAggregateSql.Should().Contain("StoredFiles");
            unitDocumentAggregateSql.Should().Contain("UNION");
            unitDocumentAggregateSql.Should().Contain("GROUP BY");
            unitDocumentAggregateSql.Should().Contain("ANY");
            unitDocumentAggregateSql.Should().ContainEquivalentOf("join");
            unitDocumentAggregateSql.Should().Contain("WHERE");
        }
        else
        {
            unitDocumentAggregateSql.Should().BeNull(
                "only the Unit area has the separate document badge aggregate");
        }

        _commands.Should().HaveCount(unitDocumentAggregateSql is null ? 2 : 3);
    }

    private static bool IsTopLevelCountCommand(string sql) =>
        sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedPageCommand(string sql) =>
        !IsTopLevelCountCommand(sql)
        && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnitDocumentAggregateCommand(string sql) =>
        !IsTopLevelCountCommand(sql)
        && !IsBoundedPageCommand(sql)
        && sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("UNION", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase);

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
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
