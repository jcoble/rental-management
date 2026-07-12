using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class TenantServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TenantPortalProvisioningService _provisioning;
    private readonly TenantService _sut;

    public TenantServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _userManager = CreateUserManager(_ctx.Db);
        // Real provisioning over the same in-memory DB so CreateAsync's auto-provision actually mints
        // an Identity login we can assert on (Tenant role + TenantId link).
        _provisioning = new TenantPortalProvisioningService(
            _userManager,
            _ctx.Db,
            Options.Create(new SeedSettings()),
            TimeProvider.System,
            NullLogger<TenantPortalProvisioningService>.Instance);
        _sut = new TenantService(
            _ctx.Db, Mock.Of<IDataUpdateService>(), _provisioning, NullLogger<TenantService>.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task CreateAsync_WithEmail_ProvisionsTenantRoleIdentityUserLinkedToTenant()
    {
        SeedTenantRole();

        var response = await _sut.CreateAsync(PortfolioId, new CreateTenantRequest
        {
            FirstName = "Portal",
            LastName = "Tenant",
            Email = "portal.tenant@example.local",
        });

        var identityUser = await _userManager.FindByEmailAsync("portal.tenant@example.local");
        identityUser.Should().NotBeNull("creating a tenant with an email provisions their portal login");
        identityUser!.PortfolioId.Should().Be(PortfolioId);
        identityUser.EmailConfirmed.Should().BeTrue();
        (await _userManager.GetRolesAsync(identityUser)).Should().BeEmpty();
        _ctx.Db.TenantUserAccesses.Should().BeEmpty(
            "a tenant record alone is not an effective lease-party relationship");
    }

    [Fact]
    public async Task CreateAsync_WithoutEmail_DoesNotProvisionAndDoesNotThrow()
    {
        SeedTenantRole();

        var response = await _sut.CreateAsync(PortfolioId, new CreateTenantRequest
        {
            FirstName = "NoEmail",
            LastName = "Tenant",
            Email = null,
        });

        response.Id.Should().BeGreaterThan(0, "tenant creation still succeeds without an email");
        _ctx.Db.Users.Should().BeEmpty("a tenant with no email gets no Identity login");
        _ctx.Db.UserAccounts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_ReportsPortalAccessState()
    {
        SeedTenantRole();

        // A tenant with an email is auto-provisioned on create → active.
        var active = await _sut.CreateAsync(PortfolioId, new CreateTenantRequest
        {
            FirstName = "Active",
            LastName = "Portal",
            Email = "active.portal@example.local",
        });
        (await _sut.GetAsync(PortfolioId, active.Id))!.PortalAccess.Should().Be("active");

        // A tenant with no email has no login → none.
        var none = await _sut.CreateAsync(PortfolioId, new CreateTenantRequest
        {
            FirstName = "NoLogin",
            LastName = "Portal",
            Email = null,
        });
        (await _sut.GetAsync(PortfolioId, none.Id))!.PortalAccess.Should().Be("none");

        // Turning the active tenant's access off → disabled; back on → active.
        await _provisioning.SetPortalAccessAsync(active.Id, PortfolioId, enabled: false);
        (await _sut.GetAsync(PortfolioId, active.Id))!.PortalAccess.Should().Be("disabled");

        await _provisioning.SetPortalAccessAsync(active.Id, PortfolioId, enabled: true);
        (await _sut.GetAsync(PortfolioId, active.Id))!.PortalAccess.Should().Be("active");
    }

    private void SeedTenantRole()
    {
        // The Identity store throws if the Tenant role row is missing when AddToRoleAsync runs.
        _ctx.Db.Roles.Add(new IdentityRole<int>
        {
            Name = nameof(UserRole.Tenant),
            NormalizedName = nameof(UserRole.Tenant).ToUpperInvariant(),
        });
        _ctx.Db.SaveChanges();
    }

    private static UserManager<ApplicationUser> CreateUserManager(RentalCommandDbContext db)
    {
        var store = new UserStore<ApplicationUser, IdentityRole<int>, RentalCommandDbContext, int>(db);
        return new UserManager<ApplicationUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedTenant("Avery", "Ellis", activeLeaseCount: 1);
        SeedTenant("Blair", "Kline", activeLeaseCount: 3);
        SeedTenant("Casey", "Moss", activeLeaseCount: 0);
        SeedTenant("Devon", "Nash", activeLeaseCount: 2);

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
        SeedTenant("Avery", "Ellis", activeLeaseCount: 0);
        SeedTenant("Avery", "Stone", activeLeaseCount: 0);
        SeedTenant("Blair", "Ellis", activeLeaseCount: 0);

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

        _commands.Should().HaveCount(3);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("LIKE", StringComparison.OrdinalIgnoreCase) ||
            sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AvailableForLeaseExcludesTenantsAlreadyOccupyingLeasesInSql()
    {
        SeedTenant("Avery", "Available", activeLeaseCount: 0);
        SeedTenant("Blair", "Primary", activeLeaseCount: 1);
        var noticeTenant = SeedTenant("Casey", "Notice", activeLeaseCount: 0);
        var expiredTenant = SeedTenant("Devon", "Expired", activeLeaseCount: 0);
        var pendingTenant = SeedTenant("Emery", "Pending", activeLeaseCount: 0);
        SeedLeaseTenantMembership(noticeTenant, LeaseStatus.NoticeGiven, withSeparatePrimaryTenant: true);
        SeedLeaseTenantMembership(expiredTenant, LeaseStatus.Expired);
        SeedLeaseTenantMembership(pendingTenant, LeaseStatus.PendingSignature);

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

        _commands.Should().HaveCount(3);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("LeaseTenants", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_AvailableForLeaseWithIncludedRelationshipKeepsCurrentParties()
    {
        SeedTenant("Avery", "Available", activeLeaseCount: 0);
        var currentPrimary = SeedTenant("Blair", "Current", activeLeaseCount: 0);
        var currentMember = SeedTenant("Casey", "Current", activeLeaseCount: 0);
        var conflictTenant = SeedTenant("Devon", "Conflict", activeLeaseCount: 0);
        var currentLease = SeedLeaseTenantMembership(currentMember, LeaseStatus.Active, withSeparatePrimaryTenant: true);
        var generatedPrimaryTenantId = currentLease.TenantId;
        currentLease.TenantId = currentPrimary.Id;
        _ctx.Db.Tenants.Single(t => t.Id == generatedPrimaryTenantId).DeletedAt = DateTime.UtcNow;
        _ctx.Db.SaveChanges();
        SeedLeaseTenantMembership(conflictTenant, LeaseStatus.Active);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, new TenantListQuery
        {
            AvailableForLease = true,
            IncludeLeaseManagementId = currentLease.Id,
            Sort = "name",
            Skip = 0,
            Take = 20,
        });

        result.Items.Select(t => $"{t.FirstName} {t.LastName}")
            .Should().Equal("Avery Available", "Blair Current", "Casey Current");
        result.Items.Should().NotContain(t => t.FirstName == "Devon");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_UnitFilterReturnsCurrentOccupantsInSql()
    {
        var (property, targetUnit, otherUnit) = SeedPropertyWithUnits();
        var activeTenant = SeedTenant("Avery", "Active", activeLeaseCount: 0);
        var memberTenant = SeedTenant("Blair", "Member", activeLeaseCount: 0);
        var otherUnitTenant = SeedTenant("Casey", "Other", activeLeaseCount: 0);
        var expiredTenant = SeedTenant("Devon", "Expired", activeLeaseCount: 0);
        SeedLeaseOnUnit(activeTenant, property, targetUnit, LeaseStatus.Active);
        SeedLeaseOnUnit(memberTenant, property, targetUnit, LeaseStatus.NoticeGiven, withSeparatePrimaryTenant: true);
        SeedLeaseOnUnit(otherUnitTenant, property, otherUnit, LeaseStatus.Active);
        SeedLeaseOnUnit(expiredTenant, property, targetUnit, LeaseStatus.Expired);

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

        _commands.Should().HaveCount(3);
        _commands.Where(sql => sql.Contains("FROM \"Tenants\"", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(sql =>
            sql.Contains("UnitId", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("LeaseTenants", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(LeaseStatus.Active)]
    [InlineData(LeaseStatus.NoticeGiven)]
    public async Task DeleteAsync_ThrowsWhenTenantStillOccupiesAUnit(LeaseStatus status)
    {
        var tenant = SeedTenantWithLease(status);

        var act = async () => await _sut.DeleteAsync(PortfolioId, tenant.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("lease");
        (await _sut.GetAsync(PortfolioId, tenant.Id))
            .Should().NotBeNull("a tenant who still occupies a unit must not be deleted");
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenTenantHasLeaseHistory()
    {
        var tenant = SeedTenantWithLease(LeaseStatus.Expired);

        var act = async () => await _sut.DeleteAsync(PortfolioId, tenant.Id);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("lease history");
        (await _sut.GetAsync(PortfolioId, tenant.Id))
            .Should().NotBeNull("a tenant with lease history is preserved for past leases and payments");
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesWhenTenantHasNoLeaseHistory()
    {
        var tenant = SeedTenant("Unlinked", "Tenant", activeLeaseCount: 0);

        var deleted = await _sut.DeleteAsync(PortfolioId, tenant.Id);

        deleted.Should().BeTrue();
        (await _sut.GetAsync(PortfolioId, tenant.Id)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReportsDeleteStateForLeaseHistory()
    {
        var tenant = SeedTenantWithLease(LeaseStatus.Expired);

        var response = await _sut.GetAsync(PortfolioId, tenant.Id);

        response!.ActiveLeaseCount.Should().Be(0);
        response.LeaseHistoryCount.Should().Be(1);
        response.CanDelete.Should().BeFalse();
        response.DeleteBlockedReason.Should().Contain("lease history");
    }

    [Fact]
    public async Task GetAsync_CountsNoticeGivenLeaseAsOccupying()
    {
        var tenant = SeedTenantWithLease(LeaseStatus.NoticeGiven);

        var response = await _sut.GetAsync(PortfolioId, tenant.Id);

        // ActiveLeaseCount now means "occupying" (Active + NoticeGiven); the web delete-state helper
        // disables delete while it is > 0, so a notice-given-only tenant is also blocked in the UI.
        response!.ActiveLeaseCount.Should().Be(1);
    }

    private Tenant SeedTenantWithLease(LeaseStatus status)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Occupying",
            LastName = status.ToString(),
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
        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            TenantId = tenant.Id,
            LeaseNumber = $"L-{status}",
            Status = status,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Tenant SeedTenant(string firstName, string lastName, int activeLeaseCount)
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

        for (var i = 0; i < activeLeaseCount; i++)
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
            _ctx.Db.Leases.Add(new Lease
            {
                PortfolioId = PortfolioId,
                Property = property,
                Unit = unit,
                TenantId = tenant.Id,
                LeaseNumber = $"{firstName}-{i}",
                Status = LeaseStatus.Active,
                StartDate = now.Date,
                EndDate = now.Date.AddYears(1),
                MonthlyRent = 1200m,
                SecurityDeposit = 1200m,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.SaveChanges();
        return tenant;
    }

    private Lease SeedLeaseTenantMembership(
        Tenant tenant,
        LeaseStatus status,
        bool withSeparatePrimaryTenant = false)
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
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            TenantId = primaryTenant.Id,
            LeaseNumber = $"{tenant.FirstName}-membership",
            Status = status,
            StartDate = now.Date.AddMonths(-1),
            EndDate = now.Date.AddMonths(11),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
            LeaseTenants =
            [
                new LeaseTenant
                {
                    PortfolioId = PortfolioId,
                    TenantId = tenant.Id,
                    IsPrimary = !withSeparatePrimaryTenant,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };

        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
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

    private void SeedLeaseOnUnit(
        Tenant tenant,
        Property property,
        Unit unit,
        LeaseStatus status,
        bool withSeparatePrimaryTenant = false)
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

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = primaryTenant.Id,
            LeaseNumber = $"{tenant.FirstName}-{unit.UnitNumber}-{status}",
            Status = status,
            StartDate = now.Date.AddMonths(-1),
            EndDate = now.Date.AddMonths(11),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (withSeparatePrimaryTenant)
        {
            lease.LeaseTenants.Add(new LeaseTenant
            {
                PortfolioId = PortfolioId,
                TenantId = tenant.Id,
                IsPrimary = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.Leases.Add(lease);
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
