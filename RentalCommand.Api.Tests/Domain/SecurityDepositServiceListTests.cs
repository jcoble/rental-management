using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public sealed class SecurityDepositServiceListTests
{
    [Fact]
    public async Task ListPageAsync_ReturnsCanonicalProjectionStatusAndRequestedWindow()
    {
        await using var db = NewFixtureContext();
        db.Portfolios.Add(new Portfolio
        {
            Id = 1,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Company",
            TimeZone = "UTC",
        });
        SeedCanonicalAccount(db, id: 101, relationshipNumber: "REL-A", heldBalance: 900m, status: "Held");
        SeedCanonicalAccount(db, id: 102, relationshipNumber: "REL-B", heldBalance: 1_100m, status: "PartiallyReturned");
        SeedCanonicalAccount(db, id: 103, relationshipNumber: "REL-C", heldBalance: 1_300m, status: "Returned");
        await db.SaveChangesAsync();
        var scope = SeedAdministratorScope(db);

        var result = await BuildService(db).ListPageAsync(scope, leaseManagementId: null, new ListQuery
        {
            Sort = "amount",
            Skip = 1,
            Take = 1,
        });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(1);
        result.Items.Should().ContainSingle();
        result.Items[0].Should().Match<SecurityDepositAccountResponse>(row =>
            row.Id == 102 &&
            row.RelationshipNumber == "REL-B" &&
            row.TotalReceived == 1_350m &&
            row.TotalDeductions == 250m &&
            row.HeldBalance == 1_100m &&
            row.Status == "PartiallyReturned");
    }

    [Fact]
    public void CanonicalQuery_SearchFilterSortAndPagingRemainDatabaseSide()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql("Host=localhost;Database=translation_only;Username=translation_only;Password=translation_only")
                .Options);

        var term = "%REL-B%";
        var scope = new WorkspaceReadScope(
            1, 17, Guid.Parse("8b2b8fc4-931e-4896-a8ea-d52b261df51a"), 23, 5);
        var sql = BuildService(db).BuildAccountQuery(scope)
            .Where(row => row.LeaseManagementId == 202)
            .Where(row => EF.Functions.ILike(row.RelationshipNumber, term)
                || (row.TenantName != null && EF.Functions.ILike(row.TenantName, term)))
            .OrderBy(row => row.HeldBalance)
            .Skip(5)
            .Take(10)
            .ToQueryString();

        sql.Should().Contain("vw_security_deposit_balances");
        sql.Should().Contain("vw_lease_management_lifecycle");
        sql.Should().Contain("SecurityDepositAccounts");
        sql.Should().Contain("TenantAccounts");
        sql.Should().Contain("LeaseManagements");
        sql.Should().Contain("ILIKE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("OFFSET");
        sql.Should().Contain("AuthSessions");
        sql.Should().Contain("MembershipRoleAssignmentProperties");
        sql.Should().Contain(CapabilityKeys.MoneyDepositsManage);
        sql.Should().Contain(CapabilityKeys.LeasingDepositsRead);
        sql.Should().Contain("AccessRevision");
        sql.Should().NotContain("SecurityDepositHoldings");
    }

    [Fact]
    public async Task EveryAccountReadSurfaceHidesUnauthorizedPropertyAndHonorsLeaseFilter()
    {
        await using var db = NewFixtureContext();
        db.Portfolios.Add(new Portfolio
        {
            Id = 1,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Company",
            TimeZone = "UTC",
        });
        SeedCanonicalAccount(db, id: 201, relationshipNumber: "ALLOWED", heldBalance: 700m, status: "Held");
        SeedCanonicalAccount(db, id: 202, relationshipNumber: "DECOY", heldBalance: 9_000m, status: "Held");
        await db.SaveChangesAsync();
        var allowedLeaseManagementId = 201 + 100;
        var scope = SeedSelectedPropertyManagerScope(db, propertyId: 201 + 300);
        var service = BuildService(db);

        var list = await service.ListAsync(scope, leaseManagementId: null);
        var page = await service.ListPageAsync(scope, leaseManagementId: null, new ListQuery
        {
            Sort = "amount",
            Skip = 0,
            Take = 20,
        });
        var filtered = await service.ListAsync(scope, allowedLeaseManagementId);
        var allowedDetail = await service.GetAsync(scope, 201);
        var decoyDetail = await service.GetAsync(scope, 202);

        list.Should().ContainSingle().Which.Id.Should().Be(201);
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(201);
        filtered.Should().ContainSingle().Which.Id.Should().Be(201);
        allowedDetail.Should().NotBeNull();
        decoyDetail.Should().BeNull("resource-target reads must not disclose an out-of-scope deposit");
    }

    private static FixtureDbContext NewFixtureContext()
    {
        var db = new FixtureDbContext(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase($"canonical-deposits-{Guid.NewGuid():N}")
            .Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static SecurityDepositService BuildService(RentalCommandDbContext db) =>
        new(
            db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IMoveOutStatementPdfGenerator>(),
            NullLogger<SecurityDepositService>.Instance,
            TimeProvider.System);

    private static WorkspaceReadScope SeedAdministratorScope(RentalCommandDbContext db)
    {
        var access = SeedAccess(db, RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        db.SaveChanges();
        return ScopeFor(access.Context, access.Session);
    }

    private static WorkspaceReadScope SeedSelectedPropertyManagerScope(
        RentalCommandDbContext db,
        int propertyId)
    {
        var access = SeedAccess(db, RoleProfileKeys.PropertyManager,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        access.Assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignment = access.Assignment,
            PortfolioId = 1,
            PropertyId = propertyId,
        });
        db.SaveChanges();
        return ScopeFor(access.Context, access.Session);
    }

    private static TestAccess SeedAccess(
        RentalCommandDbContext db,
        string roleKey,
        MembershipRoleAssignmentScopeKind scopeKind)
    {
        var now = DateTime.UtcNow;
        var email = $"deposit-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Deposit Reader",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var context = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        return new TestAccess(context, assignment, session);
    }

    private static WorkspaceReadScope ScopeFor(WorkspaceAccessContext context, AuthSession session) =>
        new(1, context.UserId, session.Id, context.Id, context.AccessRevision);

    private sealed record TestAccess(
        WorkspaceAccessContext Context,
        MembershipRoleAssignment Assignment,
        AuthSession Session);

    private static void SeedCanonicalAccount(
        FixtureDbContext db,
        int id,
        string relationshipNumber,
        decimal heldBalance,
        string status)
    {
        var managementId = id + 100;
        var tenantAccountId = id + 200;
        var propertyId = id + 300;
        var unitId = id + 400;
        var createdAt = new DateTime(2026, 1, ((id - 1) % 28) + 1, 12, 0, 0, DateTimeKind.Utc);
        var totalDeductions = status == "PartiallyReturned" ? 250m : 0m;
        var totalRefunded = status == "Returned" ? 1_300m : 0m;
        var totalReceived = heldBalance + totalDeductions + totalRefunded;

        db.Properties.Add(new Property
        {
            Id = propertyId,
            PortfolioId = 1,
            Name = $"Property {relationshipNumber}",
        });
        db.Units.Add(new Unit
        {
            Id = unitId,
            PortfolioId = 1,
            PropertyId = propertyId,
            UnitNumber = relationshipNumber[^1..],
        });
        db.LeaseManagements.Add(new LeaseManagement
        {
            Id = managementId,
            PortfolioId = 1,
            PropertyId = propertyId,
            UnitId = unitId,
            RelationshipNumber = relationshipNumber,
        });
        db.TenantAccounts.Add(new TenantAccount
        {
            Id = tenantAccountId,
            PortfolioId = 1,
            LeaseManagementId = managementId,
            AccountNumber = $"TA-{id}",
            Currency = "USD",
        });
        db.SecurityDepositAccounts.Add(new SecurityDepositAccount
        {
            Id = id,
            PortfolioId = 1,
            TenantAccountId = tenantAccountId,
            OriginatingAgreementId = id + 500,
            Currency = "USD",
            CreatedAtUtc = createdAt,
        });
        db.SecurityDepositEntries.Add(new SecurityDepositEntry
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            PortfolioId = 1,
            SecurityDepositAccountId = id,
            EntryType = SecurityDepositEntryType.Receipt,
            Direction = SecurityDepositDirection.Increase,
            Amount = totalReceived,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(createdAt),
            PostedAtUtc = createdAt,
            BusinessKey = $"seed-{id}",
            Description = "Canonical deposit receipt",
        });
        db.SecurityDepositBalanceProjections.Add(new SecurityDepositBalanceProjection
        {
            PortfolioId = 1,
            LeaseManagementId = managementId,
            TenantAccountId = tenantAccountId,
            SecurityDepositAccountId = id,
            Currency = "USD",
            TotalReceived = totalReceived,
            TotalDeductions = totalDeductions,
            TotalRefunded = totalRefunded,
            HeldBalance = heldBalance,
            DepositStatus = status,
        });
        db.LeaseManagementLifecycleProjections.Add(new LeaseManagementLifecycleProjection
        {
            PortfolioId = 1,
            PropertyId = propertyId,
            UnitId = unitId,
            LeaseManagementId = managementId,
            CurrentPrimaryTenantName = $"Tenant {relationshipNumber}",
        });
    }

    private sealed class FixtureDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<SecurityDepositBalanceProjection>()
                .HasKey(row => new { row.PortfolioId, row.SecurityDepositAccountId });
            modelBuilder.Entity<LeaseManagementLifecycleProjection>()
                .HasKey(row => new { row.PortfolioId, row.LeaseManagementId });
        }
    }
}
