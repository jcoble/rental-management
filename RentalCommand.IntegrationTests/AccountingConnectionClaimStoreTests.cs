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
                .Should().HaveCount(4, "pull and refresh have separate ownership lanes");
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
    public async Task Stale_refresh_cannot_persist_rotated_tokens_after_reclaim()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Stale refresh");
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
                    .SetProperty(row => row.RefreshClaimExpiresAtUtc, now.AddMinutes(-1)));
        AccountingConnectionClaim current;
        await using (var reclaim = NewContext())
            current = (await new AccountingConnectionClaimStore(reclaim).ClaimTokenRefreshAsync(
                "current", now, now.AddMinutes(10), TimeSpan.FromMinutes(3), 1)).Single();

        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.Setup(x => x.RefreshTokenAsync(
                It.IsAny<AccountingAppSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountingTokenResult("rotated-access", "rotated-refresh", now.AddHours(1), null, null));

        await using var staleDb = NewContext();
        staleDb.Attach(stale.Connection);
        var tokenService = CreateTokenService(provider.Object);
        var act = () => tokenService.RefreshAsync(staleDb, stale.Connection, CancellationToken.None, stale.Fence);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var verify = NewContext();
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == stale.Connection.Id);
        saved.RefreshClaimToken.Should().Be(current.Fence.ClaimToken);
        _dataProtection.CreateProtector("RentalCommand.Accounting.v1")
            .Unprotect(saved.AccessTokenCipherText!).Should().Be("access-token");
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
        return new AccountingImportService(
            db, _dataProtection, providerResolver, settingsResolver,
            new AccountingTokenService(_dataProtection, providerResolver, settingsResolver,
                TimeProvider.System, NullLogger<AccountingTokenService>.Instance),
            TimeProvider.System, NullLogger<AccountingImportService>.Instance);
    }

    private AccountingTokenService CreateTokenService(IAccountingProvider provider)
    {
        var (providerResolver, settingsResolver) = CreateResolvers(provider);
        return new AccountingTokenService(
            _dataProtection, providerResolver, settingsResolver,
            TimeProvider.System, NullLogger<AccountingTokenService>.Instance);
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
