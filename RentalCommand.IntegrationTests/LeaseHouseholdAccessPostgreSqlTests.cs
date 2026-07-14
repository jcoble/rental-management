using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class LeaseHouseholdAccessPostgreSqlTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> GrantCodec =
        new("lease-management.party.access.grant.v1");
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> RevokeCodec =
        new("lease-management.party.access.revoke.v1");
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public LeaseHouseholdAccessPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<GrantTenantUserAccessCommand, LeasePartyMutationResult,
            GrantTenantUserAccessHandler>();
        services.AddAtomicCommandHandler<RevokeTenantUserAccessCommand, LeasePartyMutationResult,
            RevokeTenantUserAccessHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task GrantAndRevoke_ReplayOnce_AndCannotCrossRelationshipBoundary()
    {
        var scenario = await SeedScenarioAsync();
        var grant = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Grant resident access",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "household-grant-replay");
        var grantIdentity = new AtomicCommandIdentity(
            "leasing.household-access.grant",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:grant-replay");

        var grants = await Task.WhenAll(
            Atomic.ExecuteAsync(grantIdentity, grant, GrantCodec),
            Atomic.ExecuteAsync(grantIdentity, grant, GrantCodec));

        grants.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        grants.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == LeasePartyMutationOutcome.Applied);
        var tenantAccessId = grants[0].Value.TenantUserAccessIds.Should().ContainSingle().Subject;
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.CountAsync(access =>
            access.LeaseManagementPartyId == scenario.FirstPartyId && access.RevokedAtUtc == null))
            .Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == grantIdentity.CommandType && receipt.IdempotencyKey == grantIdentity.IdempotencyKey))
            .Should().Be(1);

        var crossRelationshipRevoke = new RevokeTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.SecondRelationshipId,
            scenario.SecondPartyId,
            tenantAccessId,
            "Must not cross relationship boundary",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "cross-relationship-revoke-denied");
        var crossResult = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity(
                "leasing.household-access.revoke",
                $"{scenario.PortfolioId}:{scenario.SecondRelationshipId}:{tenantAccessId}:cross-relationship"),
            crossRelationshipRevoke,
            RevokeCodec);
        crossResult.Value.Outcome.Should().Be(LeasePartyMutationOutcome.InvalidParty);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.RevokedAtUtc)
            .SingleAsync()).Should().BeNull();

        var revoke = new RevokeTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            tenantAccessId,
            "Revoke resident access",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "household-revoke-replay");
        var revokeIdentity = new AtomicCommandIdentity(
            "leasing.household-access.revoke",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{tenantAccessId}:revoke-replay");
        var revokes = await Task.WhenAll(
            Atomic.ExecuteAsync(revokeIdentity, revoke, RevokeCodec),
            Atomic.ExecuteAsync(revokeIdentity, revoke, RevokeCodec));

        revokes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        revokes.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == LeasePartyMutationOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.RevokedAtUtc)
            .SingleAsync()).Should().NotBeNull();
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == revokeIdentity.CommandType && receipt.IdempotencyKey == revokeIdentity.IdempotencyKey))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentRelationshipGrants_ForSameNormalizedEmail_ConvergeOnOnePasswordlessIdentity()
    {
        var scenario = await SeedScenarioAsync(
            "Shared.Resident@example.test",
            "shared.resident@example.test");
        var firstCommand = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Grant first resident relationship",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "same-email-first");
        var secondCommand = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.SecondRelationshipId,
            scenario.SecondPartyId,
            "Grant second resident relationship",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "same-email-second");
        var firstIdentity = new AtomicCommandIdentity(
            "leasing.household-access.grant",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:same-email-first");
        var secondIdentity = new AtomicCommandIdentity(
            "leasing.household-access.grant",
            $"{scenario.PortfolioId}:{scenario.SecondRelationshipId}:{scenario.SecondPartyId}:same-email-second");

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(firstIdentity, firstCommand, GrantCodec),
            Atomic.ExecuteAsync(secondIdentity, secondCommand, GrantCodec));

        outcomes.Select(outcome => outcome.Disposition)
            .Should().OnlyContain(disposition => disposition == AtomicCommandDisposition.Executed);
        outcomes.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == LeasePartyMutationOutcome.Applied);

        _context.Db.ChangeTracker.Clear();
        var identity = await _context.Db.Users.AsNoTracking()
            .Where(user => user.NormalizedEmail == "SHARED.RESIDENT@EXAMPLE.TEST")
            .Select(user => new { user.Id, user.PasswordHash, user.EmailConfirmed })
            .SingleAsync();
        identity.PasswordHash.Should().BeNull();
        identity.EmailConfirmed.Should().BeFalse();

        var grantedPartyIds = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.PortfolioId == scenario.PortfolioId
                && access.ApplicationUserId == identity.Id
                && access.RevokedAtUtc == null)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => access.LeaseManagementPartyId)
            .ToListAsync();
        grantedPartyIds.Should().Equal(scenario.FirstPartyId, scenario.SecondPartyId);

        var activationMessages = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(message => message.IdempotencyKey.EndsWith(":portal-activation-v2"))
            .Select(message => message.Payload)
            .ToListAsync();
        activationMessages.Should().ContainSingle();
        activationMessages[0].Should().Contain("/forgot-password?email=");
        activationMessages[0].ToLowerInvariant().Should().NotContain("temporary password");
        activationMessages[0].ToLowerInvariant().Should().NotContain("resettoken");

        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == firstIdentity.CommandType
            && (receipt.IdempotencyKey == firstIdentity.IdempotencyKey
                || receipt.IdempotencyKey == secondIdentity.IdempotencyKey)))
            .Should().Be(2);
    }

    private IAtomicUnitOfWork Atomic => _services.GetRequiredService<IAtomicUnitOfWork>();

    private async Task<Scenario> SeedScenarioAsync(
        string firstResidentEmail = "first-resident@example.test",
        string secondResidentEmail = "second-resident@example.test")
    {
        var now = DateTime.UtcNow;
        var db = _context.Db;
        var actor = new ApplicationUser
        {
            UserName = "household-manager@example.test",
            NormalizedUserName = "HOUSEHOLD-MANAGER@EXAMPLE.TEST",
            Email = "household-manager@example.test",
            NormalizedEmail = "HOUSEHOLD-MANAGER@EXAMPLE.TEST",
            DisplayName = "Household Manager",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Household Property",
            AddressLine1 = "1 Household Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(actor, property);
        await db.SaveChangesAsync();
        var firstUnit = Unit(property, "1", now);
        var secondUnit = Unit(property, "2", now);
        db.AddRange(firstUnit, secondUnit);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
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
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = actor,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();

        var firstRelationship = Relationship(property.Id, firstUnit.Id, actor.Id, "1", now);
        var secondRelationship = Relationship(property.Id, secondUnit.Id, actor.Id, "2", now);
        var firstTenant = Tenant("First", "Resident", firstResidentEmail, now);
        var secondTenant = Tenant("Second", "Resident", secondResidentEmail, now);
        db.AddRange(firstRelationship, secondRelationship, firstTenant, secondTenant);
        await db.SaveChangesAsync();
        var businessDate = DateOnly.FromDateTime(now);
        var firstParty = Party(firstRelationship.Id, firstTenant.Id, actor.Id, businessDate, now);
        var secondParty = Party(secondRelationship.Id, secondTenant.Id, actor.Id, businessDate, now);
        db.AddRange(firstParty, secondParty);
        await db.SaveChangesAsync();
        return new Scenario(1, actor.Id, session.Id, context.Id, context.AccessRevision,
            firstRelationship.Id, firstParty.Id, secondRelationship.Id, secondParty.Id);
    }

    private static Unit Unit(Property property, string number, DateTime now) => new()
    {
        PortfolioId = 1,
        Property = property,
        UnitNumber = number,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagement Relationship(
        int propertyId, int unitId, int actorUserId, string suffix, DateTime now) => new()
    {
        PortfolioId = 1,
        PropertyId = propertyId,
        UnitId = unitId,
        RelationshipNumber = $"LM-HOUSEHOLD-{suffix}",
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        CreatedByUserId = actorUserId,
    };

    private static Tenant Tenant(string firstName, string lastName, string email, DateTime now) => new()
    {
        PortfolioId = 1,
        FirstName = firstName,
        LastName = lastName,
        Email = email,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static LeaseManagementParty Party(
        int relationshipId, int tenantId, int actorUserId, DateOnly effectiveFrom, DateTime now) => new()
    {
        PortfolioId = 1,
        LeaseManagementId = relationshipId,
        TenantId = tenantId,
        Role = LeaseManagementPartyRole.PrimaryTenant,
        EffectiveFrom = effectiveFrom,
        ChangeReason = "Initial household",
        CreatedAtUtc = now,
        CreatedByUserId = actorUserId,
    };

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:lease-household-access";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed record Scenario(
        int PortfolioId,
        int ActorUserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        int FirstRelationshipId,
        int FirstPartyId,
        int SecondRelationshipId,
        int SecondPartyId);
}
