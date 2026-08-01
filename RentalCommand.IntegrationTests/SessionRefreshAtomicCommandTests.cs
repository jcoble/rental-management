using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auth;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof for the future AuthSession refresh-token kernel. The live legacy JWT service is
/// intentionally not dual-written; the later destructive auth cutover will become this kernel's caller.
/// </summary>
public sealed class SessionRefreshAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<SessionRefreshMutationResult> Codec =
        new("session-refresh-mutation-result.v1");

    private readonly DateTime _now = new(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc);
    private readonly Guid _sessionId = Guid.NewGuid();
    private int _accessContextId;
    private int _membershipId;
    private int _assignmentId;
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private ReuseFailureInterceptor? _failureInterceptor;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_session_refresh")
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
            await SeedSessionAsync(db);
        }

        _failureInterceptor = new ReuseFailureInterceptor();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, RefreshTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            IssueSessionRefreshCredentialCommand,
            SessionRefreshMutationResult,
            IssueSessionRefreshCredentialHandler>();
        services.AddAtomicCommandHandler<
            RotateSessionRefreshCredentialCommand,
            SessionRefreshMutationResult,
            RotateSessionRefreshCredentialHandler>();
        services.AddSingleton(_failureInterceptor);
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ReuseFailureInterceptor>()));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Issue_ReplayCreatesOneFamilyAndOneCredential()
    {
        SkipIfNoDocker();
        var operationId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var command = Issue(familyId, credentialId, Hash("first"));

        var first = await ExecuteAtomicAsync(Identity("issue", operationId), command, Codec);
        var replay = await ExecuteAtomicAsync(Identity("issue", operationId), command, Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        replay.Value.CredentialId.Should().Be(credentialId);
        await using var db = NewPlainContext();
        (await db.AuthSessionRefreshTokenFamilies.CountAsync(item => item.Id == familyId)).Should().Be(1);
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.Id == credentialId)).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(item => item.ChangeReason == "Refresh credential family issued"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task ConcurrentReplayWithDifferentOperations_MintsOneSuccessorThenRevokesFamily()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        var firstReplacement = Guid.NewGuid();
        var secondReplacement = Guid.NewGuid();
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        var first = Rotate(firstOperation, issued.TokenHash, firstReplacement, Hash("replacement-a"));
        var second = Rotate(secondOperation, issued.TokenHash, secondReplacement, Hash("replacement-b"));

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(Identity("rotate", firstOperation), first, Codec),
            ExecuteAtomicAsync(Identity("rotate", secondOperation), second, Codec));

        outcomes.Select(item => item.Value.Status).Should().BeEquivalentTo(
            [SessionRefreshMutationStatus.Rotated, SessionRefreshMutationStatus.ReuseDetected]);

        await using var db = NewPlainContext();
        var credentials = await db.AuthSessionRefreshCredentials
            .Where(item => item.RefreshTokenFamilyId == issued.FamilyId)
            .OrderBy(item => item.IssuedAtUtc)
            .ToListAsync();
        credentials.Should().HaveCount(2);
        credentials.Count(item => item.Id == firstReplacement || item.Id == secondReplacement)
            .Should().Be(1);
        credentials.Single(item => item.Id == issued.CredentialId).ReuseDetectedAtUtc.Should().NotBeNull();

        var family = await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == issued.FamilyId);
        family.ReuseDetectedAtUtc.Should().NotBeNull();
        family.RevokedAtUtc.Should().NotBeNull();
        var session = await db.AuthSessions.SingleAsync(item => item.Id == _sessionId);
        session.Status.Should().Be(AuthSessionStatus.Revoked);
        session.RevokedAtUtc.Should().NotBeNull();
        (await db.AtomicAuditLogs.CountAsync(item => item.ChangeReason == "Refresh credential reuse detected"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task RotationRetryWithSameOperation_ReplaysTheConfirmedSuccessorWithoutReuse()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        var operation = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        var replacementHash = Hash("stable-replacement");
        var command = Rotate(operation, issued.TokenHash, replacementId, replacementHash);

        var first = await ExecuteAtomicAsync(Identity("rotate", operation), command, Codec);
        var retry = await ExecuteAtomicAsync(Identity("rotate", operation), command, Codec);

        first.Value.Status.Should().Be(SessionRefreshMutationStatus.Rotated);
        retry.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        retry.Value.Should().Be(first.Value);
        retry.Value.ReplacementCredentialId.Should().Be(replacementId);

        await using var db = NewPlainContext();
        (await db.AuthSessionRefreshCredentials.CountAsync(item =>
            item.RefreshTokenFamilyId == issued.FamilyId)).Should().Be(2);
        (await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == issued.FamilyId))
            .ReuseDetectedAtUtc.Should().BeNull();
        (await db.AuthSessions.SingleAsync(item => item.Id == _sessionId))
            .Status.Should().Be(AuthSessionStatus.Active);
    }

    [SkippableFact]
    public async Task Rotation_CapsSuccessorExpiryAtFamilyAbsoluteExpiry()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        var operation = Guid.NewGuid();
        var replacementId = Guid.NewGuid();

        var outcome = await ExecuteAtomicAsync(
            Identity("rotate", operation),
            Rotate(
                operation,
                issued.TokenHash,
                replacementId,
                Hash("family-bounded-replacement"),
                _now.AddDays(40)),
            Codec);

        outcome.Value.Status.Should().Be(SessionRefreshMutationStatus.Rotated);
        await using var db = NewPlainContext();
        var replacement = await db.AuthSessionRefreshCredentials
            .SingleAsync(item => item.Id == replacementId);
        replacement.ExpiresAtUtc.Should().Be(_now.AddDays(30));
    }

    [SkippableFact]
    public async Task ReuseFailure_RollsBackCredentialFamilySessionAuditAndReceiptTogether()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        var initialRotationOperation = Guid.NewGuid();
        await ExecuteAtomicAsync(
            Identity("rotate", initialRotationOperation),
            Rotate(initialRotationOperation, issued.TokenHash, Guid.NewGuid(), Hash("replacement")),
            Codec);
        var reuseOperation = Guid.NewGuid();
        _failureInterceptor!.FailNextReuse = true;

        var act = async () => await ExecuteAtomicAsync(
            Identity("rotate", reuseOperation),
            Rotate(reuseOperation, issued.TokenHash, Guid.NewGuid(), Hash("discarded")),
            Codec);
        await act.Should().ThrowAsync<InjectedReuseFailureException>();

        await using var db = NewPlainContext();
        var original = await db.AuthSessionRefreshCredentials.SingleAsync(item => item.Id == issued.CredentialId);
        original.ReuseDetectedAtUtc.Should().BeNull();
        original.RevokedAtUtc.Should().BeNull();
        var family = await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == issued.FamilyId);
        family.ReuseDetectedAtUtc.Should().BeNull();
        family.RevokedAtUtc.Should().BeNull();
        var session = await db.AuthSessions.SingleAsync(item => item.Id == _sessionId);
        session.Status.Should().Be(AuthSessionStatus.Active);
        session.RevokedAtUtc.Should().BeNull();
        (await db.AtomicAuditLogs.CountAsync(item => item.ChangeReason == "Refresh credential reuse detected"))
            .Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(item =>
            item.CommandType == "session-refresh:rotate" &&
            item.IdempotencyKey == SafeOperationKey(reuseOperation))).Should().Be(0);
    }

    [SkippableFact]
    public async Task RotationConstraintFailure_RollsBackConsumptionAndReceipt()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        var operation = Guid.NewGuid();

        var act = async () => await ExecuteAtomicAsync(
            Identity("rotate", operation),
            Rotate(operation, issued.TokenHash, Guid.NewGuid(), issued.TokenHash),
            Codec);
        await act.Should().ThrowAsync<DbUpdateException>();

        await using var db = NewPlainContext();
        var original = await db.AuthSessionRefreshCredentials.SingleAsync(item => item.Id == issued.CredentialId);
        original.ConsumedAtUtc.Should().BeNull();
        original.ReplacedByCredentialId.Should().BeNull();
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.RefreshTokenFamilyId == issued.FamilyId))
            .Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(item =>
            item.CommandType == "session-refresh:rotate" &&
            item.IdempotencyKey == SafeOperationKey(operation))).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(AccessLossKind.ContextSuspended)]
    [InlineData(AccessLossKind.MembershipRevoked)]
    [InlineData(AccessLossKind.LastAssignmentRemoved)]
    public async Task Issue_RejectsWhenEffectiveWorkspaceAccessIsLost(AccessLossKind loss)
    {
        SkipIfNoDocker();
        await RemoveEffectiveAccessAsync(loss, _now.AddSeconds(30));
        var familyId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        var outcome = await ExecuteAtomicAsync(
            Identity("issue", Guid.NewGuid()),
            Issue(familyId, credentialId, Hash($"rejected-{loss}")),
            Codec);

        outcome.Value.Status.Should().Be(SessionRefreshMutationStatus.Rejected);
        await using var db = NewPlainContext();
        (await db.AuthSessionRefreshTokenFamilies.CountAsync(item => item.Id == familyId)).Should().Be(0);
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.Id == credentialId)).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(AccessLossKind.ContextSuspended)]
    [InlineData(AccessLossKind.MembershipRevoked)]
    [InlineData(AccessLossKind.LastAssignmentRemoved)]
    public async Task Rotation_RejectsWithoutConsumingCredentialWhenEffectiveWorkspaceAccessIsLost(
        AccessLossKind loss)
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        await RemoveEffectiveAccessAsync(loss, _now.AddSeconds(30));
        var replacementId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var outcome = await ExecuteAtomicAsync(
            Identity("rotate", operationId),
            Rotate(operationId, issued.TokenHash, replacementId, Hash($"rejected-rotation-{loss}")),
            Codec);

        outcome.Value.Status.Should().Be(SessionRefreshMutationStatus.Rejected);
        await using var db = NewPlainContext();
        var original = await db.AuthSessionRefreshCredentials.SingleAsync(item => item.Id == issued.CredentialId);
        original.ConsumedAtUtc.Should().BeNull();
        original.ReplacedByCredentialId.Should().BeNull();
        (await db.AuthSessionRefreshCredentials.CountAsync(item => item.Id == replacementId)).Should().Be(0);
    }

    private async Task<IssuedCredential> IssueCredentialAsync()
    {
        var familyId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var hash = Hash(Guid.NewGuid().ToString("N"));
        var result = await ExecuteAtomicAsync(
            Identity("issue", Guid.NewGuid()),
            Issue(familyId, credentialId, hash),
            Codec);
        result.Value.Status.Should().Be(SessionRefreshMutationStatus.Issued);
        return new IssuedCredential(familyId, credentialId, hash);
    }

    private IssueSessionRefreshCredentialCommand Issue(Guid familyId, Guid credentialId, string hash) =>
        new(
            _sessionId,
            familyId,
            credentialId,
            hash,
            _now,
            _now.AddDays(7),
            _now.AddDays(30));

    private RotateSessionRefreshCredentialCommand Rotate(
        Guid operationId,
        string presentedHash,
        Guid replacementId,
        string replacementHash,
        DateTime? replacementExpiresAtUtc = null) =>
        new(
            operationId,
            presentedHash,
            replacementId,
            replacementHash,
            _now.AddMinutes(1),
            replacementExpiresAtUtc ?? _now.AddDays(7));

    private static AtomicCommandIdentity Identity(string operation, Guid operationId) =>
        operation switch
        {
            "issue" => SessionRefreshCommandIdentity.ForIssue(operationId),
            "rotate" => SessionRefreshCommandIdentity.ForRotation(operationId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    // The receipt identity is a caller-generated operation UUID. Neither the raw bearer credential
    // nor a reversible representation of it is persisted as an idempotency key.
    private static string SafeOperationKey(Guid operationId) => $"operation:{operationId:N}";

    private static string Hash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    private async Task SeedSessionAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            UserName = "refresh@example.test",
            NormalizedUserName = "REFRESH@EXAMPLE.TEST",
            Email = "refresh@example.test",
            NormalizedEmail = "REFRESH@EXAMPLE.TEST",
            DisplayName = "Refresh Test",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var portfolio = new Portfolio
        {
            Name = "Refresh Workspace",
            ManagementCompanyName = "Refresh Workspace",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();
        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.WorkspaceAccessContexts.Add(context);
        await db.SaveChangesAsync();
        var membership = new WorkspaceMembership
        {
            AccessContextId = context.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.MembershipRoleAssignments.Add(assignment);
        await db.SaveChangesAsync();
        db.AuthSessions.Add(new AuthSession
        {
            Id = _sessionId,
            UserId = user.Id,
            ActiveAccessContextId = context.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now,
            LastSeenAtUtc = _now,
            ExpiresAtUtc = _now.AddDays(30),
        });
        await db.SaveChangesAsync();
        _accessContextId = context.Id;
        _membershipId = membership.Id;
        _assignmentId = assignment.Id;
    }

    private async Task RemoveEffectiveAccessAsync(AccessLossKind loss, DateTime changedAtUtc)
    {
        await using var db = NewPlainContext();
        switch (loss)
        {
            case AccessLossKind.ContextSuspended:
            {
                var context = await db.WorkspaceAccessContexts.SingleAsync(item => item.Id == _accessContextId);
                context.Status = WorkspaceAccessContextStatus.Suspended;
                context.SuspendedAtUtc = changedAtUtc;
                context.UpdatedAtUtc = changedAtUtc;
                break;
            }
            case AccessLossKind.MembershipRevoked:
            {
                var membership = await db.WorkspaceMemberships.SingleAsync(item => item.Id == _membershipId);
                membership.Status = WorkspaceMembershipStatus.Revoked;
                membership.RevokedAtUtc = changedAtUtc;
                membership.UpdatedAtUtc = changedAtUtc;
                break;
            }
            case AccessLossKind.LastAssignmentRemoved:
                await db.MembershipRoleAssignments
                    .Where(item => item.Id == _assignmentId)
                    .ExecuteDeleteAsync();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(loss), loss, null);
        }

        await db.SaveChangesAsync();
    }

    private RentalCommandDbContext NewPlainContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var services = _services
            ?? throw new InvalidOperationException("Atomic refresh services are unavailable.");
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, codec);
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; session refresh kernel test skipped.");

    private sealed record IssuedCredential(Guid FamilyId, Guid CredentialId, string TokenHash);

    public enum AccessLossKind
    {
        ContextSuspended,
        MembershipRevoked,
        LastAssignmentRemoved,
    }

    private sealed class RefreshTestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:session-refresh";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class ReuseFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailNextReuse { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextReuse &&
                eventData.Context?.ChangeTracker.Entries<AuthSessionRefreshTokenFamily>()
                    .Any(entry => entry.Entity.ReuseDetectedAtUtc is not null) == true)
            {
                FailNextReuse = false;
                throw new InjectedReuseFailureException();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedReuseFailureException : Exception { }
}
