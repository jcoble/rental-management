using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// The landlord IS the first owner (C-4): onboarding auto-creates a primary self-owner from the account
/// rather than forcing a manual "add an owner" step. These tests pin the invariant that drives the
/// getting-started "owner" task auto-completing — a primary <see cref="OwnerEntity"/> exists, derived
/// from the user, linked back to the user, and never duplicated.
/// </summary>
public class SelfOwnerProvisionerTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private SelfOwnerProvisioner BuildProvisioner() =>
        new(_ctx.Db, NullLogger<SelfOwnerProvisioner>.Instance, TimeProvider.System);

    private ApplicationUser SeedUser(string displayName, string email, int portfolioId = 1)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            PortfolioId = portfolioId,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        _ctx.Db.Users.Add(user);
        _ctx.Db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task EnsureSelfOwner_CreatesPrimaryOwner_FromAccount_AndLinksUser()
    {
        var user = SeedUser("Jane Landlord", "jane@example.com");

        var owner = await BuildProvisioner().EnsureSelfOwnerAsync(user, portfolioId: 1, CancellationToken.None);

        owner.Should().NotBeNull();
        owner!.IsPrimary.Should().BeTrue();
        owner.PortfolioId.Should().Be(1);
        owner.OwnerEntityType.Should().Be(OwnerEntityType.Person);
        owner.Name.Should().Be("Jane Landlord");          // from DisplayName
        owner.Email.Should().Be("jane@example.com");       // from the account email

        // Persisted as the single owner for the portfolio...
        var owners = await _ctx.Db.OwnerEntities.Where(o => o.PortfolioId == 1).ToListAsync();
        owners.Should().ContainSingle();

        // ...and the user is linked back to it.
        var reloaded = await _ctx.Db.Users.SingleAsync(u => u.Id == user.Id);
        reloaded.OwnerEntityId.Should().Be(owner.Id);
    }

    [Fact]
    public async Task EnsureSelfOwner_FallsBackToEmailLocalPart_WhenNoDisplayName()
    {
        var user = SeedUser(displayName: "", email: "bob.smith@example.com");

        var owner = await BuildProvisioner().EnsureSelfOwnerAsync(user, 1, CancellationToken.None);

        owner!.Name.Should().Be("bob.smith"); // local part of the email
    }

    [Fact]
    public async Task EnsureSelfOwner_IsIdempotent_NoDuplicatePrimaryOwner()
    {
        var user = SeedUser("Jane Landlord", "jane@example.com");
        var provisioner = BuildProvisioner();

        var first = await provisioner.EnsureSelfOwnerAsync(user, 1, CancellationToken.None);
        var second = await provisioner.EnsureSelfOwnerAsync(user, 1, CancellationToken.None);

        second!.Id.Should().Be(first!.Id);
        (await _ctx.Db.OwnerEntities.CountAsync(o => o.PortfolioId == 1 && o.IsPrimary)).Should().Be(1);
    }

    [Fact]
    public async Task EnsureSelfOwner_DoesNotCreateSecondPrimary_WhenOwnerAlreadyExists()
    {
        // A portfolio that already has a (manually added) primary owner must not get a second one.
        _ctx.Db.OwnerEntities.Add(new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.LLC,
            Name = "Existing Holdings LLC",
            IsPrimary = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();

        var user = SeedUser("Jane Landlord", "jane@example.com");
        var owner = await BuildProvisioner().EnsureSelfOwnerAsync(user, 1, CancellationToken.None);

        owner!.Name.Should().Be("Existing Holdings LLC"); // returned the existing primary
        (await _ctx.Db.OwnerEntities.CountAsync(o => o.PortfolioId == 1)).Should().Be(1);
    }
}
