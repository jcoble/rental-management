using FluentAssertions;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Auditing;
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
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateEffectiveNowUtc);
        await db.Database.ExecuteSqlRawAsync(LeaseEffectiveClockSql.CreateBusinessDate);
        await db.Database.ExecuteSqlRawAsync(TenantChargeBalanceViewSql.Create);
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
                "pull-a", TimeSpan.FromMinutes(12), 2),
            new AccountingConnectionClaimStore(dbB).ClaimPullAsync(
                "pull-b", TimeSpan.FromMinutes(12), 2));

        var all = claimed.SelectMany(rows => rows).ToArray();
        all.Should().HaveCount(4);
        all.Select(row => row.Connection.Id).Should().OnlyHaveUniqueItems();
        all.Select(row => row.Fence.ClaimToken).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(row => row.Connection.PullAttemptCount == 1);
        dbA.Database.CurrentTransaction.Should().BeNull();
        dbB.Database.CurrentTransaction.Should().BeNull();

        await using (var activePullDb = NewContext())
        {
            (await new AccountingConnectionClaimStore(activePullDb)
                    .ClaimPullAsync("cannot-steal", TimeSpan.FromMinutes(12), 10))
                .Should().BeEmpty("active pull leases cannot be stolen");
        }

        var first = all[0];
        await using (var activeRefreshDb = NewContext())
        {
            (await new AccountingConnectionClaimStore(activeRefreshDb).ClaimTokenRefreshAsync(
                    "refresh", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 10))
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
                .ClaimPullAsync("replacement", TimeSpan.FromMinutes(12), 1))
            .Single();
        replacement.Connection.Id.Should().Be(first.Connection.Id);
        replacement.Fence.ClaimToken.Should().NotBe(first.Fence.ClaimToken);

        await using var completeDb = NewContext();
        var completion = new AccountingConnectionClaimStore(completeDb);
        (await completion.MarkPullFailedAsync(
                first.Connection.Id, first.Fence.ClaimToken, TimeSpan.FromMinutes(15), "stale"))
            .Should().Be(0);
        (await completion.MarkPullFailedAsync(
                replacement.Connection.Id, replacement.Fence.ClaimToken, TimeSpan.FromMinutes(15), "current"))
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
                .ClaimPullAsync("pull", TimeSpan.FromMinutes(12), 10);
            pull.Should().ContainSingle();
            pull.Single().Connection.PortfolioId.Should().Be(enabledPortfolioId);
            await new AccountingConnectionClaimStore(pullDb).MarkPullFailedAsync(
                pull.Single().Connection.Id, pull.Single().Fence.ClaimToken,
                TimeSpan.FromHours(1), "test release");
        }

        await using (var refreshDb = NewContext())
        {
            var refresh = await new AccountingConnectionClaimStore(refreshDb)
                .ClaimTokenRefreshAsync("refresh", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 10);
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
                .ClaimPullAsync("stale", TimeSpan.FromMinutes(1), 1)).Single();
        await using (var expire = NewContext())
            await expire.AccountingConnections.Where(row => row.Id == stale.Connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.PullClaimExpiresAtUtc, now.AddMinutes(-1)));
        await using (var reclaim = NewContext())
            _ = (await new AccountingConnectionClaimStore(reclaim)
                .ClaimPullAsync("current", TimeSpan.FromMinutes(10), 1)).Single();

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
    public async Task Atomic_pull_deduplicates_replays_advances_cursor_and_releases_claim()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Atomic apply");
        await using (var seed = NewContext())
        {
            var actor = new ApplicationUser
            {
                UserName = $"accounting-pull-{portfolioId}@rentalcommand.local",
                NormalizedUserName = $"ACCOUNTING-PULL-{portfolioId}@RENTALCOMMAND.LOCAL",
                Email = $"accounting-pull-{portfolioId}@rentalcommand.local",
                NormalizedEmail = $"ACCOUNTING-PULL-{portfolioId}@RENTALCOMMAND.LOCAL",
                DisplayName = "Accounting pull actor",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
            };
            var property = new Property
            {
                PortfolioId = portfolioId, Name = "Maple", AddressLine1 = "1 Main",
                City = "Columbus", State = "OH", PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
            };
            seed.AddRange(actor, property);
            await seed.SaveChangesAsync();
            var unit = new Unit
            {
                PortfolioId = portfolioId, PropertyId = property.Id, UnitNumber = "1", Bedrooms = 1, Bathrooms = 1,
                MarketRent = 1200, CreatedAt = now, UpdatedAt = now,
            };
            var tenant = new Tenant
            {
                PortfolioId = portfolioId, FirstName = "Jordan", LastName = "Rivera",
                CreatedAt = now, UpdatedAt = now,
            };
            seed.AddRange(unit, tenant);
            await seed.SaveChangesAsync();
            var connection = Connection(portfolioId, now, pullEnabled: true);
            seed.AccountingConnections.Add(connection);
            var management = new LeaseManagement
            {
                PortfolioId = portfolioId, PropertyId = property.Id, UnitId = unit.Id,
                RelationshipNumber = "LM-atomic", PlannedPossessionAtUtc = now.AddMonths(-1),
                CreatedAtUtc = now, CreatedByUserId = actor.Id, UpdatedAtUtc = now,
                RowVersion = Guid.NewGuid(),
            };
            seed.LeaseManagements.Add(management);
            await seed.SaveChangesAsync();
            seed.LeaseManagementParties.Add(new LeaseManagementParty
            {
                PortfolioId = portfolioId,
                LeaseManagementId = management.Id,
                TenantId = tenant.Id,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
                ChangeReason = "Accounting pull test",
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            });
            var account = new TenantAccount
            {
                PortfolioId = portfolioId,
                LeaseManagementId = management.Id,
                AccountNumber = "TA-atomic",
                Currency = "USD",
                OpenedAtUtc = now.AddMonths(-1),
                CreatedAtUtc = now,
                CreatedByUserId = actor.Id,
            };
            seed.TenantAccounts.Add(account);
            await seed.SaveChangesAsync();
            seed.TenantLedgerEntries.Add(new TenantLedgerEntry
            {
                PortfolioId = portfolioId,
                TenantAccountId = account.Id,
                EntryType = TenantLedgerEntryType.ManualCharge,
                Direction = TenantLedgerDirection.Debit,
                Amount = 1200m,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(now.AddDays(-5)),
                DueOn = DateOnly.FromDateTime(now.AddDays(-5)),
                PostedAtUtc = now,
                Description = "Test rent charge",
                BusinessKey = "test:accounting-pull-charge",
                CreatedByUserId = actor.Id,
            });
            seed.AccountingEntityMappings.Add(new AccountingEntityMapping
            {
                PortfolioId = portfolioId, AccountingConnectionId = connection.Id,
                ExternalType = ExternalKind.Customer, ExternalId = "customer-atomic",
                ExternalDisplayName = "Jordan Rivera", LocalEntityType = LocalEntityKind.Tenant,
                LocalEntityId = tenant.Id, ConfirmedAt = now, Confidence = 1,
                CreatedAt = now, UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb)
                .ClaimPullAsync("atomic", TimeSpan.FromMinutes(5), 1)).Single();

        var provider = new Mock<IAccountingProvider>();
        provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
        provider.SetupGet(x => x.Capabilities).Returns(new AccountingCapabilities(
            false, false, false, true, false, false, false));
        var paidAt = now.AddDays(-1);
        provider.Setup(x => x.PullPaymentsAsync(
                It.IsAny<AcctCallCtx>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountingPullResult<ExtPaymentDto>(
                [
                    new("payment-atomic", "customer-atomic", 1200, paidAt, "ACH", "first", paidAt, null, null, "{}"),
                    new("payment-atomic", "customer-atomic", 1200, paidAt, "ACH", "last", paidAt, null, null, "{}"),
                ], paidAt, false));

        await using var importDb = NewContext();
        var applyCommands = new List<string>();
        var service = CreateImportService(importDb, provider.Object, applyCommands);
        var first = await service.ImportAsync(claim.Connection, null, CancellationToken.None, claim.Fence);
        var replay = await service.ImportAsync(claim.Connection, null, CancellationToken.None, claim.Fence);
        first.Should().Be(replay);
        first.PaymentsImported.Should().Be(1);

        await using var verify = NewContext();
        (await verify.TenantLedgerEntries.CountAsync(row => row.PortfolioId == portfolioId
            && row.EntryType == TenantLedgerEntryType.PaymentReceipt)).Should().Be(1);
        (await verify.TenantPaymentAttempts.CountAsync(row => row.PortfolioId == portfolioId
            && row.State == TenantPaymentAttemptState.Succeeded)).Should().Be(1);
        (await verify.TenantLedgerAllocations.CountAsync(row => row.PortfolioId == portfolioId))
            .Should().Be(1);
        (await verify.AccountingSyncMaps.CountAsync(row => row.AccountingConnectionId == claim.Connection.Id))
            .Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(row => row.CommandType == "accounting.pull.apply"))
            .Should().Be(1);
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == claim.Connection.Id);
        saved.PullClaimToken.Should().BeNull();
        saved.LastPulledAtJson.Should().Contain("payments");
        saved.LastSyncedAt.Should().NotBeNull();
        applyCommands.Count(sql => sql.Contains("payment_decisions AS MATERIALIZED", StringComparison.Ordinal))
            .Should().Be(1, "the bounded payload must be mapped, ranked, deduplicated, and written by one SQL statement");
        applyCommands.Single(sql => sql.Contains("payment_decisions AS MATERIALIZED", StringComparison.Ordinal))
            .Should().Contain("jsonb_array_elements").And.Contain("TenantLedgerEntries")
            .And.Contain("TenantLedgerAllocations").And.Contain("ON CONFLICT")
            .And.NotContain("INSERT INTO \"Payments\"");
    }

    [SkippableFact]
    public async Task Atomic_pull_injected_failure_rolls_back_receipt_mapping_cursor_and_claim_release()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Atomic rollback");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }
        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb)
                .ClaimPullAsync("rollback", TimeSpan.FromMinutes(5), 1)).Single();

        await using (var control = NewContext())
            await control.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION fail_accounting_apply() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected accounting rollback'; END $$;
                CREATE TRIGGER fail_accounting_apply_trigger BEFORE INSERT ON "AccountingEntityMappings"
                FOR EACH ROW EXECUTE FUNCTION fail_accounting_apply();
                """);
        try
        {
            var provider = new Mock<IAccountingProvider>();
            provider.SetupGet(x => x.Provider).Returns(AccountingProvider.QuickBooks);
            provider.SetupGet(x => x.Capabilities).Returns(new AccountingCapabilities(
                true, false, false, false, false, false, false));
            provider.Setup(x => x.PullCustomersAsync(
                    It.IsAny<AcctCallCtx>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AccountingPullResult<ExtCustomerDto>(
                    [new("rollback-customer", "Rollback", true, now, null, null, "{}")], now, false));
            await using var importDb = NewContext();
            var act = () => CreateImportService(importDb, provider.Object)
                .ImportAsync(claim.Connection, null, CancellationToken.None, claim.Fence);
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            await using var cleanup = NewContext();
            await cleanup.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS fail_accounting_apply_trigger ON "AccountingEntityMappings";
                DROP FUNCTION IF EXISTS fail_accounting_apply();
                """);
        }

        await using var verify = NewContext();
        (await verify.AccountingEntityMappings.CountAsync(row => row.AccountingConnectionId == claim.Connection.Id))
            .Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(row => row.CommandType == "accounting.pull.apply"))
            .Should().Be(0);
        var saved = await verify.AccountingConnections.SingleAsync(row => row.Id == claim.Connection.Id);
        saved.PullClaimToken.Should().Be(claim.Fence.ClaimToken);
        saved.LastPulledAtJson.Should().BeNull();
    }

    [SkippableFact]
    public async Task Disabling_pull_revokes_claim_and_blocks_in_flight_persistence()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var portfolioId = await SeedPortfolioAsync(now, "Disable in flight");
        var scope = await SeedAdministratorScopeAsync(portfolioId, now, "disable-in-flight");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb)
                .ClaimPullAsync("pull", TimeSpan.FromMinutes(5), 1)).Single();

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
                    .SetPullEnabledAsync(
                        scope,
                        AccountingProvider.QuickBooks,
                        false,
                        "disable-in-flight",
                        CancellationToken.None);
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
        var scope = await SeedAdministratorScopeAsync(portfolioId, now, "disconnect-rotation");
        await using (var seed = NewContext())
        {
            seed.AccountingConnections.Add(Connection(portfolioId, now, pullEnabled: true));
            await seed.SaveChangesAsync();
        }

        AccountingConnectionClaim claim;
        await using (var claimDb = NewContext())
            claim = (await new AccountingConnectionClaimStore(claimDb).ClaimTokenRefreshAsync(
                "refresh", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 1)).Single();

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
                    .DisconnectAsync(
                        scope,
                        AccountingProvider.QuickBooks,
                        "disconnect-rotation",
                        CancellationToken.None);
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
                .ClaimPullAsync("pull", TimeSpan.FromMinutes(5), 1)).Single();

        await using var scheduledDb = NewContext();
        await using var inlineDb = NewContext();
        var scheduledTask = new AccountingConnectionClaimStore(scheduledDb).ClaimTokenRefreshAsync(
            "scheduled", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 1);
        var inlineTask = new AccountingConnectionClaimStore(inlineDb).ClaimInlineTokenRotationAsync(
            pull.Connection.Id, pull.Fence.ClaimToken, "inline", TimeSpan.FromMinutes(3));
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
                "stale", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), 1)).Single();
        await using (var expire = NewContext())
            await expire.AccountingConnections.Where(row => row.Id == stale.Connection.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.TokenRotationClaimExpiresAtUtc, now.AddMinutes(-1)));
        await using (var staleCompletion = NewContext())
        {
            var store = new AccountingConnectionClaimStore(staleCompletion);
            (await store.CompleteTokenRotationAsync(
                    stale.Connection.Id,
                    stale.Fence.ClaimToken,
                    null,
                    "forged-access",
                    "forged-refresh",
                    now.AddHours(1)))
                .Should().Be(0, "an expired rotation lease cannot persist provider credentials");
            (await store.MarkTokenRotationRecoveryRequiredAsync(
                    stale.Connection.Id, stale.Fence.ClaimToken, "stale recovery"))
                .Should().Be(0, "an expired worker cannot overwrite the reconciliation owner");
        }
        await using (var retry = NewContext())
            (await new AccountingConnectionClaimStore(retry).ClaimTokenRefreshAsync(
                "must-not-retry", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 1))
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
            "refresh", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3), 1)).Single();

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

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync(
        int portfolioId,
        DateTime now,
        string suffix)
    {
        await using var db = NewContext();
        var email = $"accounting-{suffix}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = $"Accounting {suffix}",
            CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var context = new WorkspaceAccessContext
        {
            UserId = user.Id,
            PortfolioId = portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = context,
            PortfolioId = portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContext = context,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        return new WorkspaceReadScope(
            portfolioId,
            user.Id,
            session.Id,
            context.Id,
            context.AccessRevision);
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

    private AccountingImportService CreateImportService(
        RentalCommandDbContext db,
        IAccountingProvider provider,
        List<string>? applyCommands = null)
    {
        var (providerResolver, settingsResolver) = CreateResolvers(provider);
        var claims = new AccountingConnectionClaimStore(db);
        return new AccountingImportService(
            _dataProtection, providerResolver, settingsResolver,
            new AccountingTokenService(_dataProtection, providerResolver, settingsResolver,
                claims, TimeProvider.System, NullLogger<AccountingTokenService>.Instance),
            claims,
            CreateAtomicUnitOfWork(applyCommands),
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
            _dataProtection, providerResolver, settingsResolver, tokenService, claims,
            CreateAtomicUnitOfWork(),
            TimeProvider.System, NullLogger<AccountingImportService>.Instance);
        return new AccountingConnectionService(
            db, _dataProtection, providerResolver, settingsResolver, import,
            TimeProvider.System, null!,
            NullLogger<AccountingConnectionService>.Instance);
    }

    private sealed class FailCompletionClaimStore(IAccountingConnectionClaimStore inner)
        : IAccountingConnectionClaimStore
    {
        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimPullAsync(
            string owner, TimeSpan duration, int size, CancellationToken ct = default) =>
            inner.ClaimPullAsync(owner, duration, size, ct);
        public Task<IReadOnlyList<AccountingConnectionClaim>> ClaimTokenRefreshAsync(
            string owner, TimeSpan horizon, TimeSpan duration, int size, CancellationToken ct = default) =>
            inner.ClaimTokenRefreshAsync(owner, horizon, duration, size, ct);
        public Task<AccountingConnectionClaim?> ClaimInlineTokenRotationAsync(
            int id, Guid? pullToken, string owner, TimeSpan duration, CancellationToken ct = default) =>
            inner.ClaimInlineTokenRotationAsync(id, pullToken, owner, duration, ct);
        public Task<int> MarkPullFailedAsync(
            int id, Guid token, TimeSpan retryDelay, string error, CancellationToken ct = default) =>
            inner.MarkPullFailedAsync(id, token, retryDelay, error, ct);
        public Task<int> MarkTokenRotationRecoveryRequiredAsync(
            int id, Guid token, string error, CancellationToken ct = default) =>
            inner.MarkTokenRotationRecoveryRequiredAsync(id, token, error, ct);
        public Task<int> CompleteTokenRotationAsync(
            int id, Guid token, Guid? parentPullToken, string access, string refresh,
            DateTime expires,
            CancellationToken ct = default) => throw new DbUpdateException("Injected local persistence loss.");
        public Task<int> ReconcileAbandonedTokenRotationsAsync(CancellationToken ct = default) =>
            inner.ReconcileAbandonedTokenRotationsAsync(ct);
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

    private IAtomicUnitOfWork CreateAtomicUnitOfWork(List<string>? applyCommands = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<
            RentalCommand.Core.Accounting.ApplyAccountingPullResultCommand,
            RentalCommand.Core.Accounting.ApplyAccountingPullResult,
            ApplyAccountingPullResultHandler>();
        services.AddAtomicCommandHandler<
            PrepareAccountingDisconnectCommand,
            PrepareAccountingDisconnectResult,
            PrepareAccountingDisconnectHandler>();
        services.AddAtomicCommandHandler<
            FinalizeAccountingDisconnectCommand,
            FinalizeAccountingDisconnectResult,
            FinalizeAccountingDisconnectHandler>();
        services.AddAtomicCommandHandler<
            SetAccountingDirectionCommand,
            SetAccountingDirectionResult,
            SetAccountingDirectionHandler>();
        services.AddDbContext<RentalCommandDbContext>((sp, options) =>
        {
            options.UseNpgsql(_connectionString).UseAtomicPersistenceKernel(sp);
            if (applyCommands is not null) options.AddInterceptors(new AccountingApplyCommandInterceptor(applyCommands));
        });
        return services.BuildServiceProvider().GetRequiredService<IAtomicUnitOfWork>();
    }

    private sealed class AccountingApplyCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
