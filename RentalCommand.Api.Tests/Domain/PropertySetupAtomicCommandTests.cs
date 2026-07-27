using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PropertySetupAtomicCommandTests : IDisposable
{
    private const int PortfolioId = 1;
    private readonly SqliteTestContext _ctx = new();
    private readonly MutableTimeProvider _timeProvider = new(
        new DateTimeOffset(2027, 1, 5, 5, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly PropertyService _sut;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly WorkspaceReadScope _scope;

    public PropertySetupAtomicCommandTests()
    {
        _ctx.Db.Database.InstallCanonicalLeaseProjectionViewsForSqlite();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(PropertySetupAtomicCommandTests));
        _services = AtomicDomainTestKernel.CreateForCoreCrud(_ctx.ConnectionString, _timeProvider);
        _atomic = _services.GetRequiredService<IAtomicUnitOfWork>();
        _sut = new PropertyService(
            _ctx.Db,
            Mock.Of<IDataUpdateService>(),
            _timeProvider,
            _atomic);
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
        var command = AtomicCoreCrudMutation.Command(
            _scope,
            AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Create,
            entityId: 0,
            operationKey: "ordinary-create-is-disabled",
            request: request);

        Func<Task> act = async () => await _atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command),
            command,
            AtomicCoreCrudMutation.Codec);

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
        var command = new AtomicCoreCrudMutationCommand(
            _scope.PortfolioId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            AtomicCoreCrudMutationDomain.Property,
            AtomicCoreCrudMutationOperation.Update,
            created!.Property.Id,
            requestJson,
            "patch-structure-ignored");
        await _atomic.ExecuteAsync(
            AtomicCoreCrudMutation.Identity(command), command, AtomicCoreCrudMutation.Codec);

        (await _ctx.Db.Properties.SingleAsync()).RentalStructure.Should().Be(RentalStructure.SingleRental);
        (await _ctx.Db.Units.CountAsync()).Should().Be(1);
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
