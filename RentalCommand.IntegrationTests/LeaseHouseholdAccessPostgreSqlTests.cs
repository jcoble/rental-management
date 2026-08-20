using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        services.AddAtomicPersistenceKernel();
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
            "lease-management.party.access.grant",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:grant-replay");

        var grants = await Task.WhenAll(
            ExecuteAtomicAsync(grantIdentity, grant, GrantCodec),
            ExecuteAtomicAsync(grantIdentity, grant, GrantCodec));

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

        var tenantRoleId = await _context.Db.RoleProfiles.AsNoTracking()
            .Where(role => role.Key == RoleProfileKeys.TenantPortal)
            .Select(role => role.Id)
            .SingleAsync();
        var tenantMembershipId = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.AccessContext!.Membership!.Id)
            .SingleAsync();
        var tenantAccessContextId = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.AccessContextId)
            .SingleAsync();
        var grantedAtUtc = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.GrantedAtUtc)
            .SingleAsync();
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == grantIdentity.CommandType
                && row.CommandIdempotencyKey == grantIdentity.IdempotencyKey)
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync()).Should().OnlyContain(timestamp => timestamp == grantedAtUtc);
        await _context.Db.MembershipRoleAssignments
            .Where(assignment =>
                assignment.WorkspaceMembershipId == tenantMembershipId &&
                assignment.PortfolioId == scenario.PortfolioId &&
                assignment.RoleProfileId == tenantRoleId)
            .ExecuteDeleteAsync();
        _context.Db.ChangeTracker.Clear();
        var repairGrant = grant with { DeliveryIdempotencyKey = "household-grant-repair-tenant-role" };
        var repairResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:grant-repair-tenant-role"),
            repairGrant,
            GrantCodec);
        repairResult.Value.Outcome.Should().Be(LeasePartyMutationOutcome.AlreadyActive);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.MembershipRoleAssignments.AsNoTracking()
            .CountAsync(assignment =>
                assignment.WorkspaceMembershipId == tenantMembershipId &&
                assignment.PortfolioId == scenario.PortfolioId &&
                assignment.RoleProfileId == tenantRoleId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.RevokedAtUtc == null))
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
        var crossResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.revoke",
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
            "lease-management.party.access.revoke",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{tenantAccessId}:revoke-replay");
        var revisionBeforeRevoke = await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(context => context.Id == tenantAccessContextId)
            .Select(context => context.AccessRevision)
            .SingleAsync();
        var revokes = await Task.WhenAll(
            ExecuteAtomicAsync(revokeIdentity, revoke, RevokeCodec),
            ExecuteAtomicAsync(revokeIdentity, revoke, RevokeCodec));

        revokes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        revokes.Select(outcome => outcome.Value.Outcome)
            .Should().OnlyContain(outcome => outcome == LeasePartyMutationOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.RevokedAtUtc)
            .SingleAsync()).Should().NotBeNull();
        var revokedAtUtc = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == tenantAccessId)
            .Select(access => access.RevokedAtUtc)
            .SingleAsync();
        (await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(context => context.Id == tenantAccessContextId)
            .Select(context => context.AccessRevision)
            .SingleAsync()).Should().Be(revisionBeforeRevoke + 1);
        (await _context.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == revokeIdentity.CommandType
                && row.CommandIdempotencyKey == revokeIdentity.IdempotencyKey)
            .Select(row => row.Timestamp)
            .Distinct()
            .ToListAsync()).Should().OnlyContain(timestamp => timestamp == revokedAtUtc);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == revokeIdentity.CommandType && receipt.IdempotencyKey == revokeIdentity.IdempotencyKey))
            .Should().Be(1);
    }

    [Fact]
    public async Task ExactGrantReplay_RejectsStaleActorAccessRevision()
    {
        var scenario = await SeedScenarioAsync();
        var command = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Grant before stale replay",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "stale-grant-replay");
        var identity = new AtomicCommandIdentity(
            "lease-management.party.access.grant",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:stale-grant-replay");

        (await ExecuteAtomicAsync(identity, command, GrantCodec)).Value.Outcome
            .Should().Be(LeasePartyMutationOutcome.Applied);

        var actorContext = await _context.Db.WorkspaceAccessContexts
            .SingleAsync(context => context.Id == scenario.AccessContextId);
        actorContext.AdvanceRevision(actorContext.AccessRevision);
        actorContext.UpdatedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        Func<Task> replay = async () => await ExecuteAtomicAsync(identity, command, GrantCodec);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
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
            "lease-management.party.access.grant",
            $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:same-email-first");
        var secondIdentity = new AtomicCommandIdentity(
            "lease-management.party.access.grant",
            $"{scenario.PortfolioId}:{scenario.SecondRelationshipId}:{scenario.SecondPartyId}:same-email-second");

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(firstIdentity, firstCommand, GrantCodec),
            ExecuteAtomicAsync(secondIdentity, secondCommand, GrantCodec));

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

        var tenantContext = await _context.Db.WorkspaceAccessContexts.AsNoTracking()
            .Where(context => context.UserId == identity.Id && context.PortfolioId == scenario.PortfolioId)
            .Select(context => new { context.Id, context.LastAuthorizedExperience })
            .SingleAsync();
        tenantContext.LastAuthorizedExperience.Should().Be(WorkspaceExperience.Tenant);

        var tenantMembership = await _context.Db.WorkspaceMemberships.AsNoTracking()
            .Where(membership => membership.AccessContextId == tenantContext.Id)
            .Select(membership => new { membership.Id, membership.DefaultExperience, membership.Status })
            .SingleAsync();
        tenantMembership.DefaultExperience.Should().Be(WorkspaceExperience.Tenant);
        tenantMembership.Status.Should().Be(WorkspaceMembershipStatus.Active);

        var tenantRoleId = await _context.Db.RoleProfiles.AsNoTracking()
            .Where(role => role.Key == RoleProfileKeys.TenantPortal)
            .Select(role => role.Id)
            .SingleAsync();
        (await _context.Db.MembershipRoleAssignments.AsNoTracking()
            .CountAsync(assignment =>
                assignment.WorkspaceMembershipId == tenantMembership.Id &&
                assignment.PortfolioId == scenario.PortfolioId &&
                assignment.RoleProfileId == tenantRoleId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.RevokedAtUtc == null))
            .Should().Be(1);

        var grantedPartyIds = await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.PortfolioId == scenario.PortfolioId
                && access.ApplicationUserId == identity.Id
                && access.RevokedAtUtc == null)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => access.LeaseManagementPartyId)
            .ToListAsync();
        grantedPartyIds.Should().Equal(scenario.FirstPartyId, scenario.SecondPartyId);

        var invitations = await _context.Db.WorkspaceInvitations.AsNoTracking()
            .Where(invitation =>
                invitation.PortfolioId == scenario.PortfolioId &&
                invitation.WorkspaceMembershipId == tenantMembership.Id &&
                invitation.InvitedUserId == identity.Id &&
                invitation.AcceptedAtUtc == null &&
                invitation.RevokedAtUtc == null)
            .ToListAsync();
        invitations.Should().ContainSingle();

        var activationMessages = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(message => message.IdempotencyKey.StartsWith("tenant-portal-invitation:"))
            .Select(message => message.Payload)
            .ToListAsync();
        activationMessages.Should().ContainSingle();
        activationMessages[0].Should().Contain("/activate-team?token=");
        activationMessages[0].ToLowerInvariant().Should().NotContain("temporary password");
        activationMessages[0].ToLowerInvariant().Should().NotContain("resettoken");

        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == firstIdentity.CommandType
            && (receipt.IdempotencyKey == firstIdentity.IdempotencyKey
                || receipt.IdempotencyKey == secondIdentity.IdempotencyKey)))
            .Should().Be(2);
    }

    [Fact]
    public async Task TenantInvitation_ActivatesThroughWorkspaceInvitation_AndRevocationCancelsUnusedLinks()
    {
        var scenario = await SeedScenarioAsync();
        var grant = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Grant resident activation",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "tenant-activation-proof");
        var grantResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:tenant-activation-proof"),
            grant,
            GrantCodec);

        grantResult.Value.Outcome.Should().Be(LeasePartyMutationOutcome.Applied);
        var accessId = grantResult.Value.TenantUserAccessIds.Should().ContainSingle().Subject;
        _context.Db.ChangeTracker.Clear();

        var payload = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(message => message.IdempotencyKey.StartsWith("tenant-portal-invitation:"))
            .Select(message => message.Payload)
            .SingleAsync();
        var token = ExtractActivationToken(payload);
        var tokenHash = HashInvitationToken(token);
        var invitation = await _context.Db.WorkspaceInvitations.AsNoTracking()
            .Where(row => row.TokenHash == tokenHash)
            .Select(row => new { row.Id, row.InvitedUserId, row.WorkspaceMembershipId })
            .SingleAsync();

        var activation = await ActivateInvitationAsApiAsync(
            invitation.Id,
            invitation.InvitedUserId,
            tokenHash,
            "hashed-first-tenant-password");
        activation.Should().NotBeNull();
        activation!.AccessContextId.Should().Be(await _context.Db.TenantUserAccesses.AsNoTracking()
            .Where(access => access.Id == accessId)
            .Select(access => access.AccessContextId)
            .SingleAsync());

        var activatedUser = await _context.Db.Users.AsNoTracking()
            .Where(user => user.Id == invitation.InvitedUserId)
            .Select(user => new { user.PasswordHash, user.EmailConfirmed })
            .SingleAsync();
        activatedUser.PasswordHash.Should().Be("hashed-first-tenant-password");
        activatedUser.EmailConfirmed.Should().BeTrue();

        var expiredReplay = await ActivateInvitationAsApiAsync(
            invitation.Id,
            invitation.InvitedUserId,
            tokenHash,
            "second-password-hash");
        expiredReplay.Should().BeNull();

        var secondScenario = await SeedScenarioAsync(
            "wrong-email-resident@example.test",
            "unused-second@example.test");
        var secondGrant = new GrantTenantUserAccessCommand(
            secondScenario.PortfolioId,
            secondScenario.FirstRelationshipId,
            secondScenario.FirstPartyId,
            "Grant wrong email resident activation",
            "https://rentalcommand.test",
            secondScenario.ActorUserId,
            secondScenario.SessionId,
            secondScenario.AccessContextId,
            secondScenario.AccessRevision,
            "tenant-revoke-pending-invitation-revoked");
        var secondGrantResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{secondScenario.PortfolioId}:{secondScenario.FirstRelationshipId}:{secondScenario.FirstPartyId}:tenant-revoke-pending-invitation"),
            secondGrant,
            GrantCodec);
        var secondAccessId = secondGrantResult.Value.TenantUserAccessIds.Should().ContainSingle().Subject;

        var revoke = new RevokeTenantUserAccessCommand(
            secondScenario.PortfolioId,
            secondScenario.FirstRelationshipId,
            secondScenario.FirstPartyId,
            secondAccessId,
            "Wrong email; revoke pending activation",
            secondScenario.ActorUserId,
            secondScenario.SessionId,
            secondScenario.AccessContextId,
            secondScenario.AccessRevision,
            "tenant-revoke-pending-invitation");
        var revokeResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.revoke",
                $"{secondScenario.PortfolioId}:{secondScenario.FirstRelationshipId}:{secondAccessId}:tenant-revoke-pending-invitation"),
            revoke,
            RevokeCodec);

        revokeResult.Value.Outcome.Should().Be(LeasePartyMutationOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkspaceInvitations.AsNoTracking()
            .CountAsync(row =>
                row.InvitedUser!.NormalizedEmail == "WRONG-EMAIL-RESIDENT@EXAMPLE.TEST" &&
                row.AcceptedAtUtc == null &&
                row.RevokedAtUtc != null))
            .Should().Be(1);
        (await _context.Db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM "AtomicAuditLogs"
                WHERE "PortfolioId" = {secondScenario.PortfolioId}
                  AND "EntityType" = {nameof(WorkspaceInvitation)}
                  AND "Operation" = {(int)AuditLogOperation.Updated}
                  AND "ChangeReason" = {"Tenant portal invitation revoked"}
                  AND "NewValues"::text LIKE {"%TenantUserAccessId%"}
                """)
            .SingleAsync())
            .Should().Be(1);

        var reissueGrant = secondGrant with { DeliveryIdempotencyKey = "tenant-reissue-pending-invitation" };
        var reissueResult = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{secondScenario.PortfolioId}:{secondScenario.FirstRelationshipId}:{secondScenario.FirstPartyId}:tenant-reissue-pending-invitation"),
            reissueGrant,
            GrantCodec);
        reissueResult.Value.Outcome.Should().Be(LeasePartyMutationOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        var wrongEmailInvitationStates = await _context.Db.WorkspaceInvitations.AsNoTracking()
            .Where(row => row.InvitedUser!.NormalizedEmail == "WRONG-EMAIL-RESIDENT@EXAMPLE.TEST")
            .Select(row => new { row.AcceptedAtUtc, row.RevokedAtUtc })
            .ToListAsync();
        wrongEmailInvitationStates.Count(row => row.AcceptedAtUtc == null && row.RevokedAtUtc != null)
            .Should().Be(1);
        wrongEmailInvitationStates.Count(row => row.AcceptedAtUtc == null && row.RevokedAtUtc == null)
            .Should().Be(1);
    }

    [Fact]
    public async Task Grant_AllowsScheduledNonEndedParty_BeforeEffectiveDate()
    {
        var scenario = await SeedScenarioAsync();
        var businessDate = await BusinessDateAsync(scenario.PortfolioId);
        var scheduledStart = businessDate.AddDays(25);
        await _context.Db.LeaseManagementParties
            .Where(party => party.Id == scenario.FirstPartyId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(party => party.EffectiveFrom, scheduledStart)
                .SetProperty(party => party.EffectiveThrough, (DateOnly?)null));
        _context.Db.ChangeTracker.Clear();

        var command = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Invite resident before move-in",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "scheduled-party-grant");
        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:scheduled-party-grant"),
            command,
            GrantCodec);

        result.Value.Outcome.Should().Be(LeasePartyMutationOutcome.Applied);
        result.Value.TenantUserAccessIds.Should().ContainSingle();
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.AsNoTracking()
            .CountAsync(access => access.LeaseManagementPartyId == scenario.FirstPartyId
                && access.RevokedAtUtc == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task Grant_RejectsPartyEndedBeforeBusinessDate()
    {
        var scenario = await SeedScenarioAsync();
        var businessDate = await BusinessDateAsync(scenario.PortfolioId);
        await _context.Db.LeaseManagementParties
            .Where(party => party.Id == scenario.FirstPartyId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(party => party.EffectiveFrom, businessDate.AddDays(-30))
                .SetProperty(party => party.EffectiveThrough, businessDate.AddDays(-1)));
        _context.Db.ChangeTracker.Clear();

        var command = new GrantTenantUserAccessCommand(
            scenario.PortfolioId,
            scenario.FirstRelationshipId,
            scenario.FirstPartyId,
            "Do not invite ended resident",
            "https://rentalcommand.test",
            scenario.ActorUserId,
            scenario.SessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            "ended-party-grant");
        var result = await ExecuteAtomicAsync(
            new AtomicCommandIdentity(
                "lease-management.party.access.grant",
                $"{scenario.PortfolioId}:{scenario.FirstRelationshipId}:{scenario.FirstPartyId}:ended-party-grant"),
            command,
            GrantCodec);

        result.Value.Outcome.Should().Be(LeasePartyMutationOutcome.InvalidParty);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.TenantUserAccesses.AsNoTracking()
            .AnyAsync(access => access.LeaseManagementPartyId == scenario.FirstPartyId))
            .Should().BeFalse();
    }

    private async Task<AtomicCommandOutcome<LeasePartyMutationResult>> ExecuteAtomicAsync<TCommand>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<LeasePartyMutationResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, ILeasePartyAccessCommand
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IWriteExecutor>();
        var write = LeasePartyAccessWriteSupport.Write(
            command,
            (request, context, token) => ExecuteAsync(db, request, context, token),
            (request, context, token) => AuthorizeAsync(db, request, context, token));
        write.ResultContract.Should().Be(resultCodec.ContractName);
        write.OperationName.Should().Be(identity.CommandType);
        return await writes.ExecuteAsync(identity.IdempotencyKey, write, ct);
    }

    private static Task<LeasePartyMutationResult> ExecuteAsync<TCommand>(
        RentalCommandDbContext db, TCommand command, IAtomicCommandContext context, CancellationToken ct)
        where TCommand : notnull, ILeasePartyAccessCommand => command switch
        {
            GrantTenantUserAccessCommand value =>
                GrantTenantUserAccessHandler.ExecuteAsync(db, value, context, ct),
            RevokeTenantUserAccessCommand value =>
                RevokeTenantUserAccessHandler.ExecuteAsync(db, value, context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static Task AuthorizeAsync<TCommand>(
        RentalCommandDbContext db, TCommand command, IAtomicCommandContext context, CancellationToken ct)
        where TCommand : notnull, ILeasePartyAccessCommand => command switch
        {
            GrantTenantUserAccessCommand value =>
                GrantTenantUserAccessHandler.AuthorizeAsync(db, value, context, ct),
            RevokeTenantUserAccessCommand value =>
                RevokeTenantUserAccessHandler.AuthorizeAsync(db, value, context, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private Task<DateOnly> BusinessDateAsync(int portfolioId) =>
        _context.Db.Database
            .SqlQuery<DateOnly>($"SELECT rc_business_date({portfolioId}) AS \"Value\"")
            .SingleAsync();

    private async Task<WorkspaceInvitationActivationRow?> ActivateInvitationAsApiAsync(
        long invitationId,
        int invitedUserId,
        string tokenHash,
        string passwordHash)
    {
        _context.Db.ChangeTracker.Clear();
        await _context.Db.Database.OpenConnectionAsync();
        try
        {
            await _context.Db.Database.ExecuteSqlRawAsync("SET SESSION AUTHORIZATION rentalcommand_api;");
            return await _context.Db.Database.SqlQuery<WorkspaceInvitationActivationRow>($"""
                    SELECT * FROM rc_activate_workspace_invitation(
                        {invitationId}, {invitedUserId}, {tokenHash}, {passwordHash},
                        {"test-security-stamp"}, {"test-concurrency-stamp"})
                    """)
                .SingleOrDefaultAsync();
        }
        finally
        {
            await _context.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
            await _context.Db.Database.CloseConnectionAsync();
        }
    }

    private static string ExtractActivationToken(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var body = document.RootElement.GetProperty("body").GetString()
            ?? throw new InvalidOperationException("Activation email payload did not contain a text body.");
        const string marker = "/activate-team?token=";
        var markerIndex = body.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("Activation email payload did not contain an activate-team token.");
        }

        var tokenStart = markerIndex + marker.Length;
        var tokenEnd = body.IndexOfAny(['\r', '\n', ' ', '\t'], tokenStart);
        return Uri.UnescapeDataString(tokenEnd < 0 ? body[tokenStart..] : body[tokenStart..tokenEnd]);
    }

    private static string HashInvitationToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task<Scenario> SeedScenarioAsync(
        string firstResidentEmail = "first-resident@example.test",
        string secondResidentEmail = "second-resident@example.test")
    {
        var now = DateTime.UtcNow;
        var unique = Guid.NewGuid().ToString("N")[..12];
        var db = _context.Db;
        var actor = new ApplicationUser
        {
            UserName = $"household-manager-{unique}@example.test",
            NormalizedUserName = $"HOUSEHOLD-MANAGER-{unique.ToUpperInvariant()}@EXAMPLE.TEST",
            Email = $"household-manager-{unique}@example.test",
            NormalizedEmail = $"HOUSEHOLD-MANAGER-{unique.ToUpperInvariant()}@EXAMPLE.TEST",
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

        var firstRelationship = Relationship(property.Id, firstUnit.Id, actor.Id, $"{unique}-1", now);
        var secondRelationship = Relationship(property.Id, secondUnit.Id, actor.Id, $"{unique}-2", now);
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

    private sealed class WorkspaceInvitationActivationRow
    {
        public int PortfolioId { get; init; }
        public int WorkspaceMembershipId { get; init; }
        public int AccessContextId { get; init; }
        public int InvitedUserId { get; init; }
        public DateTime AcceptedAtUtc { get; init; }
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
