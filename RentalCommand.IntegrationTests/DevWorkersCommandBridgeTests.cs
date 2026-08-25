using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Simulation;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;
using RentalCommand.TestCommon;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Exercises the command-bridge (spec §6.2) across a real Postgres: a Pending SimWorkerCommand (as the
/// API's DevWorkersController enqueues) is claimed, run, and completed by the Engine's
/// SimWorkerCommandWorker. The harness does not boot the Engine host, so — per the plan/review — the
/// worker's claim→run→result cycle is driven directly against the Testcontainers DB with the leaf
/// automation service mocked (the registry mapping itself is unit-tested in Engine.Tests).
/// </summary>
public sealed class DevWorkersCommandBridgeTests : IAsyncLifetime
{
    private SharedPostgreSqlDatabase? _pg;
    private bool _dockerAvailable;
    private string _conn = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _conn = _pg.GetConnectionString();
        await using var ctx = NewContext(_conn);
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [SkippableFact]
    public async Task PendingCommand_IsClaimedRunAndCompleted_WithCreatedResult()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; command-bridge Postgres verification skipped.");

        // 1. Enqueue a Pending rent-charge command (as DevWorkersController.RunOnce would).
        var id = Guid.NewGuid();
        await using (var seed = NewContext(_conn))
        {
            seed.SimWorkerCommands.Add(new SimWorkerCommand
            {
                Id = id,
                WorkerKey = SimWorkerKeys.RentCharge,
                RequestedSimUtc = new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc),
                Status = SimWorkerCommandStatus.Pending,
                CreatedRealUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        // 2. A scope with a real PG DbContext + a mocked automation service + the real registry.
        var rentCharge = new Mock<IRentChargeService>();
        rentCharge.Setup(s => s.GenerateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_conn).UseAtomicPersistenceKernel(provider));
        services.AddScoped<ISimWorkerCommandClaimStore, SimWorkerCommandClaimStore>();
        services.AddSingleton(rentCharge.Object);
        services.AddSingleton<SimWorkerRegistry>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var worker = new SimWorkerCommandWorker(provider, NullLogger<SimWorkerCommandWorker>.Instance);

        // 3. One cycle claims + runs + writes the result.
        var processed = await worker.ProcessOldestPendingAsync(scope.ServiceProvider, CancellationToken.None);
        processed.Should().Be(1);

        // 4. The row is Done with {created:3} and a completion stamp.
        await using (var verify = NewContext(_conn))
        {
            var row = await verify.SimWorkerCommands.AsNoTracking().SingleAsync(c => c.Id == id);
            row.Status.Should().Be(SimWorkerCommandStatus.Done);
            row.ResultJson.Should().NotBeNull();
            using var result = JsonDocument.Parse(row.ResultJson!);
            result.RootElement.GetProperty("created").GetInt32().Should().Be(3);
            row.CompletedRealUtc.Should().NotBeNull();
            row.Error.Should().BeNull();
        }
        rentCharge.Verify(s => s.GenerateAsync(It.IsAny<CancellationToken>()), Times.Once);

        // 5. No Pending left → the next cycle is a no-op.
        (await worker.ProcessOldestPendingAsync(scope.ServiceProvider, CancellationToken.None)).Should().Be(0);
    }

    [SkippableFact]
    public async Task FailingService_MarksCommandError_WithMessage()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; command-bridge Postgres verification skipped.");

        var id = Guid.NewGuid();
        await using (var seed = NewContext(_conn))
        {
            seed.SimWorkerCommands.Add(new SimWorkerCommand
            {
                Id = id,
                WorkerKey = SimWorkerKeys.LateFee,
                RequestedSimUtc = DateTime.UtcNow,
                Status = SimWorkerCommandStatus.Pending,
                CreatedRealUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var lateFee = new Mock<ILateFeeService>();
        lateFee.Setup(s => s.AssessAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));

        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_conn).UseAtomicPersistenceKernel(provider));
        services.AddScoped<ISimWorkerCommandClaimStore, SimWorkerCommandClaimStore>();
        services.AddSingleton(lateFee.Object);
        services.AddSingleton<SimWorkerRegistry>();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var worker = new SimWorkerCommandWorker(provider, NullLogger<SimWorkerCommandWorker>.Instance);

        var processed = await worker.ProcessOldestPendingAsync(scope.ServiceProvider, CancellationToken.None);
        processed.Should().Be(1);

        await using var verify = NewContext(_conn);
        var row = await verify.SimWorkerCommands.AsNoTracking().SingleAsync(c => c.Id == id);
        row.Status.Should().Be(SimWorkerCommandStatus.Error);
        row.Error.Should().Contain("boom");
        row.CompletedRealUtc.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task AtomicEnqueue_ReplayReturnsSameCommandId_AndSingleQueueRow()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; command-bridge Postgres verification skipped.");

        await using var provider = CreateAtomicProvider();
        var access = await SeedAdministratorAccessAsync(provider);
        var commandId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var command = new EnqueueSimulationWorkerCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            commandId,
            SimWorkerKeys.RentCharge);
        var key = $"{access.PortfolioId}:{access.UserId}:worker-replay-proof";
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IWriteExecutor>();

        var first = await writes.ExecuteAsync(
            key, SimulationWriteSupport.Write<EnqueueSimulationWorkerCommand, EnqueueSimulationWorkerResult>(db, command));
        var replay = await writes.ExecuteAsync(
            key, SimulationWriteSupport.Write<EnqueueSimulationWorkerCommand, EnqueueSimulationWorkerResult>(db, command));

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.CommandId.Should().Be(first.Value.CommandId);
        replay.Value.CommandId.Should().Be(commandId);

        (await db.SimWorkerCommands.CountAsync(row => row.Id == commandId)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "simulation.worker.enqueue"
            && receipt.IdempotencyKey == key)).Should().Be(1);
    }

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    private ServiceProvider CreateAtomicProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_conn)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task<ActiveAccessContext> SeedAdministratorAccessAsync(ServiceProvider provider)
    {
        var now = DateTime.UtcNow;
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var portfolio = new Portfolio
        {
            Name = $"Dev worker {Guid.NewGuid():N}",
            ManagementCompanyName = "Dev Worker Co",
            TimeZone = "America/New_York",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            UserName = $"dev-worker-{Guid.NewGuid():N}@example.test",
            Email = $"dev-worker-{Guid.NewGuid():N}@example.test",
            DisplayName = "Dev Worker Admin",
            CreatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();

        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolio.Id,
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
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(user, accessContext, membership, assignment, session);
        await db.SaveChangesAsync();

        return new ActiveAccessContext(
            session.Id,
            user.Id,
            accessContext.Id,
            portfolio.Id,
            accessContext.AccessRevision,
            accessContext.LastAuthorizedExperience,
            membership.Id,
            membership.DefaultExperience);
    }
}
