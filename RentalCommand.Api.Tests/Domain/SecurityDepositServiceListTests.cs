using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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

        var result = await BuildService(db).ListPageAsync(1, leaseManagementId: null, new ListQuery
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
        var sql = BuildService(db).BuildAccountQuery(1)
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
        sql.Should().NotContain("SecurityDepositHoldings");
    }

    private static FixtureDbContext NewFixtureContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase($"canonical-deposits-{Guid.NewGuid():N}")
            .Options);

    private static SecurityDepositService BuildService(RentalCommandDbContext db) =>
        new(
            db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IMoveOutStatementPdfGenerator>(),
            NullLogger<SecurityDepositService>.Instance,
            TimeProvider.System);

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
        var createdAt = new DateTime(2026, 1, id - 100, 12, 0, 0, DateTimeKind.Utc);
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
        : RentalCommandDbContext(options)
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
