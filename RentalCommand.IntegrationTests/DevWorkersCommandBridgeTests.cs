using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Simulation;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;
using Testcontainers.PostgreSql;
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
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _conn = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
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
        services.AddDbContext<RentalCommandDbContext>(o => o.UseNpgsql(_conn));
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
            row.ResultJson.Should().Contain("\"created\":3");
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
        services.AddDbContext<RentalCommandDbContext>(o => o.UseNpgsql(_conn));
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

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);
}
