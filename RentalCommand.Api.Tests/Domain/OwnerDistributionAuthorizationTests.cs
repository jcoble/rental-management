using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class OwnerDistributionAuthorizationTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _atomicServices = null!;
    private OwnerDistributionService _service = null!;

    public OwnerDistributionAuthorizationTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _atomicServices = AtomicDomainTestKernel.CreateForMoneyPostgreSql(_ctx.ConnectionString);
        _service = new OwnerDistributionService(
            _ctx.Db,
            TimeProvider.System,
            _atomicServices.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
        SeedPortfolio();
    }

    public async Task DisposeAsync()
    {
        await _atomicServices.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task PropertyManagerReadsOnlyAssignedPropertyDistributions()
    {
        var owner = SeedOwner("Assigned Owner");
        var assignedProperty = SeedProperty(owner.Id, "Assigned Property");
        var otherProperty = SeedProperty(owner.Id, "Other Property");
        var assigned = SeedDistribution(owner.Id, assignedProperty.Id, 100m);
        var other = SeedDistribution(owner.Id, otherProperty.Id, 200m);
        var scope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, assignedProperty.Id, nameof(PropertyManagerReadsOnlyAssignedPropertyDistributions));

        var page = await _service.ListPageAsync(scope, new OwnerDistributionListQuery());
        var assignedDetail = await _service.GetAsync(scope, assigned.Id);
        var otherDetail = await _service.GetAsync(scope, other.Id);

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.Id == assigned.Id);
        assignedDetail.Should().NotBeNull();
        otherDetail.Should().BeNull();
    }

    [Fact]
    public async Task PropertyManagerDirectServiceAttemptsCannotMutateOwnerDistributions()
    {
        var owner = SeedOwner("Managed Owner");
        var property = SeedProperty(owner.Id, "Managed Property");
        var distribution = SeedDistribution(owner.Id, property.Id, 100m);
        var scope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, property.Id, nameof(PropertyManagerDirectServiceAttemptsCannotMutateOwnerDistributions));

        var create = async () => await _service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = DateTime.UtcNow,
            Amount = 50m,
        }, $"pm-create-{Guid.NewGuid():N}");
        var update = async () => await _service.UpdateAsync(scope, distribution.Id,
            new UpdateOwnerDistributionRequest { Amount = 125m }, $"pm-update-{Guid.NewGuid():N}");
        var delete = async () => await _service.DeleteAsync(
            scope, distribution.Id, $"pm-delete-{Guid.NewGuid():N}");

        await create.Should().ThrowAsync<UnauthorizedAccessException>();
        await update.Should().ThrowAsync<UnauthorizedAccessException>();
        await delete.Should().ThrowAsync<UnauthorizedAccessException>();
        _ctx.Db.ChangeTracker.Clear();
        var persisted = _ctx.Db.OwnerDistributions.IgnoreQueryFilters()
            .Single(item => item.Id == distribution.Id);
        persisted.Amount.Should().Be(100m);
        persisted.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task AdministratorWithDisbursementAndDestructiveAuthorityCanDeleteDistribution()
    {
        var owner = SeedOwner("Administrator Owner");
        var property = SeedProperty(owner.Id, "Administrator Property");
        var distribution = SeedDistribution(owner.Id, property.Id, 100m);
        var scope = _ctx.Db.SeedAdministratorScope(
            PortfolioId, nameof(AdministratorWithDisbursementAndDestructiveAuthorityCanDeleteDistribution));

        var deleted = await _service.DeleteAsync(
            scope, distribution.Id, $"admin-delete-{Guid.NewGuid():N}");

        deleted.Should().BeTrue();
        _ctx.Db.ChangeTracker.Clear();
        _ctx.Db.OwnerDistributions.IgnoreQueryFilters()
            .Single(item => item.Id == distribution.Id)
            .DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public void EndpointsKeepPropertyScopedReadsInSqlAndDeclareMutationPolicies()
    {
        Policies(nameof(OwnerDistributionController.List)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.ListPage)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.Get)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.Create))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Update))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Delete)).Should().BeEquivalentTo(
            CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage,
            CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive);
    }

    [Fact]
    public void ControllerRequiresTheActiveManagementExperienceForEveryAction()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RentalCommand.Api", "Controllers", "OwnerDistributionController.cs"));

        source.Should().NotContain("GetWorkspaceReadScope()");
        source.Split("TryReadManagementScope(out var scope)")
            .Should().HaveCount(7, "all six owner-distribution actions must fail closed outside Management");
    }

    private static string[] Policies(string methodName) =>
        typeof(OwnerDistributionController).GetMethod(methodName)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy!)
            .ToArray();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
    }

    private void SeedPortfolio()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Owner Distribution Authorization",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private OwnerEntity SeedOwner(string name)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(int ownerEntityId, string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerEntityId,
            Name = name,
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private OwnerDistribution SeedDistribution(int ownerEntityId, int propertyId, decimal amount)
    {
        var now = DateTime.UtcNow;
        var distribution = new OwnerDistribution
        {
            PortfolioId = PortfolioId,
            OwnerEntityId = ownerEntityId,
            PropertyId = propertyId,
            Date = now,
            Amount = amount,
            Method = DistributionMethod.Check,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerDistributions.Add(distribution);
        _ctx.Db.SaveChanges();
        return distribution;
    }
}
