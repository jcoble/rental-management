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
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class TenantServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int ActorUserId = 9001;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly ServiceProvider _services;
    private readonly TenantService _sut;
    private readonly WorkspaceReadScope _scope;

    public TenantServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        SeedCanonicalLeaseReadModel();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(TenantServiceTests));
        _services = AtomicDomainTestKernel.CreateForCoreCrud(_ctx.ConnectionString);
        _sut = new TenantService(
            _ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System,
            _services.GetRequiredService<IAtomicUnitOfWork>());
    }

    private void SeedCanonicalLeaseReadModel()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Users.Add(new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "tenant-tests@rentalcommand.local",
            NormalizedUserName = "TENANT-TESTS@RENTALCOMMAND.LOCAL",
            Email = "tenant-tests@rentalcommand.local",
            NormalizedEmail = "TENANT-TESTS@RENTALCOMMAND.LOCAL",
            DisplayName = "Tenant Test Actor",
            CreatedAt = now,
        });
        _ctx.Db.SaveChanges();

        _ctx.Db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_unit_occupancy" AS
            SELECT
                lm."PortfolioId",
                lm."PropertyId",
                lm."UnitId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                CASE WHEN lm."PossessionGivenAtUtc" IS NOT NULL
                          AND lm."PossessionReturnedAtUtc" IS NULL
                          AND lm."CanceledAtUtc" IS NULL THEN 1 ELSE 0 END AS "IsOccupied",
                CASE WHEN lm."PossessionGivenAtUtc" IS NOT NULL
                          AND lm."PossessionReturnedAtUtc" IS NULL
                          AND lm."CanceledAtUtc" IS NULL THEN lm."Id" END AS "CurrentLeaseManagementId",
                CASE WHEN lm."PlannedPossessionAtUtc" IS NOT NULL
                          AND lm."PossessionGivenAtUtc" IS NULL
                          AND lm."CanceledAtUtc" IS NULL THEN 1 ELSE 0 END AS "HasScheduledMoveIn",
                lm."PlannedPossessionAtUtc" AS "NextPlannedPossessionAtUtc",
                CASE WHEN lm."PlannedPossessionAtUtc" IS NOT NULL
                          AND lm."PossessionGivenAtUtc" IS NULL
                          AND lm."CanceledAtUtc" IS NULL THEN lm."Id" END AS "PlannedLeaseManagementId",
                0 AS "IsInTurnover",
                0 AS "IsOutOfService",
                0 AS "IsOnManagementHold",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                NULL AS "OccupancyExceptionCode"
            FROM "LeaseManagements" lm;

            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT
                lm."PortfolioId",
                lm."PropertyId",
                lm."UnitId",
                lm."Id" AS "LeaseManagementId",
                CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                date('now') AS "BusinessDate",
                CASE WHEN lm."PossessionGivenAtUtc" IS NOT NULL
                          AND lm."PossessionReturnedAtUtc" IS NULL THEN 'PossessionActive'
                     WHEN lm."PossessionReturnedAtUtc" IS NOT NULL THEN 'PossessionReturned'
                     ELSE 'PrePossession' END AS "Lifecycle",
                NULL AS "CurrentAgreementId",
                NULL AS "UpcomingAgreementId",
                0 AS "CurrentPartyCount",
                0 AS "CurrentResidentCount",
                0 AS "CurrentFinanciallyResponsiblePartyCount",
                NULL AS "CurrentPrimaryPartyId",
                NULL AS "CurrentPrimaryTenantId",
                NULL AS "CurrentPrimaryTenantName",
                NULL AS "TenantAccountId",
                0 AS "HasMissingTenantAccount",
                0 AS "HasMultipleGoverningAgreements",
                0 AS "HasMultipleCurrentPrimaryTenants",
                0 AS "HasAccountCloseMismatch",
                0 AS "HasGoverningAgreementWithoutPossession",
                0 AS "HasPossessionWithoutGoverningAgreement",
                0 AS "HasReconciliationException"
            FROM "LeaseManagements" lm;
            """);
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedTenant("Avery", "Ellis", activeRelationshipCount: 1);
        SeedTenant("Blair", "Kline", activeRelationshipCount: 3);
        SeedTenant("Casey", "Moss", activeRelationshipCount: 0);
        SeedTenant("Devon", "Nash", activeRelationshipCount: 2);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            Sort = "-activeLeaseCount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(t => t.FirstName).Should().Equal("Devon", "Avery");
        result.Items.Select(t => t.ActiveLeaseCount).Should().Equal(2, 1);

        _commands.Should().HaveCount(2, "the count and requested page each execute as one DB-side query");
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_TokenizesHyphenatedSearchTermsInSql()
    {
        SeedTenant("Avery", "Ellis", activeRelationshipCount: 0);
        SeedTenant("Avery", "Stone", activeRelationshipCount: 0);
        SeedTenant("Blair", "Ellis", activeRelationshipCount: 0);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            Search = "Avery-Ellis",
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(1);
        var tenant = result.Items.Should().ContainSingle().Subject;
        tenant.FirstName.Should().Be("Avery");
        tenant.LastName.Should().Be("Ellis");

        _commands.Should().HaveCount(2);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AvailableForRentalExcludesCurrentResidentsInSql()
    {
        SeedTenant("Avery", "Available", activeRelationshipCount: 0);
        SeedTenant("Blair", "Primary", activeRelationshipCount: 1);
        var noticeTenant = SeedTenant("Casey", "Notice", activeRelationshipCount: 0);
        var expiredTenant = SeedTenant("Devon", "Expired", activeRelationshipCount: 0);
        var pendingTenant = SeedTenant("Emery", "Pending", activeRelationshipCount: 0);
        SeedRelationshipMembership(noticeTenant, occupying: true, withSeparatePrimaryTenant: true, noticeGiven: true);
        SeedRelationshipMembership(expiredTenant, occupying: false);
        SeedRelationshipMembership(pendingTenant, occupying: false);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            AvailableForLease = true,
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(3);
        result.Items.Select(t => $"{t.FirstName} {t.LastName}")
            .Should().Equal("Avery Available", "Devon Expired", "Emery Pending");

        _commands.Should().HaveCount(2);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LeaseManagementParties", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AvailableForRentalWithIncludedRelationshipKeepsCurrentParties()
    {
        SeedTenant("Avery", "Available", activeRelationshipCount: 0);
        var currentPrimary = SeedTenant("Blair", "Current", activeRelationshipCount: 0);
        var currentMember = SeedTenant("Casey", "Current", activeRelationshipCount: 0);
        var conflictTenant = SeedTenant("Devon", "Conflict", activeRelationshipCount: 0);
        var currentRelationship = SeedRelationshipMembership(currentMember, occupying: true, withSeparatePrimaryTenant: true);
        var generatedPrimaryParty = _ctx.Db.LeaseManagementParties.Single(party =>
            party.LeaseManagementId == currentRelationship.Id && party.Role == LeaseManagementPartyRole.PrimaryTenant);
        var generatedPrimaryTenantId = generatedPrimaryParty.TenantId;
        generatedPrimaryParty.TenantId = currentPrimary.Id;
        _ctx.Db.Tenants.Single(t => t.Id == generatedPrimaryTenantId).DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();
        SeedRelationshipMembership(conflictTenant, occupying: true);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            AvailableForLease = true,
            IncludeLeaseManagementId = currentRelationship.Id,
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.Items.Select(t => $"{t.FirstName} {t.LastName}")
            .Should().Equal("Avery Available", "Blair Current", "Casey Current");
        result.Items.Should().NotContain(t => t.FirstName == "Devon");
        _commands.Should().HaveCount(2);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("LeaseManagementParties", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_UnitFilterReturnsCurrentOccupantsInSql()
    {
        var (property, targetUnit, otherUnit) = SeedPropertyWithUnits();
        var activeTenant = SeedTenant("Avery", "Active", activeRelationshipCount: 0);
        var memberTenant = SeedTenant("Blair", "Member", activeRelationshipCount: 0);
        var otherUnitTenant = SeedTenant("Casey", "Other", activeRelationshipCount: 0);
        var expiredTenant = SeedTenant("Devon", "Expired", activeRelationshipCount: 0);
        var currentRelationship = SeedRelationshipOnUnit(
            memberTenant,
            property,
            targetUnit,
            occupying: true,
            withSeparatePrimaryTenant: true,
            noticeGiven: true);
        _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = currentRelationship,
            TenantId = activeTenant.Id,
            Role = LeaseManagementPartyRole.Occupant,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-1)),
            ChangeReason = "Unit-filter occupant fixture",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = ActorUserId,
        });
        _ctx.Db.SaveChanges();
        SeedRelationshipOnUnit(otherUnitTenant, property, otherUnit, occupying: true);
        SeedRelationshipOnUnit(expiredTenant, property, targetUnit, occupying: false);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            UnitId = targetUnit.Id,
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.TotalCount.Should().Be(3);
        result.Items.Select(t => $"{t.FirstName} {t.LastName}")
            .Should().Equal("Avery Active", "Blair Primary Holder", "Blair Member");

        _commands.Should().HaveCount(2);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("UnitId", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LeaseManagementParties", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenTenantStillOccupiesAUnit()
    {
        var tenant = SeedTenantWithRelationship(occupying: true);

        var act = async () => await _sut.DeleteAuthorizedAsync(
            _scope, tenant.Id, Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("current resident");
        (await _sut.GetAsync(PortfolioId, tenant.Id))
            .Should().NotBeNull("a tenant who still occupies a unit must not be deleted");
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenTenantHasRentalRelationshipHistory()
    {
        var tenant = SeedTenantWithRelationship(occupying: false);

        var act = async () => await _sut.DeleteAuthorizedAsync(
            _scope, tenant.Id, Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("rental relationship history");
        (await _sut.GetAsync(PortfolioId, tenant.Id))
            .Should().NotBeNull("a tenant with relationship history is preserved for agreements and account history");
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesWhenTenantHasNoRentalRelationshipHistory()
    {
        var tenant = SeedTenant("Unlinked", "Tenant", activeRelationshipCount: 0);

        var deleted = await _sut.DeleteAuthorizedAsync(
            _scope, tenant.Id, Guid.NewGuid().ToString("N"));

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, tenant.Id)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReportsDeleteStateForRentalRelationshipHistory()
    {
        var tenant = SeedTenantWithRelationship(occupying: false);

        var response = await _sut.GetAsync(PortfolioId, tenant.Id);

        response!.ActiveLeaseCount.Should().Be(0);
        response.LeaseHistoryCount.Should().Be(1);
        response.CanDelete.Should().BeFalse();
        response.DeleteBlockedReason.Should().Contain("rental relationship history");
    }

    [Fact]
    public async Task GetAsync_CountsNoticeGivenRelationshipAsOccupying()
    {
        var tenant = SeedTenantWithRelationship(occupying: true, noticeGiven: true);

        var response = await _sut.GetAsync(PortfolioId, tenant.Id);

        // ActiveLeaseCount is the existing DTO name for occupied rental relationships. The web delete-state helper
        // disables delete while it is > 0, so a notice-given-only tenant is also blocked in the UI.
        response!.ActiveLeaseCount.Should().Be(1);
    }

    private Tenant SeedTenantWithRelationship(bool occupying, bool noticeGiven = false)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Occupying",
            LastName = occupying ? "Current" : "History",
            Email = "occupying@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Lease Property",
            AddressLine1 = "1 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(property, unit);
        _ctx.Db.SaveChanges();
        var relationship = NewRelationship(property, unit, now, occupying, noticeGiven);
        _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            EffectiveThrough = occupying ? null : DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Tenant service test fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        });
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Tenant SeedTenant(string firstName, string lastName, int activeRelationshipCount)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName.ToLowerInvariant()}@example.local",
            Phone = "555-0100",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();

        for (var i = 0; i < activeRelationshipCount; i++)
        {
            var property = new Property
            {
                PortfolioId = PortfolioId,
                Name = $"{firstName} Property {i}",
                AddressLine1 = $"{i} Main Street",
                City = "Columbus",
                State = "OH",
                PostalCode = "43215",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var unit = new Unit
            {
                Property = property,
                UnitNumber = $"{i + 1}A",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ctx.Db.AddRange(property, unit);
            _ctx.Db.SaveChanges();
            var relationship = NewRelationship(property, unit, now, occupying: true);
            _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
            {
                PortfolioId = PortfolioId,
                LeaseManagement = relationship,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
                ChangeReason = "Tenant count fixture",
                CreatedAtUtc = now,
                CreatedByUserId = ActorUserId,
            });
        }

        _ctx.Db.SaveChanges();
        return tenant;
    }

    private LeaseManagement SeedRelationshipMembership(
        Tenant tenant,
        bool occupying,
        bool withSeparatePrimaryTenant = false,
        bool noticeGiven = false)
    {
        var now = DateTime.UtcNow;
        var primaryTenant = tenant;
        if (withSeparatePrimaryTenant)
        {
            primaryTenant = new Tenant
            {
                PortfolioId = PortfolioId,
                FirstName = $"{tenant.FirstName} Primary",
                LastName = "Holder",
                Email = $"{tenant.FirstName.ToLowerInvariant()}-primary@example.local",
                Phone = "555-0101",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ctx.Db.Tenants.Add(primaryTenant);
            _ctx.Db.SaveChanges();
        }

        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"{tenant.FirstName} Membership Property",
            AddressLine1 = "100 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = $"{tenant.FirstName[0]}1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(property, unit);
        _ctx.Db.SaveChanges();
        var relationship = NewRelationship(property, unit, now, occupying, noticeGiven);
        _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = primaryTenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            EffectiveThrough = occupying ? null : DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Primary tenant fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        });
        if (withSeparatePrimaryTenant)
        {
            _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
            {
                PortfolioId = PortfolioId,
                LeaseManagement = relationship,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.CoTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
                EffectiveThrough = occupying ? null : DateOnly.FromDateTime(now.AddDays(-1)),
                ChangeReason = "Co-tenant fixture",
                CreatedAtUtc = now,
                CreatedByUserId = ActorUserId,
            });
        }
        _ctx.Db.SaveChanges();
        return relationship;
    }

    private (Property property, Unit targetUnit, Unit otherUnit) SeedPropertyWithUnits()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Scoped Property",
            AddressLine1 = "100 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var targetUnit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherUnit = new Unit
        {
            Property = property,
            UnitNumber = "2B",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.AddRange(targetUnit, otherUnit);
        _ctx.Db.SaveChanges();
        return (property, targetUnit, otherUnit);
    }

    private LeaseManagement SeedRelationshipOnUnit(
        Tenant tenant,
        Property property,
        Unit unit,
        bool occupying,
        bool withSeparatePrimaryTenant = false,
        bool noticeGiven = false)
    {
        var now = DateTime.UtcNow;
        var primaryTenant = tenant;
        if (withSeparatePrimaryTenant)
        {
            primaryTenant = new Tenant
            {
                PortfolioId = PortfolioId,
                FirstName = $"{tenant.FirstName} Primary",
                LastName = "Holder",
                Email = $"{tenant.FirstName.ToLowerInvariant()}-primary@example.local",
                Phone = "555-0102",
                CreatedAt = now,
                UpdatedAt = now,
            };
            _ctx.Db.Tenants.Add(primaryTenant);
            _ctx.Db.SaveChanges();
        }

        var relationship = NewRelationship(property, unit, now, occupying, noticeGiven);
        _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagement = relationship,
            TenantId = primaryTenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            EffectiveThrough = occupying ? null : DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Unit-filter primary fixture",
            CreatedAtUtc = now,
            CreatedByUserId = ActorUserId,
        });

        if (withSeparatePrimaryTenant)
        {
            _ctx.Db.LeaseManagementParties.Add(new LeaseManagementParty
            {
                PortfolioId = PortfolioId,
                LeaseManagement = relationship,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.CoTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
                EffectiveThrough = occupying ? null : DateOnly.FromDateTime(now.AddDays(-1)),
                ChangeReason = "Unit-filter co-tenant fixture",
                CreatedAtUtc = now,
                CreatedByUserId = ActorUserId,
            });
        }

        _ctx.Db.SaveChanges();
        return relationship;
    }

    private static LeaseManagement NewRelationship(
        Property property,
        Unit unit,
        DateTime now,
        bool occupying,
        bool noticeGiven = false) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = PortfolioId,
        PropertyId = property.Id,
        UnitId = unit.Id,
        RelationshipNumber = $"LM-{unit.Id}-{Guid.NewGuid():N}",
        PlannedPossessionAtUtc = occupying ? now.AddMonths(-1) : now.AddMonths(-2),
        PossessionGivenAtUtc = now.AddMonths(-1),
        NoticeGivenAtUtc = noticeGiven ? now.AddDays(-7) : null,
        PlannedMoveOutAtUtc = noticeGiven ? now.AddMonths(1) : null,
        PossessionReturnedAtUtc = occupying ? null : now.AddDays(-1),
        CreatedAtUtc = now,
        CreatedByUserId = ActorUserId,
        UpdatedAtUtc = now,
        RowVersion = Guid.NewGuid(),
    };

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
