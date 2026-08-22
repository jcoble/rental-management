using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Sandbox;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Simulation;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class SimulationWriteExecutorReceiptTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // Frozen legacy fingerprints computed from the command DTO shapes at d24b5c95. These values
    // must never be regenerated from the current command model or a current serialization helper.
    private const string SetFingerprint = "923a784c14e3ecbdc26091fc5202ec20b2b31c211104a7c9e66685ac511eaf8b";
    private const string AdvanceFingerprint = "b48136983785bbc1c4ac24558eaebbe345359373a3a2700f7d71a9a66f94422d";
    private const string EnvelopeFingerprint = "4284d68b71e0dfdd899eee324bf81317ecd1543aaa45bbcf499e4a52c54a9910";
    private const string EnqueueFingerprint = "b59db55d4f510ff409695814c04865963e53d014213999ef2dc933f743a9d3c8";
    private const string SandboxChoiceFingerprint = "29e8f4240f0b10f29e953187190b82b76030a7a9d40801fcd023bf684142dcee";
    private const string SandboxLiveFingerprint = "0589506427b01cbcd644e20d45cb38c8d71c12125ab2cf80cf8d31596593544c";

    // Frozen legacy operation names hand-reproduced from the handlers at d24b5c95.
    private const string SetOperation = "simulation.clock.set";
    private const string AdvanceOperation = "simulation.clock.advance";
    private const string FreezeOperation = "simulation.clock.freeze";
    private const string UnfreezeOperation = "simulation.clock.unfreeze";
    private const string ResetOperation = "simulation.clock.reset";
    private const string EnqueueOperation = "simulation.worker.enqueue";
    private const string SandboxChoiceOperation = "sandbox.onboarding-choice";
    private const string SandboxLiveOperation = "sandbox.go-live";

    [Fact]
    public async Task LegacyReceiptFixtures_ReplayEverySimulationAndSandboxOperation()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayAuthorityAsync(database.Db);
        await using var services = Services(database.ConnectionString);

        const string clockJson =
            "{\"SimNowUtc\":\"2027-01-29T05:00:00Z\",\"Mode\":\"Frozen\",\"TimeZoneId\":\"America/New_York\",\"OffsetSeconds\":123.5}";
        var clockResult = new SimulationClockMutationResult(
            new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc),
            "Frozen", "America/New_York", 123.5);
        await ReplayAsync(services, "8:7:set-replay", Set(), SetOperation, SetFingerprint,
            clockJson, clockResult, "simulation.clock.mutation.v1");
        await ReplayAsync(services, "8:7:advance-replay", Advance(), AdvanceOperation, AdvanceFingerprint,
            clockJson, clockResult, "simulation.clock.mutation.v1");
        await ReplayAsync(services, "8:7:freeze-replay", Freeze(), FreezeOperation, EnvelopeFingerprint,
            clockJson, clockResult, "simulation.clock.mutation.v1");
        await ReplayAsync(services, "8:7:unfreeze-replay", Unfreeze(), UnfreezeOperation, EnvelopeFingerprint,
            clockJson, clockResult, "simulation.clock.mutation.v1");
        await ReplayAsync(services, "8:7:reset-replay", Reset(), ResetOperation, EnvelopeFingerprint,
            clockJson, clockResult, "simulation.clock.mutation.v1");

        const string enqueueJson =
            "{\"CommandId\":\"33333333-3333-3333-3333-333333333333\",\"WorkerKey\":\"rent-charge\",\"Status\":\"Pending\",\"RequestedSimUtc\":\"2027-01-29T05:00:00Z\",\"CreatedRealUtc\":\"2026-08-21T12:00:00Z\"}";
        await ReplayAsync(services, "8:7:enqueue-replay", Enqueue(), EnqueueOperation, EnqueueFingerprint,
            enqueueJson,
            new EnqueueSimulationWorkerResult(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "rent-charge", "Pending",
                new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc), Now),
            "simulation.worker.enqueue.v1");

        const string sandboxJson =
            "{\"PortfolioFound\":true,\"PortfolioId\":8,\"IsSandbox\":false,\"SandboxSeededAtUtc\":null,\"OnboardingChoicePending\":false}";
        var sandboxResult = new SandboxLifecycleResult(true, 8, false, null, false);
        await ReplayAsync(services,
            "8:6a9be48f7341ecf93f39f5b4c50e834b91089ec9a1437d6ea1b58cae557dbb0d",
            SandboxChoice(), SandboxChoiceOperation, SandboxChoiceFingerprint, sandboxJson, sandboxResult,
            "sandbox-lifecycle-result:v1");
        await ReplayAsync(services,
            "8:d02fdcd03e9ebb006ad94dcc58b5f74c870b1e75142a74444c8117a53962b689",
            SandboxLive(), SandboxLiveOperation, SandboxLiveFingerprint, sandboxJson, sandboxResult,
            "sandbox-lifecycle-result:v1");
    }

    [Fact]
    public async Task LegacySimulationReceiptReplay_RefusesRevokedSession()
    {
        await using var database = await fixture.CreateContextAsync();
        await SeedReplayAuthorityAsync(database.Db);
        await database.Db.AuthSessions.Where(session => session.Id == SessionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                .SetProperty(session => session.RevokedAtUtc, Now));
        await using var services = Services(database.ConnectionString);

        var action = () => ReplayAsync(services, "8:7:set-denied-replay", Set(), SetOperation, SetFingerprint,
            "{\"SimNowUtc\":\"2027-01-29T05:00:00Z\",\"Mode\":\"Frozen\",\"TimeZoneId\":\"America/New_York\",\"OffsetSeconds\":123.5}",
            new SimulationClockMutationResult(
                new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc),
                "Frozen", "America/New_York", 123.5),
            "simulation.clock.mutation.v1");

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public void Fingerprints_PreserveLegacySimulationAndSandboxRetryFields()
    {
        AtomicCommandFingerprint.Create(Set()).Should().Be(SetFingerprint);
        AtomicCommandFingerprint.Create(Advance()).Should().Be(AdvanceFingerprint);
        AtomicCommandFingerprint.Create(Freeze()).Should().Be(EnvelopeFingerprint);
        AtomicCommandFingerprint.Create(Unfreeze()).Should().Be(EnvelopeFingerprint);
        AtomicCommandFingerprint.Create(Reset()).Should().Be(EnvelopeFingerprint);
        AtomicCommandFingerprint.Create(Enqueue()).Should().Be(EnqueueFingerprint);
        AtomicCommandFingerprint.Create(SandboxChoice()).Should().Be(SandboxChoiceFingerprint);
        AtomicCommandFingerprint.Create(SandboxLive()).Should().Be(SandboxLiveFingerprint);

        AtomicCommandFingerprint.Create(Set() with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 99, ExpectedAccessRevision = 99,
        }).Should().Be(SetFingerprint);
        AtomicCommandFingerprint.Create(SandboxChoice() with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 99, ExpectedAccessRevision = 99,
            BusinessNowUtc = Now.AddHours(1), DeliveryIdempotencyKey = "different",
        }).Should().Be(SandboxChoiceFingerprint);
    }

    private static async Task ReplayAsync<TCommand, TResult>(
        ServiceProvider services,
        string key,
        TCommand command,
        string legacyOperation,
        string legacyFingerprint,
        string legacyResultJson,
        TResult expected,
        string contract)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var write = command is SandboxLifecycleCommand sandbox
            ? (TransactionalWrite<TCommand, TResult>)(object)SandboxLifecycleWriteSupport.Write(db, sandbox)
            : SimulationWriteSupport.Write<TCommand, TResult>(db, command);
        write.OperationName.Should().Be(legacyOperation);
        write.ResultContract.Should().Be(contract);

        await using (var fixtureDb = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(db.Database.GetConnectionString()).Options))
        {
            fixtureDb.AtomicCommandReceipts.Add(new AtomicCommandReceipt
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                CommandType = legacyOperation,
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

        var outcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
            .ExecuteExactAsync(key, write);
        outcome.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        outcome.Value.Should().BeEquivalentTo(expected);
    }

    private static async Task SeedReplayAuthorityAsync(RentalCommandDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = 7, UserName = "simulation@example.test", NormalizedUserName = "SIMULATION@EXAMPLE.TEST",
            Email = "simulation@example.test", NormalizedEmail = "SIMULATION@EXAMPLE.TEST",
            DisplayName = "Simulation", SecurityStamp = "stamp",
            ConcurrencyStamp = Guid.NewGuid().ToString("N"), CreatedAt = Now.AddDays(-2),
        };
        var portfolio = new Portfolio
        {
            Id = 8, Name = "Simulation", ManagementCompanyName = "Simulation Company",
            CreatedAt = Now.AddDays(-2), UpdatedAt = Now.AddDays(-2),
        };
        db.AddRange(user, portfolio);
        await db.SaveChangesAsync();

        var access = new WorkspaceAccessContext
        {
            Id = 9, UserId = 7, PortfolioId = 8, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = Now.AddDays(-2), UpdatedAtUtc = Now.AddDays(-2),
        };
        access.AdvanceRevision(1);
        access.AdvanceRevision(2);
        db.Add(access);
        await db.SaveChangesAsync();

        var membership = new WorkspaceMembership
        {
            AccessContextId = 9, PortfolioId = 8, Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management, EffectiveFromUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
        };
        db.Add(membership);
        await db.SaveChangesAsync();
        db.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id, PortfolioId = 8, RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = Now.AddDays(-1), CreatedAtUtc = Now.AddDays(-1), UpdatedAtUtc = Now.AddDays(-1),
        });
        db.Add(new AuthSession
        {
            Id = SessionId, UserId = 7, ActiveAccessContextId = 9, Status = AuthSessionStatus.Active,
            CreatedAtUtc = Now, LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddDays(30),
        });
        await db.SaveChangesAsync();
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

    private static SetSimulationClockCommand Set() => new(
        8, 7, SessionId, 9, 3,
        new DateTime(2027, 1, 29, 5, 0, 0, DateTimeKind.Utc),
        ClockMode.Frozen, true, "America/New_York");
    private static AdvanceSimulationClockCommand Advance() => new(8, 7, SessionId, 9, 3, 2, 3, 4, 5);
    private static FreezeSimulationClockCommand Freeze() => new(8, 7, SessionId, 9, 3);
    private static UnfreezeSimulationClockCommand Unfreeze() => new(8, 7, SessionId, 9, 3);
    private static ResetSimulationClockCommand Reset() => new(8, 7, SessionId, 9, 3);
    private static EnqueueSimulationWorkerCommand Enqueue() => new(
        8, 7, SessionId, 9, 3,
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "rent-charge");
    private static SandboxLifecycleCommand SandboxChoice() => new(
        8, 7, SessionId, 9, 3, SandboxLifecycleOperation.ApplyOnboardingChoice,
        SandboxOnboardingChoice.Live, Now, "sandbox-choice-replay");
    private static SandboxLifecycleCommand SandboxLive() => new(
        8, 7, SessionId, 9, 3, SandboxLifecycleOperation.GoLive,
        null, Now, "sandbox-live-replay");
}
