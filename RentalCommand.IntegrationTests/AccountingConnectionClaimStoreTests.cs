using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for bounded accounting worker claims and ownership fencing.</summary>
public sealed class AccountingConnectionClaimStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

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
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Pull_claims_are_bounded_disjoint_reclaimable_and_fenced()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedConnectionsAsync(now, 4);

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var claimed = await Task.WhenAll(
            new AccountingConnectionClaimStore(dbA).ClaimPullAsync(
                "pull-a", now, TimeSpan.FromMinutes(12), 2),
            new AccountingConnectionClaimStore(dbB).ClaimPullAsync(
                "pull-b", now, TimeSpan.FromMinutes(12), 2));

        var all = claimed.SelectMany(rows => rows).ToArray();
        all.Should().HaveCount(4);
        all.Select(row => row.Connection.Id).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(row => row.Connection.PullAttemptCount == 1);
        dbA.Database.CurrentTransaction.Should().BeNull();
        dbB.Database.CurrentTransaction.Should().BeNull();

        await using (var activePullDb = NewContext())
        {
            (await new AccountingConnectionClaimStore(activePullDb)
                    .ClaimPullAsync("cannot-steal", now, TimeSpan.FromMinutes(12), 10))
                .Should().BeEmpty("active pull leases cannot be stolen");
        }

        var first = all[0];
        await using (var activeRefreshDb = NewContext())
        {
            (await new AccountingConnectionClaimStore(activeRefreshDb).ClaimTokenRefreshAsync(
                    "refresh", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 10))
                .Should().HaveCount(4, "pull work and token rotation have separate ownership lanes");
        }

        await using (var expire = NewContext())
        {
            await expire.AccountingConnections
                .Where(row => row.Id == first.Connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.PullClaimExpiresAtUtc, now.AddSeconds(-1)));
        }

        await using var reclaimDb = NewContext();
        var replacement = (await new AccountingConnectionClaimStore(reclaimDb)
                .ClaimPullAsync("replacement", now, TimeSpan.FromMinutes(12), 1))
            .Single();
        replacement.Connection.Id.Should().Be(first.Connection.Id);
        replacement.Fence.ClaimToken.Should().NotBe(first.Fence.ClaimToken);

        await using var completeDb = NewContext();
        var completion = new AccountingConnectionClaimStore(completeDb);
        (await completion.MarkPullFailedAsync(
                first.Connection.Id, first.Fence.ClaimToken, now, now.AddMinutes(15), "stale"))
            .Should().Be(0);
        (await completion.MarkPullFailedAsync(
                replacement.Connection.Id, replacement.Fence.ClaimToken, now, now.AddMinutes(15), "current"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Claim_statements_apply_all_pull_and_refresh_eligibility_in_PostgreSQL()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var enabledPortfolioId = await SeedPortfolioAsync(now, "Enabled");
        var disabledPortfolioId = await SeedPortfolioAsync(now, "Disabled");

        await using (var seed = NewContext())
        {
            var disabled = Connection(disabledPortfolioId, now, pullEnabled: false);
            disabled.TokenExpiresAt = now.AddHours(1);
            seed.AccountingConnections.AddRange(
                Connection(enabledPortfolioId, now, pullEnabled: true),
                disabled);
            await seed.SaveChangesAsync();
        }

        await using (var pullDb = NewContext())
        {
            var pull = await new AccountingConnectionClaimStore(pullDb)
                .ClaimPullAsync("pull", now, TimeSpan.FromMinutes(12), 10);
            pull.Should().ContainSingle();
            pull.Single().Connection.PortfolioId.Should().Be(enabledPortfolioId);
            await new AccountingConnectionClaimStore(pullDb).MarkPullFailedAsync(
                pull.Single().Connection.Id, pull.Single().Fence.ClaimToken,
                now, now.AddHours(1), "test release");
        }

        await using (var refreshDb = NewContext())
        {
            var refresh = await new AccountingConnectionClaimStore(refreshDb)
                .ClaimTokenRefreshAsync("refresh", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 10);
            refresh.Should().ContainSingle();
            refresh.Single().Connection.PortfolioId.Should().Be(enabledPortfolioId);
        }
    }

    [SkippableFact]
    public async Task Stale_pull_cannot_persist_provider_results_after_reclaim()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Stale pull");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim stale;
        await using (var firstDb = NewContext())
            stale = (await new AccountingConnectionClaimStore(firstDb)
                .ClaimPullAsync("stale", now, TimeSpan.FromMinutes(1), 1)).Single();
        await using (var expire = NewContext())
            await expire.AccountingConnections.Where(row => row.Id == stale.Connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.PullClaimExpiresAtUtc, now.AddMinutes(-1)));
        await using (var reclaim = NewContext())
            _ = (await new AccountingConnectionClaimStore(reclaim)
                .ClaimPullAsync("current", now, TimeSpan.FromMinutes(10), 1)).Single();

        RentalCommandDbContext? providerCallDb = null;
        var providerCallSawTransaction = true;
        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.SetupGet(x => x.Capabilities).Returns(new AccountingCapabilities(
            true, false, false, false, false, false, false));
        provider.Setup(x => x.PullCustomersAsync(It.IsAny<AcctCallCtx>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback(() => providerCallSawTransaction = providerCallDb!.Database.CurrentTransaction is not null)
            .ReturnsAsync(new AccountingPullResult<ExtCustomerDto>(
                [new ExtCustomerDto("customer-1", "Stale Customer", true, now, null, null, "{}")], now, false));

        await using var staleDb = NewContext();
        providerCallDb = staleDb;
        staleDb.Attach(stale.Connection);
        var import = CreateImportService(staleDb, provider.Object);
        var act = () => import.ImportAsync(stale.Connection, null, CancellationToken.None, stale.Fence);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        providerCallSawTransaction.Should().BeFalse("provider I/O must finish before local persistence begins");

        await using var verify = NewContext();
        (await verify.AccountingEntityMappings.CountAsync()).Should().Be(0);
        (await verify.AccountingConnections.SingleAsync(row => row.Id == stale.Connection.Id))
            .PullClaimToken.Should().NotBe(stale.Fence.ClaimToken);
    }

    [SkippableFact]
    public async Task Disabling_pull_revokes_claim_and_blocks_in_flight_persistence()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Disable in flight");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb)
                .ClaimPullAsync("pull", now, TimeSpan.FromMinutes(5), 1)).Single();

        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.SetupGet(x => x.Capabilities).Returns(new AccountingCapabilities(
            true, false, false, false, false, false, false));
        provider.Setup(x => x.PullCustomersAsync(
                It.IsAny<AcctCallCtx>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var controlDb = NewContext();
                await CreateConnectionService(controlDb, provider.Object)
                    .SetPullEnabledAsync(portfolioId, AccountingProvider.QuickBooks, false, CancellationToken.None);
                return new AccountingPullResult<ExtCustomerDto>(
                    [new ExtCustomerDto("customer-disable", "Disabled", true, now, null, null, "{}")],
                    now, false);
            });

        await using var importDb = NewContext();
        importDb.Attach(claim.Connection);
        var act = () => CreateImportService(importDb, provider.Object)
            .ImportAsync(claim.Connection, null, CancellationToken.None, claim.Fence);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == claim.Connection.Id);
        saved.PullEnabled.Should().BeFalse();
        saved.PullClaimToken.Should().BeNull();
        (await verify.AccountingEntityMappings.CountAsync()).Should().Be(0);
    }

    [SkippableFact]
    public async Task Disconnect_revokes_rotation_and_blocks_provider_completion()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Disconnect rotation");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb).ClaimTokenRefreshAsync(
                "refresh", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 1)).Single();

        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.Setup(x => x.RevokeAsync(
                It.IsAny<AccountingAppSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        provider.Setup(x => x.RefreshTokenAsync(
                It.IsAny<AccountingAppSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var controlDb = NewContext();
                await CreateConnectionService(controlDb, provider.Object)
                    .DisconnectAsync(portfolioId, AccountingProvider.QuickBooks, CancellationToken.None);
                return new AccountingTokenResult(
                    "rotated-access", "rotated-refresh", now.AddHours(1), null, null);
            });

        await using var tokenDb = NewContext();
        var service = CreateTokenService(provider.Object, new AccountingConnectionClaimStore(tokenDb));
        var act = () => service.RefreshAsync(claim.Connection, claim.Fence, CancellationToken.None);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == claim.Connection.Id);
        saved.Status.Should().Be(AccountingConnectionStatus.Disconnected);
        saved.TokenRotationClaimToken.Should().BeNull();
        saved.AccessTokenCipherText.Should().BeNull();
        saved.RefreshTokenCipherText.Should().BeNull();
    }

    [SkippableFact]
    public async Task Scheduled_and_inline_refresh_share_one_durable_rotation_lane()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Shared rotation");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim pull;
        await using (var pullDb = NewContext())
            pull = (await new AccountingConnectionClaimStore(pullDb)
                .ClaimPullAsync("pull", now, TimeSpan.FromMinutes(5), 1)).Single();

        await using var scheduledDb = NewContext();
        await using var inlineDb = NewContext();
        var scheduledTask = new AccountingConnectionClaimStore(scheduledDb).ClaimTokenRefreshAsync(
            "scheduled", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 1);
        var inlineTask = new AccountingConnectionClaimStore(inlineDb).ClaimInlineTokenRotationAsync(
            pull.Connection.Id, pull.Fence.ClaimToken, "inline", now, TimeSpan.FromMinutes(3));
        await Task.WhenAll(scheduledTask, inlineTask);

        var winners = scheduledTask.Result.Count + (inlineTask.Result is null ? 0 : 1);
        winners.Should().Be(1, "the same rotating refresh token may be submitted only once");

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == pull.Connection.Id);
        saved.TokenRotationState.Should().Be(AccountingTokenRotationState.InFlight);
        saved.TokenRotationAttemptCount.Should().Be(1);
    }

    [SkippableFact]
    public async Task Expired_rotation_is_not_retried_and_reconciles_to_reconnect()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Abandoned rotation");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim stale;
        await using (var firstDb = NewContext())
            stale = (await new AccountingConnectionClaimStore(firstDb).ClaimTokenRefreshAsync(
                "stale", now, now.AddMinutes(10), TimeSpan.FromMinutes(1), 1)).Single();
        await using (var expire = NewContext())
            await expire.AccountingConnections.Where(row => row.Id == stale.Connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.TokenRotationClaimExpiresAtUtc, now.AddMinutes(-1)));
        await using (var retry = NewContext())
            (await new AccountingConnectionClaimStore(retry).ClaimTokenRefreshAsync(
                "must-not-retry", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 1))
                .Should().BeEmpty();

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == stale.Connection.Id);
        saved.Status.Should().Be(AccountingConnectionStatus.NeedsReconnect);
        saved.TokenRotationState.Should().Be(AccountingTokenRotationState.RecoveryRequired);
        saved.RefreshTokenCipherText.Should().BeNull();
    }

    [SkippableFact]
    public async Task Provider_success_with_local_completion_failure_enters_recovery_state()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Persistence loss");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        await using var db = NewContext();
        var durable = new AccountingConnectionClaimStore(db);
        var claim = (await durable.ClaimTokenRefreshAsync(
            "refresh", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 1)).Single();

        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.Setup(x => x.RefreshTokenAsync(
                It.IsAny<AccountingAppSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountingTokenResult("rotated-access", "rotated-refresh", now.AddHours(1), null, null));

        var tokenService = CreateTokenService(provider.Object, new FailCompletionClaimStore(durable));
        var result = await tokenService.RefreshAsync(claim.Connection, claim.Fence, CancellationToken.None);
        result.Outcome.Should().Be(AccountingTokenService.RefreshOutcome.NeedsReconnect);

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == claim.Connection.Id);
        saved.Status.Should().Be(AccountingConnectionStatus.NeedsReconnect);
        saved.TokenRotationState.Should().Be(AccountingTokenRotationState.RecoveryRequired);
        saved.AccessTokenCipherText.Should().BeNull();
        saved.RefreshTokenCipherText.Should().BeNull();
    }

    private async Task<int> SeedPortfolioAsync(DateTime now, string? suffix = null)
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Name = $"Accounting claims {suffix}",
            ManagementCompanyName = $"Accounting claims {suffix}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        return portfolio.Id;
    }

    private async Task SeedConnectionsAsync(DateTime now, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var portfolioId = await SeedPortfolioAsync(now, index.ToString());
            await using var db = NewContext();
            db.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await db.SaveChangesAsync();
        }
    }

    private AccountingConnection Connection(
        int portfolioId,
        DateTime now,
        bool pullEnabled) => PrepareTokens(new AccountingConnection
        {
            PortfolioId = portfolioId,
            Provider = AccountingProvider.QuickBooks,
            Status = AccountingConnectionStatus.Connected,
            PullEnabled = pullEnabled,
            NextPullAtUtc = now,
            ExternalAccountId = "realm-test",
            TokenExpiresAt = now.AddMinutes(5),
            CreatedAt = now,
            UpdatedAt = now,
        });

    private AccountingConnection PrepareTokens(AccountingConnection connection)
    {
        var protector = _dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        connection.AccessTokenCipherText = protector.Protect("access-token");
        connection.RefreshTokenCipherText = protector.Protect("refresh-token");
        return connection;
    }

    private AccountingImportService CreateImportService(RentalCommandDbContext db, IAccountingProvider provider)
    {
        var (providerResolver, settingsResolver) = CreateResolvers(provider);
        var claims = new AccountingConnectionClaimStore(db);
        return new AccountingImportService(
            db, _dataProtection, providerResolver, settingsResolver,
            new AccountingTokenService(_dataProtection, providerResolver, settingsResolver,
                claims, TimeProvider.System, NullLogger<AccountingTokenService>.Instance),
            claims,
            TimeProvider.System, NullLogger<AccountingImportService>.Instance);
    }

    private AccountingTokenService CreateTokenService(
        IAccountingProvider provider, IAccountingConnectionClaimStore claims)
    {
        var (providerResolver, settingsResolver) = CreateResolvers(provider);
        return new AccountingTokenService(
            _dataProtection, providerResolver, settingsResolver,
            claims, TimeProvider.System, NullLogger<AccountingTokenService>.Instance);
    }

    private AccountingConnectionService CreateConnectionService(
        RentalCommandDbContext db, IAccountingProvider provider)
    {
        var (providerResolver, settingsResolver) = CreateResolvers(provider);
        var claims = new AccountingConnectionClaimStore(db);
        var tokenService = new AccountingTokenService(
            _dataProtection, providerResolver, settingsResolver, claims,
            TimeProvider.System, NullLogger<AccountingTokenService>.Instance);
        var import = new AccountingImportService(
            db, _dataProtection, providerResolver, settingsResolver, tokenService, claims,
            TimeProvider.System, NullLogger<AccountingImportService>.Instance);
        return new AccountingConnectionService(
            db, _dataProtection, providerResolver, settingsResolver, import,
            TimeProvider.System, null!, NullLogger<AccountingConnectionService>.Instance);
    }

    private sealed class FailCompletionClaimStore(IAccountingConnectionClaimStore inner)
        : IAccountingConnectionClaimStore
    {
        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
            string owner, DateTime now, TimeSpan duration, int size, CancellationToken ct = default) =>
            inner.ClaimPullAsync(owner, now, duration, size, ct);
        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
            string owner, DateTime now, DateTime before, TimeSpan duration, int size, CancellationToken ct = default) =>
            inner.ClaimTokenRefreshAsync(owner, now, before, duration, size, ct);
        public Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
            int id, Guid? pullToken, string owner, DateTime now, TimeSpan duration, CancellationToken ct = default) =>
            inner.ClaimInlineTokenRotationAsync(id, pullToken, owner, now, duration, ct);
        public Task<int> MarkPullFailedAsync(
            int id, Guid token, DateTime failed, DateTime next, string error, CancellationToken ct = default) =>
            inner.MarkPullFailedAsync(id, token, failed, next, error, ct);
        public Task<int> MarkTokenRotationRecoveryRequiredAsync(
            int id, Guid token, DateTime failed, string error, CancellationToken ct = default) =>
            inner.MarkTokenRotationRecoveryRequiredAsync(id, token, failed, error, ct);
        public Task<int> CompleteTokenRotationAsync(
            int id, Guid token, Guid? parentPullToken, string access, string refresh,
            DateTime expires, DateTime completed,
            CancellationToken ct = default) => throw new DbUpdateException("Injected local persistence loss.");
        public Task<int> ReconcileAbandonedTokenRotationsAsync(DateTime now, CancellationToken ct = default) =>
            inner.ReconcileAbandonedTokenRotationsAsync(now, ct);
    }

    private static (AccountingProviderResolver Provider, AccountingAppSettingsResolver Settings) CreateResolvers(
        IAccountingProvider provider)
    {
        var settings = new AccountingAppSettingsResolver(new StaticOptionsMonitor<QuickBooksOptions>(new QuickBooksOptions
        {
            ClientId = "client", ClientSecret = "secret", Environment = "sandbox",
        }));
        return (new AccountingProviderResolver([provider]), settings);
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
