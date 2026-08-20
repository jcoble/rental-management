using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PropertySetupAtomicCommandTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _postgres;
    private readonly SqliteTestContext _ctx = new();
    private readonly MutableTimeProvider _timeProvider = new(
        new DateTimeOffset(2027, 1, 5, 5, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly PropertyService _sut;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly WorkspaceReadScope _scope;

    public PropertySetupAtomicCommandTests(MigratedPostgreSqlFixture postgres)
    {
        _postgres = postgres;
        _ctx.Db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(PropertySetupAtomicCommandTests));
        _services = AtomicDomainTestKernel.CreateForCoreCrud(_ctx.ConnectionString, _timeProvider);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        _sut = _services.GetRequiredService<PropertyService>();
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    [Fact]
    public async Task Setup_creates_explicit_single_rental_unit_once_and_replays_the_receipt()
    {
        var request = SingleRentalRequest();

        var first = await _sut.SetupAsync(_scope, request, "first-run-home");
        var replay = await _sut.SetupAsync(_scope, request, "first-run-home");

        first.Should().NotBeNull();
        replay.Should().BeEquivalentTo(first);
        first!.Updated.Should().BeFalse();
        first.Property.RentalStructure.Should().Be(RentalStructure.SingleRental);
        first.Property.UnitCount.Should().Be(1);
        first.Units.Should().ContainSingle().Which.UnitNumber.Should().Be("Home");
        (await _ctx.Db.Properties.CountAsync()).Should().Be(1);
        (await _ctx.Db.Units.CountAsync()).Should().Be(1,
            "setup creates only the Unit explicitly supplied by the caller");
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "rental.property.setup")).Should().Be(1);
    }

    [Fact]
    public async Task Setup_defaults_ownership_to_a_new_self_owner_when_the_portfolio_has_none()
    {
        var created = await _sut.SetupAsync(_scope, SingleRentalRequest(), "self-owner-default");

        created.Should().NotBeNull();
        var owner = await _ctx.Db.OwnerEntities.AsNoTracking()
            .SingleAsync(entity => entity.PortfolioId == PortfolioId && entity.IsPrimary);
        (await _ctx.Db.PropertyOwnerships.AsNoTracking()
            .SingleAsync(ownership => ownership.PropertyId == created!.Property.Id))
            .Should().Match<PropertyOwnership>(ownership =>
                ownership.OwnerEntityId == owner.Id && ownership.OwnershipSharePercent == 100m);
        (await _ctx.Db.OwnerUserAccesses.AsNoTracking()
            .SingleAsync(access => access.OwnerEntityId == owner.Id))
            .ApplicationUserId.Should().Be(_scope.UserId);
    }

    [Fact]
    public async Task Setup_reuses_an_existing_self_owner_without_creating_a_duplicate()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Existing self owner",
            Email = "existing-self-owner@example.test",
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(existing);
        await _ctx.Db.SaveChangesAsync();

        var created = await _sut.SetupAsync(_scope, SingleRentalRequest(), "self-owner-reuse");

        created.Should().NotBeNull();
        (await _ctx.Db.OwnerEntities.AsNoTracking()
            .CountAsync(entity => entity.PortfolioId == PortfolioId && entity.IsPrimary))
            .Should().Be(1);
        (await _ctx.Db.PropertyOwnerships.AsNoTracking()
            .SingleAsync(ownership => ownership.PropertyId == created!.Property.Id))
            .OwnerEntityId.Should().Be(existing.Id);
    }

    [Fact]
    public async Task Setup_honors_an_explicit_owner_instead_of_defaulting_to_self_owner()
    {
        var explicitOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            OwnerEntityType = OwnerEntityType.LLC,
            Name = "Explicit Holdings LLC",
            IsPrimary = false,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime,
        };
        _ctx.Db.OwnerEntities.Add(explicitOwner);
        await _ctx.Db.SaveChangesAsync();

        var request = SingleRentalRequest();
        request.Property.Ownerships =
        [new PropertyOwnershipRequest { OwnerEntityId = explicitOwner.Id }];
        var created = await _sut.SetupAsync(_scope, request, "explicit-owner-default");

        created.Should().NotBeNull();
        (await _ctx.Db.PropertyOwnerships.AsNoTracking()
            .SingleAsync(ownership => ownership.PropertyId == created!.Property.Id))
            .OwnerEntityId.Should().Be(explicitOwner.Id);
        (await _ctx.Db.OwnerEntities.AsNoTracking()
            .CountAsync(entity => entity.PortfolioId == PortfolioId && entity.IsPrimary))
            .Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_first_property_setup_creates_one_active_primary_owner()
    {
        await using var context = await _postgres.CreateContextAsync();
        var scope = context.Db.SeedAdministratorScope(
            PortfolioId, nameof(Concurrent_first_property_setup_creates_one_active_primary_owner));
        await using var firstProvider = AtomicDomainTestKernel.CreateForCoreCrudPostgreSql(
            context.ConnectionString, _timeProvider);
        await using var secondProvider = AtomicDomainTestKernel.CreateForCoreCrudPostgreSql(
            context.ConnectionString, _timeProvider);
        await using var firstScope = firstProvider.CreateAsyncScope();
        await using var secondScope = secondProvider.CreateAsyncScope();
        var firstService = firstScope.ServiceProvider.GetRequiredService<PropertyService>();
        var secondService = secondScope.ServiceProvider.GetRequiredService<PropertyService>();
        var firstRequest = SingleRentalRequest();
        firstRequest.Property.Name = "Concurrent First Property A";
        firstRequest.Property.AddressLine1 = "101 Concurrent Way";
        var secondRequest = SingleRentalRequest();
        secondRequest.Property.Name = "Concurrent First Property B";
        secondRequest.Property.AddressLine1 = "102 Concurrent Way";

        await Task.WhenAll(
            firstService.SetupAsync(scope, firstRequest, "concurrent-first-property-a"),
            secondService.SetupAsync(scope, secondRequest, "concurrent-first-property-b"));

        await using var verify = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(context.ConnectionString)
                .Options);
        var primaryOwners = await verify.OwnerEntities.AsNoTracking()
            .Where(entity => entity.PortfolioId == PortfolioId
                && entity.IsPrimary && entity.DeletedAt == null)
            .ToListAsync();
        primaryOwners.Should().ContainSingle();
        (await verify.Properties.CountAsync(property => property.PortfolioId == PortfolioId))
            .Should().Be(2);
    }

    [Theory]
    [InlineData(PropertyType.Storage)]
    [InlineData(PropertyType.Parking)]
    [InlineData(PropertyType.Commercial)]
    public async Task Setup_allows_blank_beds_and_baths_for_non_residential_property_types(
        PropertyType propertyType)
    {
        var request = SingleRentalRequest();
        request.Property.PropertyType = propertyType;
        request.Units[0].Bedrooms = null;
        request.Units[0].Bathrooms = null;

        var created = await _sut.SetupAsync(_scope, request, $"optional-bed-bath-{propertyType}");

        created.Should().NotBeNull();
        created!.Units.Should().ContainSingle().Which.Should().Match<UnitResponse>(unit =>
            unit.Bedrooms == 0m && unit.Bathrooms == 0m && unit.PropertyType == propertyType);
    }

    [Fact]
    public async Task Setup_rejects_omitted_residential_beds_and_baths_without_writes()
    {
        var request = SingleRentalRequest();
        request.Units[0].Bedrooms = null;
        request.Units[0].Bathrooms = null;

        Func<Task> act = async () => await _sut.SetupAsync(
            _scope, request, "missing-residential-bed-bath");

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*Bedrooms and bathrooms are required for residential dwellings.*");
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "rental.property.setup")).Should().Be(0);
    }

    [Fact]
    public async Task Setup_uses_injected_app_clock_for_property_and_unit_business_timestamps()
    {
        var createdAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var created = await _sut.SetupAsync(_scope, SingleRentalRequest(), "setup-clock-create");

        created.Should().NotBeNull();
        created!.Property.CreatedAt.Should().Be(createdAtUtc);
        created.Property.UpdatedAt.Should().Be(createdAtUtc);
        created.Units.Should().ContainSingle().Which.CreatedAt.Should().Be(createdAtUtc);
        created.Units.Single().UpdatedAt.Should().Be(createdAtUtc);

        var persistedProperty = await _ctx.Db.Properties.AsNoTracking().SingleAsync(property => property.Id == created.Property.Id);
        var persistedUnit = await _ctx.Db.Units.AsNoTracking().SingleAsync(unit => unit.PropertyId == created.Property.Id);
        persistedProperty.CreatedAt.Should().Be(createdAtUtc);
        persistedProperty.UpdatedAt.Should().Be(createdAtUtc);
        persistedUnit.CreatedAt.Should().Be(createdAtUtc);
        persistedUnit.UpdatedAt.Should().Be(createdAtUtc);

        var updatedAtUtc = new DateTimeOffset(2027, 1, 6, 14, 30, 0, TimeSpan.Zero);
        _timeProvider.SetUtcNow(updatedAtUtc);
        var update = SingleRentalRequest();
        update.PropertyId = created.Property.Id;
        update.Property.RentalStructure = RentalStructure.MultiRental;
        update.Units =
        [
            new SetupUnitRequest
            {
                UnitNumber = "Unit B",
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1500m,
            },
        ];

        var updated = await _sut.SetupAsync(_scope, update, "setup-clock-update");

        updated.Should().NotBeNull();
        updated!.Property.CreatedAt.Should().Be(createdAtUtc);
        updated.Property.UpdatedAt.Should().Be(updatedAtUtc.UtcDateTime);
        updated.Units.Should().Contain(unit =>
            unit.UnitNumber == "Unit B"
            && unit.CreatedAt == updatedAtUtc.UtcDateTime
            && unit.UpdatedAt == updatedAtUtc.UtcDateTime);

        _ctx.Db.ChangeTracker.Clear();
        persistedProperty = await _ctx.Db.Properties.AsNoTracking().SingleAsync(property => property.Id == created.Property.Id);
        var newUnit = await _ctx.Db.Units.AsNoTracking().SingleAsync(unit => unit.PropertyId == created.Property.Id && unit.UnitNumber == "Unit B");
        persistedProperty.CreatedAt.Should().Be(createdAtUtc);
        persistedProperty.UpdatedAt.Should().Be(updatedAtUtc.UtcDateTime);
        newUnit.CreatedAt.Should().Be(updatedAtUtc.UtcDateTime);
        newUnit.UpdatedAt.Should().Be(updatedAtUtc.UtcDateTime);
    }

    [Fact]
    public async Task Setup_rejects_invalid_single_rental_shape_without_partial_property_or_receipt()
    {
        var request = SingleRentalRequest();
        request.Units.Add(new SetupUnitRequest { UnitNumber = "Second" });

        Func<Task> act = async () => await _sut.SetupAsync(_scope, request, "invalid-single-home");

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*exactly one Unit*");
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "rental.property.setup")).Should().Be(0);
    }

    [Fact]
    public async Task Completed_setup_replay_rechecks_the_current_session()
    {
        var request = SingleRentalRequest();
        await _sut.SetupAsync(_scope, request, "replay-authorization");
        var session = await _ctx.Db.AuthSessions.SingleAsync(row => row.Id == _scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _ctx.Db.SaveChangesAsync();

        Func<Task> replay = async () => await _sut.SetupAsync(_scope, request, "replay-authorization");

        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        (await _ctx.Db.Properties.CountAsync()).Should().Be(1);
        (await _ctx.Db.Units.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Ordinary_property_create_is_rejected_without_writing_invalid_inventory()
    {
        var request = SingleRentalRequest().Property;
        var command = CoreCrudWriteSupport.Request(
            _scope,
            AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Create,
            entityId: 0,
            operationKey: "ordinary-create-is-disabled",
            request: request);

        var rules = new PropertyTenantCrudWriteRules(
            _services.GetRequiredService<RentalCommandDbContext>());
        var write = CoreCrudWriteSupport.Write(
            command, rules.RejectPropertyCreateAsync, rules.AuthorizeReplayAsync);
        Func<Task> act = async () => await _services
            .GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(CoreCrudWriteSupport.IdempotencyKey(command), write);

        await act.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*atomic Property setup command*");
        (await _ctx.Db.Properties.CountAsync()).Should().Be(0);
        (await _ctx.Db.Units.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Setup_update_changes_structure_and_preserves_existing_unit_identity()
    {
        var created = await _sut.SetupAsync(
            _scope, SingleRentalRequest(), "structure-create");
        var originalUnitId = created!.Units.Single().Id;

        var multi = SingleRentalRequest();
        multi.PropertyId = created.Property.Id;
        multi.Property.RentalStructure = RentalStructure.MultiRental;
        multi.Units =
        [
            new SetupUnitRequest
            {
                UnitNumber = "Second",
                Bedrooms = 2,
                Bathrooms = 1,
                MarketRent = 1400m,
            },
        ];

        var updated = await _sut.SetupAsync(_scope, multi, "structure-expand");

        updated!.Updated.Should().BeTrue();
        updated.Property.RentalStructure.Should().Be(RentalStructure.MultiRental);
        updated.Units.Should().HaveCount(2);
        updated.Units.Should().Contain(unit => unit.Id == originalUnitId && unit.UnitNumber == "Home");
        (await _ctx.Db.Units.CountAsync()).Should().Be(2);

        var invalidContraction = SingleRentalRequest();
        invalidContraction.PropertyId = created.Property.Id;
        invalidContraction.Units = [];

        Func<Task> contract = async () => await _sut.SetupAsync(
            _scope, invalidContraction, "structure-contract");

        await contract.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*cannot be converted to SingleRental*");
        (await _ctx.Db.Properties.SingleAsync()).RentalStructure.Should().Be(RentalStructure.MultiRental);
        (await _ctx.Db.Units.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Ordinary_patch_has_no_rental_structure_mutation_contract()
    {
        var created = await _sut.SetupAsync(
            _scope, SingleRentalRequest(), "patch-structure-create");
        typeof(UpdatePropertyRequest).GetProperty("RentalStructure").Should().BeNull(
            "structural transitions belong only to the atomic Property setup contract");

        var requestJson = "{\"rentalStructure\":\"MultiRental\",\"notes\":\"ordinary edit\"}";
        var request = JsonSerializer.Deserialize<UpdatePropertyRequest>(requestJson)!;
        await _sut.UpdateAsync(
            _scope, created!.Property.Id, request, "patch-structure-ignored");

        (await _ctx.Db.Properties.SingleAsync()).RentalStructure.Should().Be(RentalStructure.SingleRental);
        (await _ctx.Db.Units.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Ordinary_patch_persists_supported_property_financial_and_operations_fields_atomically()
    {
        await using var context = await _postgres.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<PropertyService>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        await using var serviceProvider = services.BuildServiceProvider();
        await using var serviceScope = serviceProvider.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var scope = db.SeedAdministratorScope(
            PortfolioId,
            nameof(Ordinary_patch_persists_supported_property_financial_and_operations_fields_atomically));
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
        var sut = serviceScope.ServiceProvider.GetRequiredService<PropertyService>();

        var created = await sut.SetupAsync(
            scope, SingleRentalRequest(), "patch-financial-fields-create");
        var update = new UpdatePropertyRequest
        {
            Name = "Arbor House",
            YearBuilt = 1998,
            ManagementFeePercent = 8.5m,
            Notes = "Simulation property P001.",
            PurchasePrice = 350_000m,
            LandValue = 50_000m,
            InServiceDate = new DateTime(2024, 1, 15),
            ManualAnnualDepreciation = 10_909m,
        };

        var saved = await sut.UpdateAsync(
            scope,
            created!.Property.Id,
            update,
            "patch-financial-fields");

        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Arbor House");
        saved.YearBuilt.Should().Be(1998);
        saved.ManagementFeePercent.Should().Be(8.5m);
        saved.Notes.Should().Be("Simulation property P001.");
        saved.PurchasePrice.Should().Be(350_000m);
        saved.LandValue.Should().Be(50_000m);
        saved.InServiceDate.Should().NotBeNull();
        DateOnly.FromDateTime(saved.InServiceDate!.Value).Should().Be(new DateOnly(2024, 1, 15));
        saved.ManualAnnualDepreciation.Should().Be(10_909m);

        var persisted = await db.Properties.AsNoTracking()
            .SingleAsync(property => property.Id == created.Property.Id);
        persisted.YearBuilt.Should().Be(1998);
        persisted.ManagementFeePercent.Should().Be(8.5m);
        persisted.Notes.Should().Be("Simulation property P001.");
        persisted.PurchasePrice.Should().Be(350_000m);
        persisted.LandValue.Should().Be(50_000m);
        persisted.InServiceDate.Should().NotBeNull();
        DateOnly.FromDateTime(persisted.InServiceDate!.Value).Should().Be(new DateOnly(2024, 1, 15));
        persisted.ManualAnnualDepreciation.Should().Be(10_909m);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "rental.property.update")).Should().Be(1);
    }

    private static SetupPropertyRequest SingleRentalRequest() => new()
    {
        Property = new CreatePropertyRequest
        {
            Name = "100 Proof Lane",
            PropertyType = PropertyType.SingleFamily,
            RentalStructure = RentalStructure.SingleRental,
            AddressLine1 = "100 Proof Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
        },
        Units =
        [
            new SetupUnitRequest
            {
                UnitNumber = "Home",
                Bedrooms = 3,
                Bathrooms = 2,
                MarketRent = 1800m,
            },
        ],
    };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
