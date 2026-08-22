using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Configuration;
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
    private static readonly string SigningKey = Convert.ToBase64String(
        Enumerable.Range(1, RefreshCredentialTokenFactory.MinimumSigningKeyBytes)
            .Select(value => (byte)value)
            .ToArray());
    private static readonly AtomicJsonResultCodec<SessionRefreshMutationResult> Codec =
        new("session-refresh-mutation-result.v1");

    private readonly DateTime _now = new(2026, 7, 10, 18, 0, 0, DateTimeKind.Utc);
    private readonly Guid _sessionId = Guid.NewGuid();
    private int _userId;
    private int _accessContextId;
    private int _membershipId;
    private int _assignmentId;
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private ReuseFailureInterceptor? _failureInterceptor;
    private SqlCaptureInterceptor? _sqlCapture;
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
        _sqlCapture = new SqlCaptureInterceptor();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, RefreshTestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddSingleton(new RefreshCredentialTokenFactory(SigningKey));
        services.AddSingleton<IAuthSecurityClock>(new FixedAuthSecurityClock(_now));
        services.Configure<AtomicAuthSessionCredentialOptions>(options =>
        {
            options.SigningKey = SigningKey;
            options.CredentialLifetimeDays = 7;
            options.FamilyAbsoluteLifetimeDays = 30;
            options.SessionLifetimeDays = 30;
        });
        services.AddScoped<IAtomicAuthSessionCredentialService, AtomicAuthSessionCredentialService>();
        services.AddSingleton(_failureInterceptor);
        services.AddSingleton(_sqlCapture);
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_connectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<ReuseFailureInterceptor>(),
                    provider.GetRequiredService<SqlCaptureInterceptor>()));
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
    public async Task Issue_ExecutorAcquiresFamilyLockBeforeReadingSessionEligibility()
    {
        SkipIfNoDocker();
        _sqlCapture!.Clear();
        var familyId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        await ExecuteAtomicAsync(
            Identity("issue", Guid.NewGuid()),
            Issue(familyId, credentialId, Hash("issue-lock-order")),
            Codec);

        var commands = _sqlCapture.Snapshot();
        var lockIndex = Array.FindIndex(commands, command => command.Contains("pg_advisory_xact_lock"));
        var sessionReadIndex = Array.FindIndex(commands, command =>
            command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("\"AuthSessions\""));
        lockIndex.Should().BeGreaterThanOrEqualTo(0);
        sessionReadIndex.Should().BeGreaterThan(lockIndex);
    }

    [SkippableFact]
    public async Task ApiCredentialService_RefreshReplayReturnsReuseAndRevokesTheWholeFamily()
    {
        SkipIfNoDocker();
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var credentialId = Guid.NewGuid();
        var bearer = tokens.CreateBearer(credentialId);
        var familyId = Guid.NewGuid();
        await ExecuteAtomicAsync(
            Identity("issue", Guid.NewGuid()),
            Issue(familyId, credentialId, tokens.HashBearer(bearer)),
            Codec);

        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAtomicAuthSessionCredentialService>();
        var first = await service.RotateAsync(new AtomicAuthSessionRotationRequest(Guid.NewGuid(), bearer));
        var replay = await service.RotateAsync(new AtomicAuthSessionRotationRequest(Guid.NewGuid(), bearer));

        first.Status.Should().Be(SessionRefreshMutationStatus.Rotated);
        replay.Status.Should().Be(SessionRefreshMutationStatus.ReuseDetected);
        replay.ReplacementBearer.Should().BeNull();

        await using var db = NewPlainContext();
        (await db.AuthSessions.SingleAsync(item => item.Id == _sessionId)).Status
            .Should().Be(AuthSessionStatus.Revoked);
        (await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == familyId))
            .RevokedAtUtc.Should().NotBeNull();
        (await db.AtomicAuditLogs.CountAsync(item =>
            item.ChangeReason == "Refresh credential reuse detected"))
            .Should().Be(1);
        (await service.RotateAsync(new AtomicAuthSessionRotationRequest(
            Guid.NewGuid(),
            first.ReplacementBearer!))).Status.Should().Be(SessionRefreshMutationStatus.Rejected);
    }

    [SkippableFact]
    public async Task ApiCredentialService_LogoutRevokesTheSessionAndRejectsItsRefreshCredential()
    {
        SkipIfNoDocker();
        var tokens = new RefreshCredentialTokenFactory(SigningKey);
        var credentialId = Guid.NewGuid();
        var bearer = tokens.CreateBearer(credentialId);
        var familyId = Guid.NewGuid();
        await ExecuteAtomicAsync(
            Identity("issue", Guid.NewGuid()),
            Issue(familyId, credentialId, tokens.HashBearer(bearer)),
            Codec);

        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAtomicAuthSessionCredentialService>();
        var logout = await service.RevokeSessionAsync(
            new RevokeAuthSessionCommand(
                _sessionId,
                _userId,
                _accessContextId,
                1,
                _now.AddMinutes(2),
                "User signed out"),
            Guid.NewGuid());

        logout.Revoked.Should().BeTrue();
        (await service.RotateAsync(new AtomicAuthSessionRotationRequest(
            Guid.NewGuid(),
            bearer))).Status.Should().Be(SessionRefreshMutationStatus.Rejected);

        await using var db = NewPlainContext();
        (await db.AuthSessions.SingleAsync(item => item.Id == _sessionId)).Status
            .Should().Be(AuthSessionStatus.Revoked);
        (await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == familyId))
            .RevokedAtUtc.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task ApiCredentialService_LogoutWithLargeRotationHistoryUsesSetBasedSql()
    {
        SkipIfNoDocker();
        const int historyLength = 4000;
        var familyId = Guid.NewGuid();
        var credentialIds = Enumerable.Range(0, historyLength)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var family = new AuthSessionRefreshTokenFamily
        {
            Id = familyId,
            AuthSessionId = _sessionId,
            CreatedAtUtc = _now,
            AbsoluteExpiresAtUtc = _now.AddDays(30),
        };
        var credentials = credentialIds.Select((credentialId, index) =>
            new AuthSessionRefreshCredential
            {
                Id = credentialId,
                RefreshTokenFamilyId = familyId,
                TokenHash = Hash($"history-{index}"),
                IssuedAtUtc = _now.AddSeconds(index),
                ExpiresAtUtc = _now.AddDays(7),
                ConsumedAtUtc = index == historyLength - 1
                    ? null
                    : _now.AddSeconds(index).AddMilliseconds(500),
                ConsumedByOperationId = index == historyLength - 1
                    ? null
                    : Guid.NewGuid(),
                ReplacedByCredentialId = index == historyLength - 1
                    ? null
                    : credentialIds[index + 1],
            })
            .ToArray();

        await using (var seed = NewPlainContext())
        {
            seed.AuthSessionRefreshTokenFamilies.Add(family);
            seed.AuthSessionRefreshCredentials.AddRange(credentials);
            await seed.SaveChangesAsync();
        }

        _sqlCapture!.Clear();
        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAtomicAuthSessionCredentialService>();
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logout = await service.RevokeSessionAsync(
            new RevokeAuthSessionCommand(
                _sessionId,
                _userId,
                _accessContextId,
                1,
                _now.AddMinutes(2),
                "User signed out"),
            Guid.NewGuid(),
            serverTimeout.Token);

        logout.Revoked.Should().BeTrue();
        serverTimeout.IsCancellationRequested.Should().BeFalse();

        var commands = _sqlCapture.Snapshot();
        var updates = commands
            .Where(command => command.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        updates.Should().Contain(command =>
            command.Contains("\"AuthSessions\"", StringComparison.Ordinal) &&
            command.Contains("\"Id\"", StringComparison.Ordinal) &&
            command.Contains("\"UserId\"", StringComparison.Ordinal));
        updates.Should().Contain(command =>
            command.Contains("\"AuthSessionRefreshTokenFamilies\"", StringComparison.Ordinal) &&
            command.Contains("\"AuthSessionId\"", StringComparison.Ordinal));
        updates.Should().Contain(command =>
            command.Contains("\"AuthSessionRefreshCredentials\"", StringComparison.Ordinal) &&
            command.Contains("\"AuthSessionId\"", StringComparison.Ordinal));
        commands.Should().NotContain(command =>
            command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            command.Contains("\"AuthSessionRefreshTokenFamilies\"", StringComparison.Ordinal) &&
            command.Contains("\"AuthSessionRefreshCredentials\"", StringComparison.Ordinal));

        await using var verify = NewPlainContext();
        (await verify.AuthSessions.SingleAsync(item => item.Id == _sessionId))
            .Status.Should().Be(AuthSessionStatus.Revoked);
        (await verify.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == familyId))
            .RevokedAtUtc.Should().Be(_now.AddMinutes(2));
        (await verify.AuthSessionRefreshCredentials.CountAsync(item =>
            item.RefreshTokenFamilyId == familyId && item.RevokedAtUtc != null))
            .Should().Be(historyLength);
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
        var original = await db.AuthSessionRefreshCredentials
            .SingleAsync(item => item.Id == issued.CredentialId);
        original.ConsumedAtUtc.Should().Be(command.PresentedAtUtc);
        original.ConsumedByOperationId.Should().Be(operation);
        original.ReplacedByCredentialId.Should().Be(replacementId);
        (await db.AuthSessionRefreshTokenFamilies.SingleAsync(item => item.Id == issued.FamilyId))
            .ReuseDetectedAtUtc.Should().BeNull();
        (await db.AuthSessions.SingleAsync(item => item.Id == _sessionId))
            .Status.Should().Be(AuthSessionStatus.Active);
    }

    [SkippableFact]
    public async Task Rotation_ExecutorPreservesDiscoveryLockAndProtectedRereadOrder()
    {
        SkipIfNoDocker();
        var issued = await IssueCredentialAsync();
        _sqlCapture!.Clear();
        var operation = Guid.NewGuid();

        await ExecuteAtomicAsync(
            Identity("rotate", operation),
            Rotate(operation, issued.TokenHash, Guid.NewGuid(), Hash("rotation-lock-order")),
            Codec);

        var commands = _sqlCapture.Snapshot();
        var credentialReads = commands
            .Select((command, index) => (command, index))
            .Where(item => item.command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                item.command.Contains("\"AuthSessionRefreshCredentials\""))
            .Select(item => item.index)
            .ToArray();
        var lockIndex = Array.FindIndex(commands, command => command.Contains("pg_advisory_xact_lock"));
        credentialReads.Should().HaveCountGreaterThanOrEqualTo(2);
        credentialReads.First().Should().BeLessThan(lockIndex);
        credentialReads.Last().Should().BeGreaterThan(lockIndex);
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
        _userId = user.Id;
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
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        object write = command switch
        {
            IssueSessionRefreshCredentialCommand value => Build(value, new IssueSessionRefreshCredentialRule(db)),
            RotateSessionRefreshCredentialCommand value => Build(value, new RotateSessionRefreshCredentialRule(db)),
            RevokeAuthSessionCommand value => Build(value, new RevokeAuthSessionRule(db)),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(identity.IdempotencyKey, (TransactionalWrite<TCommand, TResult>)write);
    }

    private static TransactionalWrite<IssueSessionRefreshCredentialCommand, SessionRefreshMutationResult> Build(
        IssueSessionRefreshCredentialCommand command, IssueSessionRefreshCredentialRule handler) =>
        AuthSessionWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync);

    private static TransactionalWrite<RotateSessionRefreshCredentialCommand, SessionRefreshMutationResult> Build(
        RotateSessionRefreshCredentialCommand command, RotateSessionRefreshCredentialRule handler) =>
        AuthSessionWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync);

    private static TransactionalWrite<RevokeAuthSessionCommand, RevokeAuthSessionResult> Build(
        RevokeAuthSessionCommand command, RevokeAuthSessionRule handler) =>
        AuthSessionWriteSupport.Write(command, handler.ExecuteAsync, handler.AuthorizeAsync);

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

    private sealed class FixedAuthSecurityClock : IAuthSecurityClock
    {
        private readonly DateTime _utcNow;

        public FixedAuthSecurityClock(DateTime utcNow) => _utcNow = utcNow;

        public DateTime UtcNow() => _utcNow;
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

    private sealed class SqlCaptureInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public void Clear()
        {
            while (_commands.TryDequeue(out _))
            {
            }
        }

        public string[] Snapshot() => _commands.ToArray();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InjectedReuseFailureException : Exception { }
}
