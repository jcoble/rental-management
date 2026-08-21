using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Auth;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class AuthSessionWriteExecutorTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task LegacyReceiptFixtures_ReplayEveryDistinctAuthSessionResultContract()
    {
        await using var database = await fixture.CreateContextAsync();
        await using var services = Services(database.ConnectionString);

        await ReplayAsync(services, "auth.account.bootstrap", "bootstrap", Bootstrap(),
            new BootstrapAccountResult(BootstrapAccountOutcome.Created, 7, 8, 9),
            "auth-account-bootstrap-result:v1");
        await ReplayAsync(services, "auth.email.confirm", "confirm", Confirm(),
            new ConfirmAccountEmailResult(ConfirmAccountEmailOutcome.Confirmed, 7),
            "auth-email-confirm-result:v1");
        await ReplayAsync(services, "auth.password.reset", "reset", Reset(),
            new ResetAccountPasswordResult(ResetAccountPasswordOutcome.Reset, 7),
            "auth-password-reset-result:v1");
        await ReplayAsync(services, "auth.email.email-confirmation", "outbox", Email(),
            new AuthEmailOutboxResult(true, 7, 8, "email-confirmation"),
            "auth-email-outbox-result:v1");
        await ReplayAsync(services, "auth-context-selection:issue", "challenge", Challenge(),
            new LoginContextSelectionChallengeResult(true, Guid.Parse("22222222-2222-2222-2222-222222222222"), 7, Now.AddMinutes(5)),
            "auth-context-selection-challenge-result:v1");
        await ReplayAsync(services, "auth-session:start", "start", Start(),
            new StartAuthSessionResult(true, SessionId, 7, 9, 8, 3, Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444")),
            "auth-session-start-result:v1");
        await ReplayAsync(services, "session-refresh:issue", "issue", Issue(),
            new SessionRefreshMutationResult(SessionRefreshMutationStatus.Issued, SessionId, Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444")),
            "session-refresh-mutation-result.v1");
        await ReplayAsync(services, "session-refresh:rotate", "rotate", Rotate(),
            new SessionRefreshMutationResult(SessionRefreshMutationStatus.Rotated, SessionId, Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse("55555555-5555-5555-5555-555555555555")),
            "auth-session-refresh-rotation-result:v1");
        await ReplayAsync(services, "auth-context:switch", "switch", Switch(),
            new SwitchAuthSessionContextResult(true, SessionId, 7, 10, 11, 4),
            "auth-session-context-switch-result:v1");
        await ReplayAsync(services, "auth.password.change", "change", Change(),
            new ChangePasswordResult(ChangePasswordOutcome.Changed, 7, 9),
            "auth-password-change-result:v1");
        await ReplayAsync(services, "auth-session:revoke", "revoke", Revoke(),
            new RevokeAuthSessionResult(true, SessionId),
            "auth-session-revoke-result:v1");
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
        TResult stored,
        string contract)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = AuthSessionWriteSupport.Write<TCommand, TResult>(
            command,
            (_, _, _) => throw new InvalidOperationException("A stored receipt must not execute."),
            (_, _, _) => Task.CompletedTask);
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);
        write.LockPlan.Should().BeSameAs(WriteLockPlan.None);
        var codec = new AtomicJsonResultCodec<TResult>(contract);
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
                RequestFingerprint = AtomicCommandFingerprint.Create(command),
                Status = AtomicCommandReceiptStatus.Completed,
                ResultContract = contract,
                ResultJson = codec.Serialize(stored),
                StartedAt = Now,
                CompletedAt = Now,
            });
            await fixtureDb.SaveChangesAsync();
        }

        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteAsync(key, write);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        outcome.Value.Should().BeEquivalentTo(stored);
    }

    private static ServiceProvider Services(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
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
        7, 9, 3, SessionId, Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"), new string('1', 64), Now,
        Now.AddDays(30), Now.AddDays(7), Now.AddDays(30));
    private static IssueSessionRefreshCredentialCommand Issue() => new(
        SessionId, Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"), new string('2', 64),
        Now, Now.AddDays(7), Now.AddDays(30));
    private static RotateSessionRefreshCredentialCommand Rotate() => new(
        Guid.Parse("77777777-7777-7777-7777-777777777777"), new string('3', 64),
        Guid.Parse("55555555-5555-5555-5555-555555555555"), new string('4', 64), Now, Now.AddDays(7));
    private static SwitchAuthSessionContextCommand Switch() => new(SessionId, 7, 9, 3, 10, Now);
    private static ChangePasswordCommand Change() => new(SessionId, 7, 9, 3, "old", "new", new string('5', 64));
    private static RevokeAuthSessionCommand Revoke() => new(SessionId, 7, 9, 3, Now, "logout");
}
