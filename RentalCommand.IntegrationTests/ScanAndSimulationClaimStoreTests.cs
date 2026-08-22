using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Text.Json;
using RentalCommand.Api.Data;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Scanning;
using RentalCommand.Data.Security;
using RentalCommand.Data.Simulation;
using RentalCommand.Engine.Data;
using RentalCommand.Engine.Writes;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for the scan and dev-simulation leased queue boundaries.</summary>
public sealed class ScanAndSimulationClaimStoreTests : IAsyncLifetime
{
    private const string ApiPassword = SharedPostgreSqlDatabase.ApiPassword;
    private const string EnginePassword = SharedPostgreSqlDatabase.EnginePassword;

    private SharedPostgreSqlDatabase? _postgres;
    private string _connectionString = string.Empty;
    private string _engineConnectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new SharedPostgreSqlDatabase(SharedPostgreSqlSchema.Migrated);
            await _postgres.StartAsync();
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _dockerAvailable = true;
        _connectionString = _postgres.GetConnectionString();

        // Once the container is running, migration/schema failures are product failures and must
        // fail the suite rather than being disguised as an unavailable Docker daemon.
        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var apiConnectionString = RuntimeConnectionString(DatabaseRuntimeIdentity.ApiRole, ApiPassword);
        _engineConnectionString = RuntimeConnectionString(
            DatabaseRuntimeIdentity.EngineRole,
            EnginePassword);
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Scan_claims_are_disjoint()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 8);

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var claimed = await Task.WhenAll(
            new ScanProcessingClaimStore(dbA).ClaimAsync("scan-a", TimeSpan.FromMinutes(2), 8),
            new ScanProcessingClaimStore(dbB).ClaimAsync("scan-b", TimeSpan.FromMinutes(2), 8));

        var all = claimed.SelectMany(rows => rows).ToArray();
        all.Should().HaveCount(8);
        all.Select(row => row.Id).Should().OnlyHaveUniqueItems();

    }

    [SkippableFact]
    public async Task Scan_terminal_write_executes_through_job_step_executor_and_records_rule_body_lock()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);
        await using var claimDb = NewContext();
        var claim = (await new ScanProcessingClaimStore(claimDb)
            .ClaimAsync("scan-executor", TimeSpan.FromMinutes(2), 1)).Single();
        var command = ReviewingCommand(claim, now);
        var locks = new LockRecorder();

        var outcome = await ExecuteTerminalAsync(command, locks);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        outcome.Value.Should().Be(new ScanProcessingTerminalResult(true, claim.Id, "Reviewing"));
        locks.Commands.Should().Contain(sql => sql.Contains("pg_advisory_xact_lock", StringComparison.Ordinal));
        await using var verify = NewContext();
        var draft = await verify.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == claim.Id);
        draft.Status.Should().Be("Reviewing");
        JsonSerializer.Deserialize<JsonElement>(draft.ExtractedFields!)
            .GetProperty("amount").GetProperty("value").GetInt32().Should().Be(12);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scan-processing.terminal"
            && row.CommandIdempotencyKey == ScanProcessingTerminalWrite.StepKey(command))).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == $"scan-draft-update:{ScanProcessingTerminalWrite.StepKey(command)}"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Scan_terminal_write_wrong_or_expired_token_fails_without_mutating()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);
        await using var claimDb = NewContext();
        var claim = (await new ScanProcessingClaimStore(claimDb)
            .ClaimAsync("scan-fence", TimeSpan.FromMinutes(2), 1)).Single();
        var wrong = FailedCommand(claim with { ClaimToken = Guid.NewGuid() }, now, "wrong token");

        (await ExecuteTerminalAsync(wrong)).Value.Applied.Should().BeFalse();
        await using (var expire = NewContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ScanDrafts"
                SET "ProcessingClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = {{claim.Id}}
                """);
        }
        var expired = FailedCommand(claim, now, "expired");
        (await ExecuteTerminalAsync(expired)).Value.Applied.Should().BeFalse();

        await using var verify = NewContext();
        var draft = await verify.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == claim.Id);
        draft.Status.Should().Be("Processing");
        draft.FailureReason.Should().BeNull();
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scan-processing.terminal"
            && (row.CommandIdempotencyKey == ScanProcessingTerminalWrite.StepKey(wrong)
                || row.CommandIdempotencyKey == ScanProcessingTerminalWrite.StepKey(expired))))
            .Should().Be(0);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == $"scan-draft-update:{ScanProcessingTerminalWrite.StepKey(wrong)}"
            || row.IdempotencyKey == $"scan-draft-update:{ScanProcessingTerminalWrite.StepKey(expired)}"))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task Scan_terminal_write_exact_retry_replays_without_duplicate_audit_or_outbox()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);
        await using var claimDb = NewContext();
        var claim = (await new ScanProcessingClaimStore(claimDb)
            .ClaimAsync("scan-replay", TimeSpan.FromMinutes(2), 1)).Single();
        var command = FailedCommand(claim, now, "provider failed");

        var first = await ExecuteTerminalAsync(command);
        var replay = await ExecuteTerminalAsync(command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = NewContext();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == "scan-processing.terminal"
            && row.IdempotencyKey == ScanProcessingTerminalWrite.StepKey(command))).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scan-processing.terminal"
            && row.CommandIdempotencyKey == ScanProcessingTerminalWrite.StepKey(command))).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == $"scan-draft-update:{ScanProcessingTerminalWrite.StepKey(command)}"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Scan_terminal_write_reclaim_uses_new_receipt_and_preserves_dead_attempt_receipt()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);
        await using var firstClaimDb = NewContext();
        var first = (await new ScanProcessingClaimStore(firstClaimDb)
            .ClaimAsync("scan-dead", TimeSpan.FromMinutes(2), 1)).Single();
        await using (var expire = NewContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ScanDrafts"
                SET "ProcessingClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = {{first.Id}}
                """);
        }
        var deadCommand = FailedCommand(first, now, "dead attempt");
        (await ExecuteTerminalAsync(deadCommand)).Value.Applied.Should().BeFalse();
        await using var replacementClaimDb = NewContext();
        var replacement = (await new ScanProcessingClaimStore(replacementClaimDb)
            .ClaimAsync("scan-replacement", TimeSpan.FromMinutes(2), 1)).Single();
        var replacementCommand = FailedCommand(replacement, now, "current attempt");

        ScanProcessingTerminalWrite.StepKey(replacementCommand).Should()
            .NotBe(ScanProcessingTerminalWrite.StepKey(deadCommand));
        (await ExecuteTerminalAsync(replacementCommand)).Value.Applied.Should().BeTrue();

        await using var verify = NewContext();
        var receipts = await verify.AtomicCommandReceipts.AsNoTracking()
            .Where(row => row.CommandType == "scan-processing.terminal"
                && (row.IdempotencyKey == ScanProcessingTerminalWrite.StepKey(deadCommand)
                    || row.IdempotencyKey == ScanProcessingTerminalWrite.StepKey(replacementCommand)))
            .OrderBy(row => row.IdempotencyKey)
            .ToListAsync();
        receipts.Should().HaveCount(2);
        JsonSerializer.Deserialize<JsonElement>(receipts.Single(row =>
                row.IdempotencyKey == ScanProcessingTerminalWrite.StepKey(deadCommand)).ResultJson!)
            .GetProperty("Applied").GetBoolean().Should().BeFalse();
        JsonSerializer.Deserialize<JsonElement>(receipts.Single(row =>
                row.IdempotencyKey == ScanProcessingTerminalWrite.StepKey(replacementCommand)).ResultJson!)
            .GetProperty("Applied").GetBoolean().Should().BeTrue();
        (await verify.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == "scan-processing.terminal"
            && row.CommandIdempotencyKey == ScanProcessingTerminalWrite.StepKey(replacementCommand))).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey == $"scan-draft-update:{ScanProcessingTerminalWrite.StepKey(replacementCommand)}"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Scan_claim_opens_through_ef_so_engine_rls_session_is_applied()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);

        var failClosedInterceptor = new RlsConnectionInterceptor(new HttpContextAccessor());
        await using (var failClosed = NewContext(rlsInterceptor: failClosedInterceptor))
        {
            var act = async () => await new ScanProcessingClaimStore(failClosed)
                .ClaimAsync("owner-cannot-masquerade-as-api", TimeSpan.FromMinutes(2), 1);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*restricted direct-login role 'rentalcommand_api'*");
        }

        await using var engineContext = NewContext(
            _engineConnectionString,
            new EngineRlsInterceptor());
        (await new ScanProcessingClaimStore(engineContext)
            .ClaimAsync("engine-context", TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Simulation_terminal_write_records_rule_body_lock()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var id = await SeedSimulationCommandAsync(now);
        await using var claimDb = NewContext();
        var claim = (await new SimWorkerCommandClaimStore(claimDb)
            .ClaimOldestAsync("sim-lock", TimeSpan.FromMinutes(6)))!;
        var command = DoneCommand(claim, now, "{\"created\":1}");
        var locks = new LockRecorder();

        var outcome = await ExecuteSimTerminalAsync(command, locks);

        outcome.Value.Should().Be(new SimWorkerTerminalResult(true, id, SimWorkerCommandStatus.Done));
        locks.Commands.Should().Contain(sql => sql.Contains("pg_advisory_xact_lock", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Simulation_terminal_write_wrong_or_expired_token_fails_without_mutating()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var id = await SeedSimulationCommandAsync(now);
        await using var claimDb = NewContext();
        var claim = (await new SimWorkerCommandClaimStore(claimDb)
            .ClaimOldestAsync("sim-fence", TimeSpan.FromMinutes(6)))!;

        var wrong = ErrorCommand(claim with { ClaimToken = Guid.NewGuid() }, now, "wrong token");
        (await ExecuteSimTerminalAsync(wrong)).Value.Applied.Should().BeFalse();
        await ExpireSimulationClaimAsync(id);
        var expired = ErrorCommand(claim, now, "expired");
        (await ExecuteSimTerminalAsync(expired)).Value.Applied.Should().BeFalse();

        await using var verify = NewContext();
        var row = await verify.SimWorkerCommands.AsNoTracking().SingleAsync(item => item.Id == id);
        row.Status.Should().Be(SimWorkerCommandStatus.Running);
        row.ResultJson.Should().BeNull();
        row.Error.Should().BeNull();
    }

    [SkippableFact]
    public async Task Simulation_terminal_write_exact_retry_replays_without_duplicate_receipt()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedSimulationCommandAsync(now);
        await using var claimDb = NewContext();
        var claim = (await new SimWorkerCommandClaimStore(claimDb)
            .ClaimOldestAsync("sim-replay", TimeSpan.FromMinutes(6)))!;
        var command = DoneCommand(claim, now, "{\"created\":3}");

        var first = await ExecuteSimTerminalAsync(command);
        var replay = await ExecuteSimTerminalAsync(command);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = NewContext();
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == "sim-worker.terminal"
            && row.IdempotencyKey == SimWorkerTerminalWrite.StepKey(command))).Should().Be(1);
        (await verify.SimWorkerCommands.CountAsync(row =>
            row.Id == claim.Id && row.Status == SimWorkerCommandStatus.Done)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Simulation_terminal_write_reclaim_uses_new_receipt_and_preserves_dead_attempt_receipt()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var id = await SeedSimulationCommandAsync(now);
        await using var firstClaimDb = NewContext();
        var first = (await new SimWorkerCommandClaimStore(firstClaimDb)
            .ClaimOldestAsync("sim-dead", TimeSpan.FromMinutes(6)))!;
        await ExpireSimulationClaimAsync(id);
        var deadCommand = ErrorCommand(first, now, "dead attempt");
        (await ExecuteSimTerminalAsync(deadCommand)).Value.Applied.Should().BeFalse();

        await using var replacementClaimDb = NewContext();
        var replacement = (await new SimWorkerCommandClaimStore(replacementClaimDb)
            .ClaimOldestAsync("sim-replacement", TimeSpan.FromMinutes(6)))!;
        var replacementCommand = DoneCommand(replacement, now, "{\"created\":2}");
        SimWorkerTerminalWrite.StepKey(replacementCommand).Should()
            .NotBe(SimWorkerTerminalWrite.StepKey(deadCommand));
        (await ExecuteSimTerminalAsync(replacementCommand)).Value.Applied.Should().BeTrue();

        await using var verify = NewContext();
        var receipts = await verify.AtomicCommandReceipts.AsNoTracking()
            .Where(row => row.CommandType == "sim-worker.terminal"
                && (row.IdempotencyKey == SimWorkerTerminalWrite.StepKey(deadCommand)
                    || row.IdempotencyKey == SimWorkerTerminalWrite.StepKey(replacementCommand)))
            .ToListAsync();
        receipts.Should().HaveCount(2);
        JsonSerializer.Deserialize<JsonElement>(receipts.Single(row =>
                row.IdempotencyKey == SimWorkerTerminalWrite.StepKey(deadCommand)).ResultJson!)
            .GetProperty("Applied").GetBoolean().Should().BeFalse();
        JsonSerializer.Deserialize<JsonElement>(receipts.Single(row =>
                row.IdempotencyKey == SimWorkerTerminalWrite.StepKey(replacementCommand)).ResultJson!)
            .GetProperty("Applied").GetBoolean().Should().BeTrue();
    }

    [SkippableFact]
    public async Task Simulation_claim_is_exclusive_and_reclaimable()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var id = await SeedSimulationCommandAsync(now);

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var first = await new SimWorkerCommandClaimStore(dbA)
            .ClaimOldestAsync("sim-a", TimeSpan.FromMinutes(6));
        first.Should().NotBeNull();
        (await new SimWorkerCommandClaimStore(dbB)
            .ClaimOldestAsync("sim-b", TimeSpan.FromMinutes(6))).Should().BeNull();
        await ExpireSimulationClaimAsync(id);
        await using var reclaimDb = NewContext();
        var replacement = await new SimWorkerCommandClaimStore(reclaimDb)
            .ClaimOldestAsync("sim-b", TimeSpan.FromMinutes(6));
        replacement.Should().NotBeNull();
        replacement!.ClaimToken.Should().NotBe(first!.ClaimToken);
    }

    private async Task<int> SeedPortfolioAsync(DateTime now)
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Name = "Claims",
            ManagementCompanyName = "Claims",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        return portfolio.Id;
    }

    private async Task SeedScansAsync(int portfolioId, DateTime now, int count)
    {
        await using var db = NewContext();
        db.ScanDrafts.AddRange(Enumerable.Range(1, count).Select(index => new ScanDraft
        {
            PortfolioId = portfolioId,
            FilePath = $"scan/{index}",
            TargetEntityType = "Expense",
            Status = "Pending",
            CreatedAt = now.AddMinutes(-index),
        }));
        await db.SaveChangesAsync();
    }

    private RentalCommandDbContext NewContext(
        string? connectionString = null,
        DbConnectionInterceptor? rlsInterceptor = null)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connectionString ?? _connectionString);
        if (rlsInterceptor is not null)
        {
            options.AddInterceptors(rlsInterceptor);
        }

        return new RentalCommandDbContext(options.Options);
    }

    private async Task<AtomicCommandOutcome<ScanProcessingTerminalResult>> ExecuteTerminalAsync(
        ScanProcessingTerminalCommand command,
        DbCommandInterceptor? recorder = null)
    {
        await using var services = BuildExecutorServices(recorder);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>().ExecuteAsync(
            ScanProcessingTerminalWrite.StepKey(command),
            ScanProcessingTerminalWrite.Write(db, command));
    }

    private async Task<AtomicCommandOutcome<SimWorkerTerminalResult>> ExecuteSimTerminalAsync(
        SimWorkerTerminalCommand command,
        DbCommandInterceptor? recorder = null)
    {
        await using var services = BuildExecutorServices(recorder);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IJobStepWriteExecutor>().ExecuteAsync(
            SimWorkerTerminalWrite.StepKey(command),
            SimWorkerTerminalWrite.Write(db, command));
    }

    private async Task<Guid> SeedSimulationCommandAsync(DateTime now)
    {
        var id = Guid.NewGuid();
        await using var seed = NewContext();
        seed.SimWorkerCommands.Add(new SimWorkerCommand
        {
            Id = id,
            WorkerKey = "run-due",
            RequestedSimUtc = now,
            Status = SimWorkerCommandStatus.Pending,
            CreatedRealUtc = now.AddMinutes(-1),
        });
        await seed.SaveChangesAsync();
        return id;
    }

    private async Task ExpireSimulationClaimAsync(Guid id)
    {
        await using var expire = NewContext();
        await expire.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "SimWorkerCommands"
            SET "ClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
            WHERE "Id" = {{id}}
            """);
    }

    private static SimWorkerTerminalCommand DoneCommand(
        SimWorkerCommandClaim claim, DateTime now, string resultJson) => new(
        claim.Id, claim.ClaimOwner, claim.ClaimToken, SimWorkerCommandStatus.Done, now,
        ResultJson: resultJson);

    private static SimWorkerTerminalCommand ErrorCommand(
        SimWorkerCommandClaim claim, DateTime now, string error) => new(
        claim.Id, claim.ClaimOwner, claim.ClaimToken, SimWorkerCommandStatus.Error, now,
        Error: error);

    private ServiceProvider BuildExecutorServices(DbCommandInterceptor? recorder)
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IJobStepWriteExecutor, JobStepWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
        {
            options.UseNpgsql(_connectionString).UseAtomicPersistenceKernel(provider);
            if (recorder is not null) options.AddInterceptors(recorder);
        });
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ScanProcessingTerminalCommand ReviewingCommand(
        ScanProcessingClaim claim, DateTime now) => new(
        claim.Id, claim.PortfolioId, claim.ClaimOwner, claim.ClaimToken, "Reviewing", now,
        new ScanProcessingResult(
            "{\"amount\":{\"value\":12}}", "test-model", 10, 0.01m, "Expense", now));

    private static ScanProcessingTerminalCommand FailedCommand(
        ScanProcessingClaim claim, DateTime now, string reason) => new(
        claim.Id, claim.PortfolioId, claim.ClaimOwner, claim.ClaimToken, "Failed", now,
        FailureReason: reason);

    private sealed class LockRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:scan-terminal";
        public string? IpAddress => "127.0.0.1";
    }

    private string RuntimeConnectionString(string role, string password) =>
        new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        }.ConnectionString;
}
