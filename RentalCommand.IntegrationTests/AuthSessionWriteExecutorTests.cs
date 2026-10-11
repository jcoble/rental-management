using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Auth;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class AuthSessionWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FamilyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CredentialId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ReplacementCredentialId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid RotationOperationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid IssueCredentialId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid IssueFamilyId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid PresentedCredentialId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    // Frozen legacy fingerprints computed from the command DTO shapes at cb065049. These values
    // must never be regenerated from the current command model or a current serialization helper.
    private const string BootstrapFingerprint = "92b38a017787dfa87545939d5101695d666371b25ec63613b80375014045e6ac";
    private const string ConfirmFingerprint = "368a9b8ff1f62b268b710f9b3c17a938e257dc1cd25de24e1b529562f88ab860";
    private const string ResetFingerprint = "4d6efa2f81958a08b4e51b14c7730bca2b6cc6c69407c3184d8e778d09144d06";
    private const string GoogleFingerprint = "50037a90335bce6a424a2324666a634affb628e839bdbf87d99a3483e185da4f";
    private const string EmailFingerprint = "9225ff00638561e4d97ef560e93f70351218dfd7ea8a06721b9aaafd948203c8";
    private const string ChallengeFingerprint = "9687fcdab1cd43b9af6fb7fcfdd2f67a3d1d8ca6bf6df37532e60609648c0bf8";
    private const string StartFingerprint = "e0e8b6a4967ca0e4cf43cc716c9e4301105e83eed35ddda4d3d5ddcb82909e95";
    private const string IssueFingerprint = "9721ff5cfda4f904931357976377366419fc49d346c0981588ef4bf57524d1f0";
    private const string RotateFingerprint = "a0de59a2c19b034756c1f7e7ea7b2cfb06c57f9d379deb5b78f089ca50b3faa5";
    private const string SwitchFingerprint = "c463d2588aee525e11e7bc19b9fd6e23e1c55b23f6ae9f2eab380bfbb65eb78d";
    private const string ChangeFingerprint = "eca52acba2c68af369561df2aab1e8d6be9644462596a3e7e2223d9a25f2099f";
    private const string RevokeFingerprint = "b2edb33c99ffa4c5ae96015293977e7f4ec7e0ec7d502502d4ffb282b82a8deb";

    [Fact]
    public async Task LegacyReceiptFixtures_ReplayEveryDistinctAuthSessionResultContract()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayAuthorityAsync(database.Db);
        await using var services = Services(database.ConnectionString);

        await ReplayAsync(services, "auth.account.bootstrap", LegacyBootstrapKey("bootstrap-operation"),
            Bootstrap(), BootstrapFingerprint,
            "{\"Outcome\":0,\"UserId\":7,\"PortfolioId\":8,\"AccessContextId\":9}",
            new BootstrapAccountResult(BootstrapAccountOutcome.Created, 7, 8, 9),
            "auth-account-bootstrap-result:v1", db => new BootstrapAccountRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth.email.confirm", LegacyUserOperationKey(7, "confirm-operation"),
            Confirm(), ConfirmFingerprint, "{\"Outcome\":0,\"UserId\":7}",
            new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, 7),
            "auth-email-confirm-result:v1", db => new ConfirmAccountEmailRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth.email.google-confirm", $"7:{new string('d', 64)}",
            Google(), GoogleFingerprint, "{\"Outcome\":0,\"UserId\":7}",
            new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, 7),
            "auth-email-confirm-result:v1", db => new ConfirmGoogleAccountEmailRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth.password.reset", LegacyUserOperationKey(7, "reset-operation"),
            Reset(), ResetFingerprint, "{\"Outcome\":0,\"UserId\":7}",
            new ResetAccountPasswordResult(ResetAccountPasswordOutcome.Reset, 7),
            "auth-password-reset-result:v1", db => new ResetAccountPasswordRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth.email.email-confirmation", LegacyUserOperationKey(7, "email-operation"),
            Email(), EmailFingerprint,
            "{\"Enqueued\":true,\"UserId\":7,\"PortfolioId\":8,\"EmailKind\":\"email-confirmation\"}",
            new AuthEmailOutboxResult(true, 7, 8, "email-confirmation"),
            "auth-email-outbox-result:v1", db => new AuthEmailOutboxRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth-context-selection:issue", LegacyOperationKey(Guid.Parse("12121212-1212-1212-1212-121212121212")),
            Challenge(), ChallengeFingerprint,
            "{\"Issued\":true,\"ChallengeId\":\"22222222-2222-2222-2222-222222222222\",\"UserId\":7,\"ExpiresAtUtc\":\"2026-08-21T12:05:00Z\"}",
            new LoginContextSelectionChallengeResult(true, Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, Now.AddMinutes(5)),
            "auth-context-selection-challenge-result:v1", db => new IssueLoginContextSelectionChallengeRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth-session:start", LegacyOperationKey(Guid.Parse("13131313-1313-1313-1313-131313131313")),
            Start(), StartFingerprint,
            "{\"Started\":true,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\",\"UserId\":7,\"AccessContextId\":9,\"PortfolioId\":8,\"AccessRevision\":3,\"RefreshTokenFamilyId\":\"33333333-3333-3333-3333-333333333333\",\"CredentialId\":\"44444444-4444-4444-4444-444444444444\"}",
            new StartAuthSessionResult(true, SessionId, 7, 9, 8, 3, FamilyId, CredentialId),
            "auth-session-start-result:v1", db => new StartAuthSessionRule(db).AuthorizeAsync);
        await ReplayAsync(services, "session-refresh:issue", LegacyOperationKey(Guid.Parse("14141414-1414-1414-1414-141414141414")),
            Issue(), IssueFingerprint,
            "{\"Status\":0,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\",\"RefreshTokenFamilyId\":\"99999999-9999-9999-9999-999999999999\",\"CredentialId\":\"88888888-8888-8888-8888-888888888888\",\"ReplacementCredentialId\":null,\"UserId\":null,\"AccessContextId\":null,\"PortfolioId\":null,\"AccessRevision\":null}",
            new SessionRefreshMutationResult(SessionRefreshMutationStatus.Issued, SessionId, IssueFamilyId, IssueCredentialId),
            "session-refresh-mutation-result.v1", db => new IssueSessionRefreshCredentialRule(db).AuthorizeAsync);
        await ReplayAsync(services, "session-refresh:rotate", LegacyOperationKey(RotationOperationId),
            Rotate(), RotateFingerprint,
            "{\"Status\":1,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\",\"RefreshTokenFamilyId\":\"33333333-3333-3333-3333-333333333333\",\"CredentialId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"ReplacementCredentialId\":\"55555555-5555-5555-5555-555555555555\",\"UserId\":7,\"AccessContextId\":9,\"PortfolioId\":8,\"AccessRevision\":3}",
            new SessionRefreshMutationResult(SessionRefreshMutationStatus.Rotated, SessionId, FamilyId,
                PresentedCredentialId, ReplacementCredentialId, 7, 9, 8, 3),
            "auth-session-refresh-rotation-result:v1", db => new RotateSessionRefreshCredentialRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth-context:switch", LegacyOperationKey(Guid.Parse("15151515-1515-1515-1515-151515151515")),
            Switch(), SwitchFingerprint,
            "{\"Switched\":true,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\",\"UserId\":7,\"AccessContextId\":10,\"PortfolioId\":11,\"AccessRevision\":4}",
            new SwitchAuthSessionContextResult(true, SessionId, 7, 10, 11, 4),
            "auth-session-context-switch-result:v1", db => new SwitchAuthSessionContextRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth.password.change", LegacyChangePasswordKey(7, 9, "change-operation"),
            Change(), ChangeFingerprint, "{\"Outcome\":0,\"UserId\":7,\"AccessContextId\":9}",
            new ChangePasswordResult(ChangePasswordOutcome.Changed, 7, 9),
            "auth-password-change-result:v1", db => new ChangePasswordRule(db).AuthorizeAsync);
        await ReplayAsync(services, "auth-session:revoke", LegacyOperationKey(Guid.Parse("16161616-1616-1616-1616-161616161616")),
            Revoke(), RevokeFingerprint,
            "{\"Revoked\":true,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\"}",
            new RevokeAuthSessionResult(true, SessionId),
            "auth-session-revoke-result:v1", db => new RevokeAuthSessionRule(db).AuthorizeAsync);
    }

    [Fact]
    public async Task LegacyReceiptReplay_RefusesChangedSessionState()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayAuthorityAsync(database.Db);
        await database.Db.AuthSessions.Where(session => session.Id == SessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                .SetProperty(session => session.RevokedAtUtc, Now));
        await using var services = Services(database.ConnectionString);

        var action = () => ReplayAsync(services, "auth.password.change",
            LegacyChangePasswordKey(7, 9, "change-operation"), Change(), ChangeFingerprint,
            "{\"Outcome\":0,\"UserId\":7,\"AccessContextId\":9}",
            new ChangePasswordResult(ChangePasswordOutcome.Changed, 7, 9),
            "auth-password-change-result:v1", db => new ChangePasswordRule(db).AuthorizeAsync);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task LegacyRotationReceiptReplay_RefusesMissingCredentialOwnership()
    {
        await using var database = await fixture.CreateContextAsync();
        await using var services = Services(database.ConnectionString);

        var action = () => ReplayAsync(services, "session-refresh:rotate",
            LegacyOperationKey(RotationOperationId), Rotate(), RotateFingerprint,
            "{\"Status\":1,\"AuthSessionId\":\"11111111-1111-1111-1111-111111111111\",\"RefreshTokenFamilyId\":\"33333333-3333-3333-3333-333333333333\",\"CredentialId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"ReplacementCredentialId\":\"55555555-5555-5555-5555-555555555555\",\"UserId\":7,\"AccessContextId\":9,\"PortfolioId\":8,\"AccessRevision\":3}",
            new SessionRefreshMutationResult(SessionRefreshMutationStatus.Rotated, SessionId, FamilyId,
                PresentedCredentialId, ReplacementCredentialId, 7, 9, 8, 3),
            "auth-session-refresh-rotation-result:v1", db => new RotateSessionRefreshCredentialRule(db).AuthorizeAsync);

        await action.Should().ThrowAsync<RefreshTokenRotationOwnershipException>();
    }

    [Fact]
    public void Fingerprints_IgnoreExactlyTheExistingAuthSessionRetryFields()
    {
        var pairs = new (IAtomicCommandData First, IAtomicCommandData Retry)[]
        {
            (Bootstrap(), Bootstrap() with { PasswordHash = "different" }),
            (Confirm(), Confirm() with { ExpectedSecurityStamp = "different", TokenWasValidated = false }),
            (Reset(), Reset() with { ExpectedSecurityStamp = "different", TokenWasValidated = false, PasswordHash = "different" }),
            (Google(), Google() with { ExpectedSecurityStamp = "different" }),
            (Email(), Email() with { ExpectedSecurityStamp = "different", PreparedEmailPayload = "different", DeliveryIdempotencyKey = "different" }),
            (Challenge(), Challenge() with { IssuedAtUtc = Now.AddSeconds(1), ExpiresAtUtc = Now.AddMinutes(6) }),
            (Start(), Start() with { ExpectedAccessRevision = 99, AuthSessionId = Guid.NewGuid(), IssuedAtUtc = Now.AddSeconds(1), SessionExpiresAtUtc = Now.AddDays(31), CredentialExpiresAtUtc = Now.AddDays(8), AbsoluteFamilyExpiresAtUtc = Now.AddDays(31) }),
            (Issue(), Issue() with { AuthSessionId = Guid.NewGuid(), IssuedAtUtc = Now.AddSeconds(1), ExpiresAtUtc = Now.AddDays(8), AbsoluteFamilyExpiresAtUtc = Now.AddDays(31) }),
            (Rotate(), Rotate() with { PresentedAtUtc = Now.AddSeconds(1), ReplacementExpiresAtUtc = Now.AddDays(8) }),
            (Switch(), Switch() with { AuthSessionId = Guid.NewGuid(), ChangedAtUtc = Now.AddSeconds(1) }),
            (Change(), Change() with { AuthSessionId = Guid.NewGuid(), AccessContextId = 99, ExpectedAccessRevision = 99, CurrentPassword = "different", NewPassword = "different" }),
            (Revoke(), Revoke() with { AuthSessionId = Guid.NewGuid(), AccessContextId = 99, AccessRevision = 99 }),
        };

        pairs.Should().AllSatisfy(pair =>
            AtomicCommandFingerprint.Create(pair.Retry).Should().Be(AtomicCommandFingerprint.Create(pair.First)));
    }

    private static async Task ReplayAsync<TCommand, TResult>(
        ServiceProvider services,
        string operation,
        string key,
        TCommand command,
        string legacyFingerprint,
        string legacyResultJson,
        TResult stored,
        string contract,
        Func<RentalCommandDbContext, Func<TCommand, IAtomicCommandContext, CancellationToken, Task>> authorize)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = AuthSessionWriteSupport.Write<TCommand, TResult>(
            command,
            (_, _, _) => throw new InvalidOperationException("A stored receipt must not execute."),
            authorize(db));
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);
        write.LockPlan.Should().BeSameAs(WriteLockPlan.None);
        await using (var fixtureDb = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(db.Database.GetConnectionString())
                .Options))
        {
            fixtureDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = operation,
                IdempotencyKey = key,
                RequestFingerprint = legacyFingerprint,
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = contract,
                ResultJson = legacyResultJson,
                StartedAt = Now,
                CompletedAt = Now,
            });
            await fixtureDb.SaveChangesAsync();
        }

        var outcome = await scope.ServiceProvider.GetRequiredService<IWriteExecutor>()
            .ExecuteAsync(key, write);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        outcome.Value.Should().BeEquivalentTo(stored);
    }

    private static async Task SeedReplayAuthorityAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = 7, UserName = "user@example.test", NormalizedUserName = "USER@EXAMPLE.TEST",
            Email = "user@example.test", NormalizedEmail = "USER@EXAMPLE.TEST", DisplayName = "User",
            SecurityStamp = "stamp", ConcurrencyStamp = Guid.NewGuid().ToString("N"), CreatedAt = Now.AddDays(-2),
        };
        var currentPortfolio = new Portfolio
        {
            Id = 8, Name = "Portfolio", ManagementCompanyName = "Company",
            CreatedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-2),
        };
        var selectedPortfolio = new Portfolio
        {
            Id = 11, Name = "Selected Portfolio", ManagementCompanyName = "Selected Company",
            CreatedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-2),
        };
        db.AddRange(user, currentPortfolio, selectedPortfolio);
        await db.SaveChangesAsync();

        var currentContext = AccessContext(9, 8);
        currentContext.AdvanceRevision(1);
        currentContext.AdvanceRevision(2);
        var selectedContext = AccessContext(10, 11);
        selectedContext.AdvanceRevision(1);
        selectedContext.AdvanceRevision(2);
        selectedContext.AdvanceRevision(3);
        db.AddRange(currentContext, selectedContext);
        await db.SaveChangesAsync();

        var currentMembership = Membership(9, 8);
        var selectedMembership = Membership(10, 11);
        db.AddRange(currentMembership, selectedMembership);
        await db.SaveChangesAsync();
        db.AddRange(Assignment(currentMembership.Id, 8), Assignment(selectedMembership.Id, 11));
        await db.SaveChangesAsync();

        var session = new AuthSession
        {
            Id = SessionId, UserId = 7, ActiveAccessContextId = 9, Status = AuthSessionStatus.Active,
            CreatedAtUtc = Now, LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddDays(30),
        };
        var startFamily = new AuthSessionRefreshTokenFamily
        {
            Id = FamilyId, AuthSessionId = SessionId, CreatedAtUtc = Now, AbsoluteExpiresAtUtc = Now.AddDays(30),
        };
        startFamily.Credentials.Add(Credential(CredentialId, new string('1', 64)));
        startFamily.Credentials.Add(new AuthSessionRefreshCredential
        {
            Id = PresentedCredentialId, TokenHash = new string('3', 64), IssuedAtUtc = Now,
            ExpiresAtUtc = Now.AddDays(7), ConsumedAtUtc = Now,
            ConsumedByOperationId = RotationOperationId, ReplacedByCredentialId = ReplacementCredentialId,
        });
        startFamily.Credentials.Add(Credential(ReplacementCredentialId, new string('4', 64)));
        var issueFamily = new AuthSessionRefreshTokenFamily
        {
            Id = IssueFamilyId, AuthSessionId = SessionId, CreatedAtUtc = Now, AbsoluteExpiresAtUtc = Now.AddDays(30),
        };
        issueFamily.Credentials.Add(Credential(IssueCredentialId, new string('2', 64)));
        db.AddRange(session, startFamily, issueFamily, new LoginContextSelectionChallenge
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), UserId = 7,
            TokenHash = new string('f', 64), CreatedAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(5),
        });
        await db.SaveChangesAsync();
    }

    private static WorkspaceAccessContext AccessContext(int id, int portfolioId) => new()
    {
        Id = id, UserId = 7, PortfolioId = portfolioId, Status = WorkspaceAccessContextStatus.Active,
        CreatedAtUtc = Now.AddDays(-2), UpdatedAtUtc = Now.AddDays(-2),
    };

    private static WorkspaceMembership Membership(int contextId, int portfolioId) => new()
    {
        AccessContextId = contextId, PortfolioId = portfolioId, Status = WorkspaceMembershipStatus.Active,
        DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = Now.AddDays(-1),
        CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
    };

    private static MembershipRoleAssignment Assignment(int membershipId, int portfolioId) => new()
    {
        WorkspaceMembershipId = membershipId, PortfolioId = portfolioId, RoleProfileId = 1,
        Status = MembershipRoleAssignmentStatus.Active, ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
        EffectiveFromUtc = Now.AddDays(-1), CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
    };

    private static AuthSessionRefreshCredential Credential(Guid id, string hash) => new()
    {
        Id = id, TokenHash = hash, IssuedAtUtc = Now, ExpiresAtUtc = Now.AddDays(7),
    };

    private static string LegacyBootstrapKey(string operationKey) => $"email:{Sha256(operationKey)}";
    private static string LegacyUserOperationKey(int userId, string operationKey) => $"{userId}:{Sha256(operationKey)}";
    private static string LegacyChangePasswordKey(int userId, int accessContextId, string operationKey) =>
        $"{userId}:{accessContextId}:{Sha256(operationKey)}";
    private static string LegacyOperationKey(Guid operationId) => $"operation:{operationId:N}";
    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private static BootstrapAccountCommand Bootstrap() => new(
        "user@example.test", "USER@EXAMPLE.TEST", "User", "hash", new string('a', 64),
        false, true, "Portfolio", "Company", "Owner", Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private static ConfirmAccountEmailCommand Confirm() => new(7, "stamp", true, new string('b', 64));
    private static ResetAccountPasswordCommand Reset() => new(7, "stamp", true, "hash", new string('c', 64));
    private static ConfirmGoogleAccountEmailCommand Google() => new(7, "stamp", new string('d', 64));
    private static AuthEmailOutboxCommand Email() => new(7, 8, "stamp", "email-confirmation", "{}", new string('e', 64), "delivery");
    private static IssueLoginContextSelectionChallengeCommand Challenge() => new(
        7, Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('f', 64), Now, Now.AddMinutes(5));
    private static StartAuthSessionCommand Start() => new(
        7, 9, 3, SessionId, FamilyId, CredentialId, new string('1', 64), Now,
        Now.AddDays(30), Now.AddDays(7), Now.AddDays(30));
    private static IssueSessionRefreshCredentialCommand Issue() => new(
        SessionId, IssueFamilyId, IssueCredentialId, new string('2', 64), Now, Now.AddDays(7), Now.AddDays(30));
    private static RotateSessionRefreshCredentialCommand Rotate() => new(
        RotationOperationId, new string('3', 64), ReplacementCredentialId, new string('4', 64), Now, Now.AddDays(7));
    private static SwitchAuthSessionContextCommand Switch() => new(SessionId, 7, 9, 3, 10, Now);
    private static ChangePasswordCommand Change() => new(SessionId, 7, 9, 3, "old", "new", new string('5', 64));
    private static RevokeAuthSessionCommand Revoke() => new(SessionId, 7, 9, 3, Now, "logout");
}
