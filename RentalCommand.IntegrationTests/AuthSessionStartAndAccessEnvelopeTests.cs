using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auth;
using RentalCommand.Data.Authorization;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the destructive auth-session entry path and its database-produced shell
/// envelope. These tests intentionally exercise translated SQL, the real view, advisory locks,
/// atomic receipts, and transaction rollback rather than replacing any of those boundaries with
/// in-memory fakes.
/// </summary>
public sealed class AuthSessionStartAndAccessEnvelopeTests : IAsyncLifetime
{
    private const string ApiPassword = "auth-start-api-test-password";
    private static readonly AtomicJsonResultCodec<LoginContextSelectionChallengeResult> ChallengeCodec =
        new("login-context-selection-challenge-result.v1");
    private static readonly AtomicJsonResultCodec<StartAuthSessionResult> StartCodec =
        new("start-auth-session-result.v1");

    private readonly DateTime _now = new(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private ServiceProvider? _runtimeServices;
    private AuthStartFailureInterceptor? _failureInterceptor;
    private QueryCaptureInterceptor? _queryCapture;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;
    private int _userId;
    private int _otherUserId;
    private int _firstContextId;
    private int _secondContextId;
    private int _otherUserContextId;
    private int _firstMembershipId;
    private int _secondAssignmentId;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_auth_start")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using (var db = NewPlainContext())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER ROLE rentalcommand_api PASSWORD '{ApiPassword}';");
            await SeedAsync(db);
        }

        _failureInterceptor = new AuthStartFailureInterceptor();
        _queryCapture = new QueryCaptureInterceptor();
        _services = BuildServices(_connectionString);
        var runtimeConnection = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = "rentalcommand_api",
            Password = ApiPassword,
            Pooling = false,
        };
        _runtimeServices = BuildServices(runtimeConnection.ConnectionString);
    }

    private ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_failureInterceptor!);
        services.AddSingleton(_queryCapture!);
        services.AddScoped<ICurrentActor, AuthStartTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            IssueLoginContextSelectionChallengeCommand,
            LoginContextSelectionChallengeResult,
            IssueLoginContextSelectionChallengeHandler>();
        services.AddAtomicCommandHandler<
            StartAuthSessionCommand,
            StartAuthSessionResult,
            StartAuthSessionHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<AuthStartFailureInterceptor>(),
                    provider.GetRequiredService<QueryCaptureInterceptor>()));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
        if (_runtimeServices is not null)
        {
            await _runtimeServices.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task RuntimeApiRole_WithBlankScope_StartsAndReplaysPreAuthChallengeAndSession()
    {
        SkipIfNoDocker();
        var challengeOperation = Guid.NewGuid();
        var challenge = Challenge();

        AtomicCommandOutcome<LoginContextSelectionChallengeResult> issued;
        try
        {
            issued = await RuntimeAtomic.ExecuteAsync(
                SessionRefreshCommandIdentity.ForContextSelectionChallenge(challengeOperation),
                challenge,
                ChallengeCodec);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Runtime pre-auth challenge failed. Recent database commands:\n" +
                _queryCapture!.DescribeRecentCommands(),
                exception);
        }
        var issuedReplay = await RuntimeAtomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(challengeOperation),
            challenge,
            ChallengeCodec);

        issued.Value.Issued.Should().BeTrue();
        issued.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        issuedReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        var startOperation = Guid.NewGuid();
        var start = Start(challenge);
        var started = await RuntimeAtomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(startOperation),
            start,
            StartCodec);
        var startedReplay = await RuntimeAtomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(startOperation),
            start,
            StartCodec);

        started.Value.Started.Should().BeTrue();
        started.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        startedReplay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        await using var verify = NewPlainContext();
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == issued.AttemptId &&
            row.ChangeReason == "Login context selection challenge issued")).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.AttemptId == started.AttemptId &&
            row.ChangeReason == "Authentication session started")).Should().Be(1);
    }

    [SkippableFact]
    public async Task ChallengeAndSession_ReplayPersistExactlyOneAtomicGraph()
    {
        SkipIfNoDocker();
        var challengeOperation = Guid.NewGuid();
        var challenge = Challenge();

        var firstChallenge = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(challengeOperation),
            challenge,
            ChallengeCodec);
        var replayedChallenge = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(challengeOperation),
            challenge,
            ChallengeCodec);

        firstChallenge.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        firstChallenge.Value.Issued.Should().BeTrue();
        replayedChallenge.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayedChallenge.Value.Should().Be(firstChallenge.Value);

        var startOperation = Guid.NewGuid();
        var start = Start(challenge);
        var firstStart = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(startOperation),
            start,
            StartCodec);
        var replayedStart = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(startOperation),
            start,
            StartCodec);

        firstStart.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        firstStart.Value.Started.Should().BeTrue();
        replayedStart.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayedStart.Value.Should().Be(firstStart.Value);

        await using var db = NewPlainContext();
        (await db.LoginContextSelectionChallenges.CountAsync(item => item.Id == challenge.ChallengeId))
            .Should().Be(1);
        (await db.LoginContextSelectionChallenges.SingleAsync(item => item.Id == challenge.ChallengeId))
            .ConsumedAtUtc.Should().Be(_now);
        (await db.AuthSessions.CountAsync(item => item.Id == start.AuthSessionId)).Should().Be(1);
        (await db.AuthSessionRefreshTokenFamilies.CountAsync(item => item.Id == start.RefreshTokenFamilyId))
            .Should().Be(1);
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.Id == start.CredentialId))
            .Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(item =>
            item.ChangeReason == "Login context selection challenge issued")).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(item =>
            item.ChangeReason == "Authentication session started")).Should().Be(1);
    }

    [SkippableFact]
    public async Task StartFailure_RollsBackChallengeConsumptionSessionGraphAuditAndReceipt()
    {
        SkipIfNoDocker();
        var challenge = await IssueChallengeAsync();
        var start = Start(challenge);
        var operation = Guid.NewGuid();
        _failureInterceptor!.FailNextSessionStart = true;

        var act = async () => await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(operation),
            start,
            StartCodec);
        await act.Should().ThrowAsync<InjectedAuthStartFailureException>();

        await using var db = NewPlainContext();
        (await db.LoginContextSelectionChallenges.SingleAsync(item => item.Id == challenge.ChallengeId))
            .ConsumedAtUtc.Should().BeNull();
        (await db.AuthSessions.CountAsync(item => item.Id == start.AuthSessionId)).Should().Be(0);
        (await db.AuthSessionRefreshTokenFamilies.CountAsync(item => item.Id == start.RefreshTokenFamilyId))
            .Should().Be(0);
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.Id == start.CredentialId))
            .Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(item =>
            item.ChangeReason == "Authentication session started")).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(item =>
            item.CommandType == "auth-session:start" &&
            item.IdempotencyKey == $"operation:{operation:N}")).Should().Be(0);
    }

    [SkippableFact]
    public async Task ConcurrentStarts_ConsumeChallengeOnceAndCreateOneSessionGraph()
    {
        SkipIfNoDocker();
        var challenge = await IssueChallengeAsync();
        var first = Start(challenge);
        var second = Start(challenge) with
        {
            AuthSessionId = Guid.NewGuid(),
            RefreshTokenFamilyId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
            CredentialTokenHash = Hash(Guid.NewGuid().ToString("N")),
        };

        var outcomes = await Task.WhenAll(
            Atomic.ExecuteAsync(SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()), first, StartCodec),
            Atomic.ExecuteAsync(SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()), second, StartCodec));

        outcomes.Count(item => item.Value.Started).Should().Be(1);
        outcomes.Count(item => !item.Value.Started).Should().Be(1);
        await using var db = NewPlainContext();
        (await db.AuthSessions.CountAsync(item =>
            item.Id == first.AuthSessionId || item.Id == second.AuthSessionId)).Should().Be(1);
        (await db.AuthSessionRefreshTokenFamilies.CountAsync(item =>
            item.Id == first.RefreshTokenFamilyId || item.Id == second.RefreshTokenFamilyId)).Should().Be(1);
        (await db.AuthSessionRefreshCredentials.CountAsync(item =>
            item.Id == first.CredentialId || item.Id == second.CredentialId)).Should().Be(1);
        (await db.LoginContextSelectionChallenges.SingleAsync(item => item.Id == challenge.ChallengeId))
            .ConsumedAtUtc.Should().Be(_now);
    }

    [SkippableFact]
    public async Task ExpiredOrStaleChallenge_IsRejectedWithoutConsumptionOrSessionRows()
    {
        SkipIfNoDocker();
        var expired = await IssueChallengeAsync(expiresAtUtc: _now.AddMinutes(1));
        var expiredStart = Start(expired) with { IssuedAtUtc = _now.AddMinutes(1) };

        var expiredOutcome = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()),
            expiredStart,
            StartCodec);
        expiredOutcome.Value.Started.Should().BeFalse();

        var stale = await IssueChallengeAsync();
        await using (var revokeDb = NewPlainContext())
        {
            var secondAssignment = await revokeDb.MembershipRoleAssignments
                .SingleAsync(item => item.Id == _secondAssignmentId);
            secondAssignment.Status = MembershipRoleAssignmentStatus.Revoked;
            secondAssignment.RevokedAtUtc = _now.AddSeconds(1);
            secondAssignment.UpdatedAtUtc = _now.AddSeconds(1);
            await revokeDb.SaveChangesAsync();
        }

        var staleOutcome = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()),
            Start(stale) with { IssuedAtUtc = _now.AddSeconds(2) },
            StartCodec);
        staleOutcome.Value.Started.Should().BeFalse(
            "a challenge issued for multiple choices must not silently become a single-context login");
        var noLongerNeeded = Challenge() with { IssuedAtUtc = _now.AddSeconds(2) };
        var issueOutcome = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(Guid.NewGuid()),
            noLongerNeeded,
            ChallengeCodec);
        issueOutcome.Value.Issued.Should().BeFalse(
            "the effective-context count is recomputed in PostgreSQL when the command executes");

        await using var db = NewPlainContext();
        (await db.LoginContextSelectionChallenges
            .Where(item => item.Id == expired.ChallengeId || item.Id == stale.ChallengeId)
            .AllAsync(item => item.ConsumedAtUtc == null)).Should().BeTrue();
        (await db.LoginContextSelectionChallenges.CountAsync(item => item.Id == noLongerNeeded.ChallengeId))
            .Should().Be(0);
        (await db.AuthSessions.CountAsync(item =>
            item.Id == expiredStart.AuthSessionId || item.Id == staleOutcome.Value.AuthSessionId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AccessRevokedAfterChallenge_IsRejectedWithoutConsumingChallenge()
    {
        SkipIfNoDocker();
        var challenge = await IssueChallengeAsync();
        await using (var revokeDb = NewPlainContext())
        {
            var context = await revokeDb.WorkspaceAccessContexts.SingleAsync(item => item.Id == _firstContextId);
            context.Status = WorkspaceAccessContextStatus.Revoked;
            context.RevokedAtUtc = _now.AddSeconds(1);
            context.UpdatedAtUtc = _now.AddSeconds(1);
            await revokeDb.SaveChangesAsync();
        }

        var start = Start(challenge) with { IssuedAtUtc = _now.AddSeconds(2) };
        var outcome = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForStart(Guid.NewGuid()),
            start,
            StartCodec);

        outcome.Value.Started.Should().BeFalse();
        await using var db = NewPlainContext();
        (await db.LoginContextSelectionChallenges.SingleAsync(item => item.Id == challenge.ChallengeId))
            .ConsumedAtUtc.Should().BeNull();
        (await db.AuthSessions.CountAsync(item => item.Id == start.AuthSessionId)).Should().Be(0);
    }

    [SkippableFact]
    public async Task ChallengeIssue_RollsBackRowAuditAndReceiptTogether()
    {
        SkipIfNoDocker();
        var operation = Guid.NewGuid();
        var challenge = Challenge();
        _failureInterceptor!.FailNextChallengeIssue = true;

        var act = async () => await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(operation),
            challenge,
            ChallengeCodec);
        await act.Should().ThrowAsync<InjectedAuthStartFailureException>();

        await using var db = NewPlainContext();
        (await db.LoginContextSelectionChallenges.CountAsync(item => item.Id == challenge.ChallengeId))
            .Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(item =>
            item.ChangeReason == "Login context selection challenge issued")).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(item =>
            item.CommandType == "auth-context-selection:issue" &&
            item.IdempotencyKey == $"operation:{operation:N}")).Should().Be(0);
    }

    [SkippableFact]
    public async Task EffectiveContextSelection_IsOneDatabaseQueryWithConsistentFallbackAndCount()
    {
        SkipIfNoDocker();
        await using (var mutateDb = NewPlainContext())
        {
            var firstMembership = await mutateDb.WorkspaceMemberships
                .SingleAsync(item => item.Id == _firstMembershipId);
            firstMembership.DefaultExperience = WorkspaceExperience.Maintenance;
            await mutateDb.SaveChangesAsync();
        }

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        _queryCapture!.Reset();
        var options = await new EffectiveAccessContextSelectionQuery(db).ListAsync(_userId, _now);

        options.Should().HaveCount(2);
        options.Should().OnlyContain(item => item.TotalEffectiveContexts == 2);
        options.Single(item => item.AccessContextId == _firstContextId)
            .DefaultExperience.Should().Be(WorkspaceExperience.Management,
                "the membership default is unavailable and SQL must choose the first effective experience");
        _queryCapture.ReaderCommands.Should().HaveCount(1);
        _queryCapture.ReaderCommands[0].ToUpperInvariant().Should().Contain("COUNT(*)");
        _queryCapture.ReaderCommands[0].Should().Contain("MembershipRoleAssignments");
        _queryCapture.ReaderCommands[0].Should().Contain("ORDER BY");
    }

    [SkippableFact]
    public async Task AccessEnvelopeView_FiltersIneffectiveAssignmentsAndIsolatesUsersAndWorkspaces()
    {
        SkipIfNoDocker();
        await using var db = NewPlainContext();
        var query = new AccessEnvelopeQuery(db);

        var first = await query.GetAsync(_userId, _firstContextId);
        first.Should().NotBeNull();
        first!.Identity.UserId.Should().Be(_userId);
        first.SelectedContext.AccessContextId.Should().Be(_firstContextId);
        first.Assignments.Should().ContainSingle();

        (await query.GetAsync(_otherUserId, _firstContextId)).Should().BeNull();
        (await query.GetAsync(_userId, _otherUserContextId)).Should().BeNull();

        var assignment = await db.MembershipRoleAssignments.SingleAsync(item => item.Id == _secondAssignmentId);
        assignment.Status = MembershipRoleAssignmentStatus.Suspended;
        assignment.SuspendedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        assignment.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await query.GetAsync(_userId, _secondContextId)).Should().BeNull(
            "a context without any effective assignment must not survive the view's inner joins");
    }

    [SkippableFact]
    public async Task PrimarySelfOwnerRelationship_DoesNotCreateOwnerWorkAreaForWorkspaceAdministrator()
    {
        SkipIfNoDocker();
        var now = DateTime.UtcNow;
        await using var db = NewPlainContext();
        var context = await db.WorkspaceAccessContexts
            .SingleAsync(item => item.Id == _firstContextId);
        context.LastAuthorizedExperience = WorkspaceExperience.Owner;
        var owner = new OwnerEntity
        {
            PortfolioId = context.PortfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Primary Self Owner",
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OwnerEntities.Add(owner);
        await db.SaveChangesAsync();
        db.OwnerUserAccesses.Add(new OwnerUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = context.PortfolioId,
            AccessContextId = context.Id,
            ApplicationUserId = _userId,
            OwnerEntityId = owner.Id,
            EffectiveFromUtc = now.AddMinutes(-1),
            GrantedAtUtc = now,
            GrantedByUserId = _userId,
            Reason = "Primary self-owner relationship proof",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var query = new AccessEnvelopeQuery(db);
        var envelope = await query.GetAsync(_userId, _firstContextId);

        envelope.Should().NotBeNull();
        envelope!.DefaultExperience.Should().Be(WorkspaceExperience.Management);
        envelope.SelectedContext.ActiveExperience.Should().Be(WorkspaceExperience.Management,
            "the unavailable remembered Owner work area must fall back to the administrator default");
        envelope.AvailableExperiences.Should().Equal(WorkspaceExperience.Management);
        envelope.Navigation.Should().ContainSingle(item =>
            item.Experience == WorkspaceExperience.Management && item.CapabilityKeys.Count > 0);
        (await db.OwnerUserAccesses.SingleAsync(item =>
            item.AccessContextId == _firstContextId && item.OwnerEntityId == owner.Id))
            .RevokedAtUtc.Should().BeNull("experience suppression must not remove ownership access");

        owner = await db.OwnerEntities.SingleAsync(item => item.Id == owner.Id);
        owner.IsPrimary = false;
        owner.UpdatedAt = now.AddMinutes(1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var expanded = await query.GetAsync(_userId, _firstContextId);
        expanded!.AvailableExperiences.Should().Equal(
            WorkspaceExperience.Management,
            WorkspaceExperience.Owner);
        expanded.SelectedContext.ActiveExperience.Should().Be(WorkspaceExperience.Owner,
            "a separate non-primary owner relationship remains an explicit selectable work area");
    }

    [SkippableFact]
    public async Task AccessEnvelopeView_IsCreatedAsSecurityInvoker()
    {
        SkipIfNoDocker();
        await using var db = NewPlainContext();
        var options = await db.Database.SqlQueryRaw<string>(
                "SELECT option AS \"Value\" FROM pg_class c " +
                "CROSS JOIN LATERAL unnest(c.reloptions) option " +
                "WHERE c.oid = '\"vw_access_envelopes\"'::regclass")
            .ToListAsync();

        options.Should().Contain("security_invoker=true");
    }

    [SkippableFact]
    public async Task RelationshipOnlyContexts_AreSelectableAndExposeNoTeamCapabilities()
    {
        SkipIfNoDocker();
        var now = DateTime.UtcNow;
        await using var db = NewPlainContext();
        var portfolio = Portfolio("Relationship Workspace");
        var ownerUser = User("owner-relationship@example.test", "Owner Relationship");
        var tenantUser = User("tenant-relationship@example.test", "Tenant Relationship");
        db.AddRange(portfolio, ownerUser, tenantUser);
        await db.SaveChangesAsync();

        var ownerContext = RelationshipContext(ownerUser.Id, portfolio.Id, WorkspaceExperience.Owner, now);
        var tenantContext = RelationshipContext(tenantUser.Id, portfolio.Id, WorkspaceExperience.Tenant, now);
        var owner = new OwnerEntity
        {
            PortfolioId = portfolio.Id,
            OwnerEntityType = OwnerEntityType.Person,
            Name = "Relationship Owner",
            IsPrimary = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = portfolio.Id,
            OwnerEntity = owner,
            Name = "Relationship Property",
            AddressLine1 = "1 Context Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = portfolio.Id,
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = portfolio.Id,
            FirstName = "Relationship",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(ownerContext, tenantContext, owner, property, unit, tenant);
        await db.SaveChangesAsync();

        var relationship = new LeaseManagement
        {
            PortfolioId = portfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "LM-RELATIONSHIP",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = ownerUser.Id,
        };
        db.Add(relationship);
        await db.SaveChangesAsync();
        var party = new LeaseManagementParty
        {
            PortfolioId = portfolio.Id,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "relationship access proof",
            CreatedAtUtc = now,
            CreatedByUserId = ownerUser.Id,
        };
        db.Add(party);
        await db.SaveChangesAsync();
        var tenantAccess = new TenantUserAccess
        {
            PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
            AccessContextId = tenantContext.Id, ApplicationUserId = tenantUser.Id,
            LeaseManagementPartyId = party.Id, GrantedAtUtc = now,
            GrantedByUserId = ownerUser.Id, Reason = "proof",
        };
        db.AddRange(
            new OwnerUserAccess
            {
                PublicId = Guid.NewGuid(), PortfolioId = portfolio.Id,
                AccessContextId = ownerContext.Id, ApplicationUserId = ownerUser.Id,
                OwnerEntityId = owner.Id, EffectiveFromUtc = now.AddMinutes(-1),
                GrantedAtUtc = now, GrantedByUserId = ownerUser.Id, Reason = "proof",
            },
            tenantAccess);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var ownerOptions = await new EffectiveAccessContextSelectionQuery(db)
            .ListAsync(ownerUser.Id, now);
        var tenantOptions = await new EffectiveAccessContextSelectionQuery(db)
            .ListAsync(tenantUser.Id, now);
        ownerOptions.Should().ContainSingle(item => item.DefaultExperience == WorkspaceExperience.Owner);
        tenantOptions.Should().ContainSingle(item => item.DefaultExperience == WorkspaceExperience.Tenant);

        var envelopeQuery = new AccessEnvelopeQuery(db);
        var ownerEnvelope = await envelopeQuery.GetAsync(ownerUser.Id, ownerContext.Id);
        var tenantEnvelope = await envelopeQuery.GetAsync(tenantUser.Id, tenantContext.Id);
        ownerEnvelope!.Assignments.Should().BeEmpty();
        tenantEnvelope!.Assignments.Should().BeEmpty();
        ownerEnvelope.Navigation.Should().OnlyContain(item => item.CapabilityKeys.Count == 0);
        tenantEnvelope.Navigation.Should().OnlyContain(item => item.CapabilityKeys.Count == 0);
        (await db.EffectiveOwnerAccess.SingleAsync(item => item.AccessContextId == ownerContext.Id))
            .PropertyId.Should().Be(property.Id);
        (await db.EffectiveTenantAccess.SingleAsync(item => item.AccessContextId == tenantContext.Id))
            .LeaseManagementId.Should().Be(relationship.Id);

        tenantAccess = await db.TenantUserAccesses.SingleAsync(item => item.Id == tenantAccess.Id);
        tenantContext = await db.WorkspaceAccessContexts.SingleAsync(item => item.Id == tenantContext.Id);
        tenantAccess.RevokedAtUtc = now.AddMinutes(1);
        tenantAccess.RevokedByUserId = ownerUser.Id;
        tenantContext.AdvanceRevision(1);
        tenantContext.UpdatedAtUtc = now.AddMinutes(1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await new EffectiveAccessContextSelectionQuery(db).ListAsync(tenantUser.Id, now.AddMinutes(1)))
            .Should().BeEmpty("revoking the relationship removes the relationship-only login context");
        (await envelopeQuery.GetAsync(tenantUser.Id, tenantContext.Id)).Should().BeNull();
        (await db.EffectiveTenantAccess.AnyAsync(item => item.AccessContextId == tenantContext.Id))
            .Should().BeFalse();
    }

    private async Task<IssueLoginContextSelectionChallengeCommand> IssueChallengeAsync(
        DateTime? expiresAtUtc = null)
    {
        var command = Challenge(expiresAtUtc);
        var outcome = await Atomic.ExecuteAsync(
            SessionRefreshCommandIdentity.ForContextSelectionChallenge(Guid.NewGuid()),
            command,
            ChallengeCodec);
        outcome.Value.Issued.Should().BeTrue();
        return command;
    }

    private IssueLoginContextSelectionChallengeCommand Challenge(DateTime? expiresAtUtc = null) =>
        new(
            _userId,
            Guid.NewGuid(),
            Hash(Guid.NewGuid().ToString("N")),
            _now,
            expiresAtUtc ?? _now.AddMinutes(10));

    private StartAuthSessionCommand Start(IssueLoginContextSelectionChallengeCommand challenge) =>
        new(
            _userId,
            _firstContextId,
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Hash(Guid.NewGuid().ToString("N")),
            _now,
            _now.AddDays(30),
            _now.AddDays(7),
            _now.AddDays(30),
            challenge.ChallengeId,
            challenge.ChallengeTokenHash);

    private async Task SeedAsync(RentalCommandDbContext db)
    {
        var user = User("auth-start@example.test", "Auth Start");
        var otherUser = User("other-auth@example.test", "Other Auth");
        var firstPortfolio = Portfolio("Alpha Workspace");
        var secondPortfolio = Portfolio("Beta Workspace");
        var otherPortfolio = Portfolio("Other Workspace");
        db.AddRange(user, otherUser, firstPortfolio, secondPortfolio, otherPortfolio);
        await db.SaveChangesAsync();

        var first = CreateAccessRoot(user.Id, firstPortfolio.Id, WorkspaceExperience.Management);
        var second = CreateAccessRoot(user.Id, secondPortfolio.Id, WorkspaceExperience.Leasing);
        var other = CreateAccessRoot(otherUser.Id, otherPortfolio.Id, WorkspaceExperience.Management);
        db.WorkspaceAccessContexts.AddRange(first.Context, second.Context, other.Context);
        await db.SaveChangesAsync();

        first.Membership.AccessContextId = first.Context.Id;
        second.Membership.AccessContextId = second.Context.Id;
        other.Membership.AccessContextId = other.Context.Id;
        db.WorkspaceMemberships.AddRange(first.Membership, second.Membership, other.Membership);
        await db.SaveChangesAsync();

        first.Assignment.WorkspaceMembershipId = first.Membership.Id;
        second.Assignment.WorkspaceMembershipId = second.Membership.Id;
        other.Assignment.WorkspaceMembershipId = other.Membership.Id;
        db.MembershipRoleAssignments.AddRange(first.Assignment, second.Assignment, other.Assignment);
        await db.SaveChangesAsync();

        _userId = user.Id;
        _otherUserId = otherUser.Id;
        _firstContextId = first.Context.Id;
        _secondContextId = second.Context.Id;
        _otherUserContextId = other.Context.Id;
        _firstMembershipId = first.Membership.Id;
        _secondAssignmentId = second.Assignment.Id;
    }

    private AccessRoot CreateAccessRoot(int userId, int portfolioId, WorkspaceExperience experience)
    {
        var context = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = _now.AddDays(-2),
            UpdatedAtUtc = _now.AddDays(-2),
        };
        var membership = new WorkspaceMembership
        {
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = experience,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now.AddDays(-1),
            UpdatedAtUtc = _now.AddDays(-1),
        };
        var assignment = new MembershipRoleAssignment
        {
            PortfolioId = portfolioId,
            RoleProfileId = experience == WorkspaceExperience.Leasing ? 3 : 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = experience == WorkspaceExperience.Leasing
                ? MembershipRoleAssignmentScopeKind.SelectedProperties
                : MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now.AddDays(-1),
            UpdatedAtUtc = _now.AddDays(-1),
        };
        return new AccessRoot(context, membership, assignment);
    }

    private static WorkspaceAccessContext RelationshipContext(
        int userId,
        int portfolioId,
        WorkspaceExperience experience,
        DateTime now) => new()
    {
        UserId = userId,
        PortfolioId = portfolioId,
        Status = WorkspaceAccessContextStatus.Active,
        LastAuthorizedExperience = experience,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    private ApplicationUser User(string email, string displayName) => new()
    {
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        DisplayName = displayName,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = _now.AddDays(-2),
    };

    private Portfolio Portfolio(string name) => new()
    {
        Name = name,
        ManagementCompanyName = name,
        CreatedAt = _now.AddDays(-2),
        UpdatedAt = _now.AddDays(-2),
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private RentalCommandDbContext NewPlainContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Auth start services are unavailable.");

    private IAtomicUnitOfWork Atomic => Services.GetRequiredService<IAtomicUnitOfWork>();

    private IAtomicUnitOfWork RuntimeAtomic =>
        (_runtimeServices ?? throw new InvalidOperationException("Runtime auth-start services are unavailable."))
        .GetRequiredService<IAtomicUnitOfWork>();

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; auth start PostgreSQL proof skipped.");

    private sealed record AccessRoot(
        WorkspaceAccessContext Context,
        WorkspaceMembership Membership,
        MembershipRoleAssignment Assignment);

    private sealed class AuthStartTestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:auth-start";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class AuthStartFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailNextChallengeIssue { get; set; }
        public bool FailNextSessionStart { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var context = eventData.Context;
            if (FailNextChallengeIssue &&
                context?.ChangeTracker.Entries<LoginContextSelectionChallenge>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                FailNextChallengeIssue = false;
                throw new InjectedAuthStartFailureException();
            }

            if (FailNextSessionStart &&
                context?.ChangeTracker.Entries<AuthSession>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                FailNextSessionStart = false;
                throw new InjectedAuthStartFailureException();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class QueryCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];

        private List<string> ReaderCommandDetails { get; } = [];

        public void Reset() => ReaderCommands.Clear();

        public string DescribeRecentCommands() => string.Join(
            "\n\n",
            ReaderCommandDetails.TakeLast(6));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            ReaderCommandDetails.Add(
                command.CommandText + "\n" +
                string.Join(
                    "\n",
                    command.Parameters.Cast<DbParameter>().Select(parameter =>
                        $"{parameter.ParameterName}={parameter.Value}")));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedAuthStartFailureException : Exception { }
}
