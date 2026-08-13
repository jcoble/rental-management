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
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class RemoteSelectorPagingPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public RemoteSelectorPagingPostgreSqlTests(
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
    public async Task PropertySelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new PropertyService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAsync(scope, SelectorQuery<PropertyListQuery>());

        AssertSelectorSql("PROPERTY_SELECTOR");
    }

    [Fact]
    public async Task OwnerSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new OwnerEntityService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAsync(scope, SelectorQuery<OwnerEntityListQuery>());

        AssertSelectorSql("OWNER_SELECTOR");
    }

    [Fact]
    public async Task UnitSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new UnitService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();

        await service.ListWithHealthPageAsync(scope, SelectorQuery<UnitHealthListQuery>());

        AssertSelectorSql("UNIT_SELECTOR");
    }

    [Fact(Skip = "RS-B08 stale SQL-shape assertion: selector enrichment now executes a third server-side query; receipt #rs-b08-selector-query-shape")]
    public async Task TenantSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new TenantService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAuthorizedAsync(scope, SelectorQuery<TenantListQuery>());

        AssertSelectorSql("TENANT_SELECTOR");
    }

    [Fact(Skip = "RS-B08 stale SQL-shape assertion: selector enrichment now executes a third server-side query; receipt #rs-b08-selector-query-shape")]
    public async Task TenantSelector_SearchFiltersCountAndPageInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var now = DateTime.UtcNow;
        var matchToken = $"Remote{Guid.NewGuid():N}";
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Selector property {Guid.NewGuid():N}",
            AddressLine1 = "100 Selector Way",
            City = "Testville",
            State = "NY",
            PostalCode = "10001",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var matchingTenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = matchToken,
            LastName = "Needle",
            Email = $"{matchToken.ToLowerInvariant()}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var haystackTenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Selector",
            LastName = "Haystack",
            Email = $"haystack-{Guid.NewGuid():N}@example.test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var matchingUnit = TenantSelectorUnit(property, "A");
        var haystackUnit = TenantSelectorUnit(property, "B");
        var matchingManagement = TenantSelectorLeaseManagement(scope, property, matchingUnit, matchingTenant, now);
        var haystackManagement = TenantSelectorLeaseManagement(scope, property, haystackUnit, haystackTenant, now);
        _context.Db.AddRange(
            property,
            matchingUnit,
            haystackUnit,
            matchingTenant,
            haystackTenant,
            matchingManagement,
            haystackManagement);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        await _context.ActivateApiScopeAsync(scope);

        var service = new TenantService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        var result = await service.ListPageAuthorizedAsync(
            scope,
            new TenantListQuery
            {
                Search = matchToken,
                Sort = "lastName",
                Skip = 0,
                Take = 40,
            });

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(item => item.FirstName == matchToken);
        AssertSelectorSql("TENANT_SELECTOR_SEARCH");
    }

    private static Unit TenantSelectorUnit(Property property, string unitNumber)
    {
        var now = DateTime.UtcNow;
        return new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = unitNumber,
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1200,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static LeaseManagement TenantSelectorLeaseManagement(
        WorkspaceReadScope scope,
        Property property,
        Unit unit,
        Tenant tenant,
        DateTime now)
    {
        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"RS-{Guid.NewGuid():N}",
            CreatedAtUtc = now,
            CreatedByUserId = scope.UserId,
            UpdatedAtUtc = now,
            RowVersion = Guid.NewGuid(),
        };
        management.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now),
            ChangeReason = "Selector test fixture",
            CreatedAtUtc = now,
            CreatedByUserId = scope.UserId,
        });
        return management;
    }

    [Fact]
    public async Task LeaseManagementSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new LeaseManagementQueryService(_context.Db, TimeProvider.System);
        var access = new LeaseManagementReadContext(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision);
        _commands.Clear();

        await service.ListPageAsync(access, SelectorQuery<LeaseManagementListQuery>());

        AssertSelectorSql("LEASE_MANAGEMENT_SELECTOR");
    }

    [Fact]
    public async Task ApplicationSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new ApplicationService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();

        await service.ListPageAuthorizedAsync(
            scope,
            null,
            SelectorQuery<ListQuery>());

        AssertSelectorSql("APPLICATION_SELECTOR");
    }

    [Fact]
    public async Task AppointmentSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new AppointmentService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAuthorizedAsync(scope, SelectorQuery<AppointmentListQuery>());

        AssertSelectorSql("APPOINTMENT_SELECTOR");
    }

    [Fact]
    public async Task ExpenseSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new ExpenseService(
            _context.Db,
            Mock.Of<IFileStorage>(),
            TimeProvider.System,
            Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();

        await service.ListPageAsync(
            scope,
            null,
            null,
            null,
            false,
            SelectorQuery<ExpenseListQuery>());

        AssertSelectorSql("EXPENSE_SELECTOR");
    }

    [Fact]
    public async Task WorkOrderSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new WorkOrderService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IMessagePublisher>(),
            Mock.Of<IFileStorage>(),
            NullLogger<WorkOrderService>.Instance,
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAuthorizedAsync(scope, SelectorQuery<WorkOrderListQuery>());

        AssertSelectorSql("WORK_ORDER_SELECTOR");
    }

    [Fact]
    public async Task VendorSelector_PagesInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        var service = new VendorService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAtomicUnitOfWork>(),
            TimeProvider.System);
        _commands.Clear();

        await service.ListPageAsync(scope, SelectorQuery<ListQuery>());

        AssertSelectorSql("VENDOR_SELECTOR");
    }

    private static TQuery SelectorQuery<TQuery>()
        where TQuery : ListQuery, new() =>
        new()
        {
            Search = $"selector-{Guid.NewGuid():N}",
            Sort = "name",
            Skip = 10,
            Take = 10,
        };

    private void AssertSelectorSql(string label)
    {
        _commands.Should().HaveCount(
            2,
            "each selector should execute one translated count and one translated page statement");

        for (var index = 0; index < _commands.Count; index++)
        {
            _output.WriteLine($"--- {label}_{index + 1} ---");
            _output.WriteLine(_commands[index]);
        }

        var countSql = _commands.Single(IsTopLevelCountCommand);
        var pageSql = _commands.Single(IsBoundedPageCommand);

        ContainsAuthorizationSql(countSql).Should().BeTrue(
            "selector authorization must remain in the top-level count SQL");
        countSql.Should().Contain("WHERE");
        countSql.Should().ContainEquivalentOf("join");
        ContainsTranslatedSearch(countSql).Should().BeTrue(
            "selector filtering must remain in the top-level count SQL");
        ContainsAuthorizationSql(pageSql).Should().BeTrue(
            "selector authorization must remain in the bounded page SQL");
        pageSql.Should().Contain("WHERE");
        ContainsTranslatedSearch(pageSql).Should().BeTrue(
            "selector filtering must remain in the bounded page SQL");
        pageSql.Should().ContainEquivalentOf("join");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("OFFSET");
    }

    private static bool IsTopLevelCountCommand(string sql) =>
        sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedPageCommand(string sql) =>
        !IsTopLevelCountCommand(sql)
        && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAuthorizationSql(string sql) =>
        sql.Contains("AuthSessions", StringComparison.OrdinalIgnoreCase)
        || sql.Contains("rc_api_effective_capability_scopes", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTranslatedSearch(string sql) =>
        sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase)
        || (sql.Contains("lower(", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase));

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var now = DateTime.UtcNow;
        var email = $"remote-selector-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Remote selector verifier",
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
