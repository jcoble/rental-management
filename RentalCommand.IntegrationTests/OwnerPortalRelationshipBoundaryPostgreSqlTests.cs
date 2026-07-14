using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class OwnerPortalRelationshipBoundaryPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public OwnerPortalRelationshipBoundaryPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task RelationshipOwner_SeesOnlyTheirOwnerEntityProperties_InSelectedPortfolio()
    {
        var scenario = await SeedScenarioAsync();
        var sut = Portal();
        _commands.Clear();

        var overview = await sut.GetOverviewAsync(scenario.OwnerScope);
        var properties = await sut.ListPropertiesPageAsync(
            scenario.OwnerScope,
            new ListQuery { Take = 20, Sort = "name" });

        overview.Should().NotBeNull();
        overview!.PropertyCount.Should().Be(1);
        overview.UnitCount.Should().Be(1);
        properties.TotalCount.Should().Be(1);
        properties.Items.Should().ContainSingle(item => item.Id == scenario.AuthorizedPropertyId);
        properties.Items.Should().NotContain(item => item.Id == scenario.OtherOwnerPropertyId);
        properties.Items.Should().NotContain(item => item.Id == scenario.OtherPortfolioPropertyId);

        _commands.Should().HaveCount(3, "overview is one SQL query and the page is one count plus one bounded item query");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_owner_access", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StaleOrRevokedOwnerAccess_ReturnsNoOverviewOrPortalData()
    {
        var scenario = await SeedScenarioAsync();
        var staleScope = scenario.OwnerScope with
        {
            AccessRevision = scenario.OwnerScope.AccessRevision + 1,
        };
        var portal = Portal();
        var statements = Statements();

        (await portal.GetOverviewAsync(staleScope)).Should().BeNull();
        (await portal.ListPropertiesPageAsync(staleScope, new ListQuery())).Items.Should().BeEmpty();
        (await portal.ListDistributionsPageAsync(staleScope, new ListQuery())).Items.Should().BeEmpty();
        (await statements.ListForOwnerPortalAsync(staleScope, scenario.Year)).Should().BeEmpty();

        var now = DateTime.UtcNow;
        var access = await _context.Db.OwnerUserAccesses.SingleAsync(item =>
            item.AccessContextId == scenario.OwnerScope.AccessContextId);
        var context = await _context.Db.WorkspaceAccessContexts.SingleAsync(item =>
            item.Id == scenario.OwnerScope.AccessContextId);
        access.RevokedAtUtc = now;
        access.RevokedByUserId = 1;
        context.AdvanceRevision(scenario.OwnerScope.AccessRevision);
        context.UpdatedAtUtc = now;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var revokedScope = scenario.OwnerScope with { AccessRevision = context.AccessRevision };
        (await portal.GetOverviewAsync(revokedScope)).Should().BeNull();
        (await portal.ListPropertiesPageAsync(revokedScope, new ListQuery())).Items.Should().BeEmpty();
        (await portal.ListDistributionsPageAsync(revokedScope, new ListQuery())).Items.Should().BeEmpty();
        (await statements.GetForOwnerPortalAsync(
            revokedScope,
            scenario.AuthorizedOwnerId,
            scenario.Year)).Should().BeNull();
    }

    [Fact]
    public async Task ManagementCapabilities_DoNotSubstituteForOwnerRelationshipAccess()
    {
        var scenario = await SeedScenarioAsync();
        var portal = Portal();
        var statements = Statements();

        (await portal.GetOverviewAsync(scenario.ManagerScope)).Should().BeNull();
        (await portal.ListPropertiesPageAsync(scenario.ManagerScope, new ListQuery())).Items.Should().BeEmpty();
        (await portal.ListDistributionsPageAsync(scenario.ManagerScope, new ListQuery())).Items.Should().BeEmpty();
        (await statements.ListForOwnerPortalAsync(scenario.ManagerScope, scenario.Year)).Should().BeEmpty();
        (await statements.GetForOwnerPortalAsync(
            scenario.ManagerScope,
            scenario.AuthorizedOwnerId,
            scenario.Year)).Should().BeNull();

        var managerWorkspaceScope = new WorkspaceReadScope(
            scenario.ManagerScope.PortfolioId,
            scenario.ManagerScope.UserId,
            scenario.ManagerSessionId,
            scenario.ManagerScope.AccessContextId,
            scenario.ManagerScope.AccessRevision);
        (await _context.Db.Properties.AsNoTracking()
            .WhereAuthorized(
                _context.Db,
                managerWorkspaceScope,
                CapabilityKeys.MoneyOwnerReportsRead,
                DateTime.UtcNow)
            .AnyAsync(property => property.Id == scenario.AuthorizedPropertyId))
            .Should().BeTrue("the denial must hold even when the management context can read owner reports");
        (await _context.Db.EffectiveOwnerAccess.AnyAsync(access =>
            access.AccessContextId == scenario.ManagerScope.AccessContextId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task StatementsAndDistributions_ExcludeUnauthorizedPropertyFacts_AndStayDbSide()
    {
        var scenario = await SeedScenarioAsync();
        var portal = Portal();
        var statements = Statements();

        _commands.Clear();
        var distributions = await portal.ListDistributionsPageAsync(
            scenario.OwnerScope,
            new ListQuery { Sort = "-date", Take = 10 });

        distributions.TotalCount.Should().Be(1);
        distributions.Items.Should().ContainSingle(item =>
            item.PropertyId == scenario.AuthorizedPropertyId && item.Amount == 100m);
        distributions.Items.Should().NotContain(item => item.PropertyId == scenario.OtherOwnerPropertyId);
        _commands.Should().HaveCount(2);
        _commands.Should().ContainSingle(sql =>
            IsTopLevelCountCommand(sql) &&
            sql.Contains("vw_effective_owner_access", StringComparison.OrdinalIgnoreCase));
        _commands.Should().ContainSingle(sql =>
            !IsTopLevelCountCommand(sql) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));

        _commands.Clear();
        var statement = await statements.GetForOwnerPortalAsync(
            scenario.OwnerScope,
            scenario.AuthorizedOwnerId,
            scenario.Year);
        var unauthorizedStatement = await statements.GetForOwnerPortalAsync(
            scenario.OwnerScope,
            scenario.OtherOwnerId,
            scenario.Year);

        statement.Should().NotBeNull();
        statement!.Properties.Should().ContainSingle(line =>
            line.PropertyId == scenario.AuthorizedPropertyId && line.Expenses == 125m);
        statement.TotalExpenses.Should().Be(125m);
        statement.TotalDistributed.Should().Be(100m);
        statement.Properties.Should().NotContain(line => line.PropertyId == scenario.OtherOwnerPropertyId);
        unauthorizedStatement.Should().BeNull();
        _commands.Should().HaveCount(2, "each statement request is a single PostgreSQL query");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("vw_effective_owner_access", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PropertySearchSortPagingAndCount_AreTranslatedAndBoundedInPostgreSql()
    {
        var scenario = await SeedScenarioAsync();
        await AddPropertyAsync(scenario.AuthorizedOwnerId, 1, "Alpha House", "Akron");
        await AddPropertyAsync(scenario.AuthorizedOwnerId, 1, "Gamma House", "Akron");
        _commands.Clear();

        var page = await Portal().ListPropertiesPageAsync(
            scenario.OwnerScope,
            new ListQuery
            {
                Search = "house",
                Sort = "-name",
                Skip = 1,
                Take = 1,
            });

        page.TotalCount.Should().Be(3);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.Name == "Bravo House");
        _commands.Should().HaveCount(2, "count and bounded page data are the only database round trips");
        _commands.Should().ContainSingle(sql =>
            IsTopLevelCountCommand(sql) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
        _commands.Should().ContainSingle(sql =>
            !IsTopLevelCountCommand(sql) &&
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private OwnerPortalService Portal() => new(_context.Db, TimeProvider.System);

    private OwnerStatementService Statements() => new(_context.Db, TimeProvider.System);

    private async Task<Scenario> SeedScenarioAsync()
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var year = now.Year;
        var ownerUser = User("relationship-owner@example.test", "Relationship Owner");
        var managerUser = User("property-manager@example.test", "Property Manager");
        var otherPortfolio = new Portfolio
        {
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Management",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(ownerUser, managerUser, otherPortfolio);
        await db.SaveChangesAsync();

        var authorizedOwner = Owner(1, "Authorized Owner", now);
        var otherOwner = Owner(1, "Other Owner", now);
        var otherPortfolioOwner = Owner(otherPortfolio.Id, "Cross Portfolio Owner", now);
        db.AddRange(authorizedOwner, otherOwner, otherPortfolioOwner);
        await db.SaveChangesAsync();

        var authorizedProperty = Property(1, authorizedOwner.Id, "Bravo House", "Columbus", now);
        var otherOwnerProperty = Property(1, otherOwner.Id, "Other Owner House", "Cleveland", now);
        var otherPortfolioProperty = Property(
            otherPortfolio.Id,
            otherPortfolioOwner.Id,
            "Cross Portfolio House",
            "Cincinnati",
            now);
        db.AddRange(authorizedProperty, otherOwnerProperty, otherPortfolioProperty);
        await db.SaveChangesAsync();
        db.Units.AddRange(
            Unit(1, authorizedProperty.Id, "A", now),
            Unit(1, otherOwnerProperty.Id, "B", now),
            Unit(otherPortfolio.Id, otherPortfolioProperty.Id, "C", now));

        var ownerContext = new WorkspaceAccessContext
        {
            UserId = ownerUser.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var managerContext = new WorkspaceAccessContext
        {
            UserId = managerUser.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var managerMembership = new WorkspaceMembership
        {
            AccessContext = managerContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var managerAssignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = managerMembership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.PropertyManager).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            EffectiveFromUtc = now.AddMinutes(-5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        managerAssignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = managerAssignment,
            PortfolioId = 1,
            PropertyId = authorizedProperty.Id,
        });
        var managerSession = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = managerUser,
            ActiveAccessContext = managerContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(ownerContext, managerAssignment, managerSession);
        await db.SaveChangesAsync();

        db.OwnerUserAccesses.Add(new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            AccessContextId = ownerContext.Id,
            ApplicationUserId = ownerUser.Id,
            OwnerEntityId = authorizedOwner.Id,
            EffectiveFromUtc = now.AddMinutes(-5),
            GrantedAtUtc = now,
            GrantedByUserId = 1,
            Reason = "Owner portal relationship boundary proof",
        });
        db.Expenses.AddRange(
            Expense(1, authorizedProperty.Id, 125m, year, now),
            Expense(1, otherOwnerProperty.Id, 900m, year, now),
            Expense(otherPortfolio.Id, otherPortfolioProperty.Id, 500m, year, now));
        db.OwnerDistributions.AddRange(
            Distribution(1, authorizedOwner.Id, authorizedProperty.Id, 100m, year, now),
            Distribution(1, otherOwner.Id, otherOwnerProperty.Id, 900m, year, now),
            Distribution(otherPortfolio.Id, otherPortfolioOwner.Id, otherPortfolioProperty.Id, 500m, year, now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Scenario(
            new OwnerPortalReadScope(1, ownerUser.Id, ownerContext.Id, ownerContext.AccessRevision),
            new OwnerPortalReadScope(1, managerUser.Id, managerContext.Id, managerContext.AccessRevision),
            managerSession.Id,
            authorizedOwner.Id,
            otherOwner.Id,
            authorizedProperty.Id,
            otherOwnerProperty.Id,
            otherPortfolioProperty.Id,
            year);
    }

    private async Task AddPropertyAsync(int ownerId, int portfolioId, string name, string city)
    {
        _context.Db.Properties.Add(Property(portfolioId, ownerId, name, city, DateTime.UtcNow));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private static ApplicationUser User(string email, string name) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = name,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTime.UtcNow,
    };

    private static OwnerEntity Owner(int portfolioId, string name, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Property Property(int portfolioId, int ownerId, string name, string city, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        OwnerEntityId = ownerId,
        Name = name,
        AddressLine1 = $"1 {name} Way",
        City = city,
        State = "OH",
        PostalCode = "43215",
        ManagementFeePercent = 10m,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Unit Unit(int portfolioId, int propertyId, string number, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Expense Expense(
        int portfolioId,
        int propertyId,
        decimal amount,
        int year,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Description = "Owner statement boundary expense",
        Category = ScheduleECategory.Repairs,
        Status = ExpenseStatus.Paid,
        Amount = amount,
        IncurredAt = new DateTime(year, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static OwnerDistribution Distribution(
        int portfolioId,
        int ownerId,
        int propertyId,
        decimal amount,
        int year,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        OwnerEntityId = ownerId,
        PropertyId = propertyId,
        Date = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        Amount = amount,
        Method = DistributionMethod.Ach,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private sealed record Scenario(
        OwnerPortalReadScope OwnerScope,
        OwnerPortalReadScope ManagerScope,
        Guid ManagerSessionId,
        int AuthorizedOwnerId,
        int OtherOwnerId,
        int AuthorizedPropertyId,
        int OtherOwnerPropertyId,
        int OtherPortfolioPropertyId,
        int Year);

    private static bool IsTopLevelCountCommand(string sql) =>
        sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase);

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
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
