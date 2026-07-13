using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Data;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Scanning;
using RentalCommand.Data.Simulation;
using RentalCommand.Engine.Data;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for the scan and dev-simulation leased queue boundaries.</summary>
public sealed class ScanAndSimulationClaimStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
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
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Scan_claims_are_disjoint_and_stale_completion_is_fenced()
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

        var first = all[0];
        await using (var wrongOwnerDb = NewContext())
        {
            var wrongOwner = new ScanProcessingClaimStore(wrongOwnerDb);
            (await wrongOwner.MarkFailedAsync(
                first.Id, "not-the-owner", first.ClaimToken, now, "wrong owner"))
                .Should().Be(0);
        }
        await using (var expire = NewContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "ScanDrafts"
                SET "ProcessingClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = {{first.Id}}
                """);
        }
        await using (var expiredDb = NewContext())
        {
            var expired = new ScanProcessingClaimStore(expiredDb);
            (await expired.MarkFailedAsync(
                first.Id, first.ClaimOwner, first.ClaimToken, now, "expired"))
                .Should().Be(0);
        }
        await using var reclaimDb = NewContext();
        var replacement = (await new ScanProcessingClaimStore(reclaimDb)
            .ClaimAsync("scan-replacement", TimeSpan.FromMinutes(2), 1)).Single();
        replacement.Id.Should().Be(first.Id);
        replacement.ClaimToken.Should().NotBe(first.ClaimToken);

        await using var staleDb = NewContext();
        var staleStore = new ScanProcessingClaimStore(staleDb);
        (await staleStore.MarkFailedAsync(
            first.Id, first.ClaimOwner, first.ClaimToken, now, "stale"))
            .Should().Be(0);
        (await staleStore.MarkFailedAsync(
            replacement.Id, replacement.ClaimOwner, replacement.ClaimToken, now, "current"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Scan_claim_opens_through_ef_so_engine_rls_session_is_applied()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now);
        await SeedScansAsync(portfolioId, now, 1);

        var failClosedInterceptor = new RlsConnectionInterceptor(
            new HttpContextAccessor(),
            new RlsExecutionContext());
        await using (var failClosed = NewContext(rlsInterceptor: failClosedInterceptor))
        {
            (await new ScanProcessingClaimStore(failClosed)
                .ClaimAsync("no-engine-context", TimeSpan.FromMinutes(2), 1))
                .Should().BeEmpty();
        }

        await using var engineContext = NewContext(rlsInterceptor: new EngineRlsInterceptor());
        (await new ScanProcessingClaimStore(engineContext)
            .ClaimAsync("engine-context", TimeSpan.FromMinutes(2), 1))
            .Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Simulation_claim_is_exclusive_reclaimable_and_fenced()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.SimWorkerCommands.Add(new SimWorkerCommand
            {
                Id = id,
                WorkerKey = "run-due",
                RequestedSimUtc = now,
                Status = SimWorkerCommandStatus.Pending,
                CreatedRealUtc = now.AddMinutes(-1),
            });
            await seed.SaveChangesAsync();
        }

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var first = await new SimWorkerCommandClaimStore(dbA)
            .ClaimOldestAsync("sim-a", TimeSpan.FromMinutes(6));
        first.Should().NotBeNull();
        (await new SimWorkerCommandClaimStore(dbB)
            .ClaimOldestAsync("sim-b", TimeSpan.FromMinutes(6))).Should().BeNull();

        await using (var wrongOwnerDb = NewContext())
        {
            var wrongOwner = new SimWorkerCommandClaimStore(wrongOwnerDb);
            (await wrongOwner.MarkDoneAsync(
                id, "not-the-owner", first!.ClaimToken, "{}", now)).Should().Be(0);
        }

        await using (var expire = NewContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "SimWorkerCommands"
                SET "ClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = {{id}}
                """);
        }
        await using (var expiredDb = NewContext())
        {
            var expired = new SimWorkerCommandClaimStore(expiredDb);
            (await expired.MarkDoneAsync(
                id, first!.ClaimOwner, first.ClaimToken, "{}", now)).Should().Be(0);
        }
        await using var reclaimDb = NewContext();
        var replacement = await new SimWorkerCommandClaimStore(reclaimDb)
            .ClaimOldestAsync("sim-b", TimeSpan.FromMinutes(6));
        replacement.Should().NotBeNull();
        replacement!.ClaimToken.Should().NotBe(first!.ClaimToken);

        await using var completeDb = NewContext();
        var completion = new SimWorkerCommandClaimStore(completeDb);
        (await completion.MarkDoneAsync(
            id, first!.ClaimOwner, first.ClaimToken, "{}", now)).Should().Be(0);
        (await completion.MarkDoneAsync(
            id, replacement!.ClaimOwner, replacement.ClaimToken, "{\"created\":1}", now)).Should().Be(1);
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
}
