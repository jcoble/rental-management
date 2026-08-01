using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Core;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Banking;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Banking;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public class BankingServiceTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly Mock<IPlaidBankingProvider> _plaid = new();
    private readonly List<IDisposable> _atomicHosts = [];
    private MigratedPostgreSqlTestContext _ctx = null!;
    private BankingService _sut = null!;
    private WorkspaceReadScope _scope;

    public BankingServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _plaid
            .Setup(p => p.SyncTransactionsAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidTransactionsSyncResult(null, [], [], [], "initial-request-id"));
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        await new ChartOfAccountsSeedService(_ctx.Db).SeedAsync(1);
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();
        _scope = _ctx.Db.SeedAdministratorScope(1, nameof(BankingServiceTests));
        _sut = CreateService();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _atomicHosts) host.Dispose();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task ImportAsync_DeduplicatesBankLines_AndSuggestsPaymentMatch()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));

        var first = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Plaid",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            AccountMask = "1234",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "plaid-txn-1",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "ACH CREDIT EMILY CHEN RENT",
                    Amount = payment.Amount,
                    IsoCurrencyCode = "USD",
                    Category = "Transfer",
                },
            ],
        });

        var duplicate = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Plaid",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            AccountMask = "1234",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "plaid-txn-1",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Duplicate line",
                    Amount = payment.Amount,
                },
            ],
        });

        first.ImportedCount.Should().Be(1);
        duplicate.ImportedCount.Should().Be(0);
        duplicate.SkippedCount.Should().Be(1);

        var transactionId = first.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);
        var suggestion = (await _sut.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle().Subject
            .SuggestedMatch;
        suggestion.Should().NotBeNull();
        suggestion!.EntityType.Should().Be("TenantLedgerEntry");
        suggestion.EntityId.Should().Be(payment.Id);
    }

    [Fact]
    public async Task ImportAsync_StatementOnly_PersistsMonthlyAccountControlsWithoutSyntheticTransactions()
    {
        var securityDepositRequest = new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Blue Door Synthetic Bank",
            AccountName = "Security deposits",
            AccountMask = "1818",
            Statement = new ImportBankStatementControl
            {
                PeriodStart = new DateOnly(2027, 1, 1),
                PeriodEnd = new DateOnly(2027, 1, 31),
                OpeningBalance = 54250m,
                ClosingBalance = 55925m,
                IsoCurrencyCode = "usd",
            },
        };
        var reserveRequest = new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Blue Door Synthetic Bank",
            AccountName = "Reserve",
            AccountMask = "7070",
            Statement = new ImportBankStatementControl
            {
                PeriodStart = new DateOnly(2027, 1, 1),
                PeriodEnd = new DateOnly(2027, 1, 31),
                OpeningBalance = 50000m,
                ClosingBalance = 50000m,
            },
        };

        var securityDeposit = await _sut.ImportAsync(1, securityDepositRequest);
        var replay = await _sut.ImportAsync(1, securityDepositRequest);
        var reserve = await _sut.ImportAsync(1, reserveRequest);

        replay.Statement.Should().BeEquivalentTo(securityDeposit.Statement);
        securityDeposit.Statement!.StatementMovement.Should().Be(1675m);
        securityDeposit.Statement.IsoCurrencyCode.Should().Be("USD");
        reserve.Statement!.StatementMovement.Should().Be(0m);
        securityDeposit.Transactions.Should().BeEmpty();
        reserve.Transactions.Should().BeEmpty();
        var controls = await _ctx.Db.BankStatements.AsNoTracking()
            .Where(row => row.PortfolioId == 1
                && row.PeriodStart == new DateOnly(2027, 1, 1)
                && row.PeriodEnd == new DateOnly(2027, 1, 31))
            .OrderBy(row => row.BankConnection!.AccountMask)
            .Select(row => new
            {
                row.BankConnection!.AccountMask,
                row.OpeningBalance,
                row.ClosingBalance,
                row.StatementMovement,
                TransactionCount = row.BankConnection.Transactions.Count,
            })
            .ToListAsync();
        controls.Should().BeEquivalentTo(
        [
            new
            {
                AccountMask = "1818",
                OpeningBalance = 54250m,
                ClosingBalance = 55925m,
                StatementMovement = 1675m,
                TransactionCount = 0,
            },
            new
            {
                AccountMask = "7070",
                OpeningBalance = 50000m,
                ClosingBalance = 50000m,
                StatementMovement = 0m,
                TransactionCount = 0,
            },
        ]);
    }

    [Fact]
    public async Task MatchAndClearMatch_UpdateTransactionReconciliationState()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "manual-txn-1",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Rent deposit",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);

        var matchRequest = new MatchBankTransactionRequest
        {
            OperationKey = "match-payment",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        };
        var matched = await _sut.MatchAsync(_scope, transactionId, matchRequest);
        matched.Should().NotBeNull();
        matched!.MatchStatus.Should().Be("Matched");
        var matchedState = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new
            {
                row.MatchStatus,
                row.MatchedTenantAccountId,
                row.MatchedTenantLedgerEntryId,
            })
            .SingleAsync();
        matchedState.MatchStatus.Should().Be("Matched");
        matchedState.MatchedTenantAccountId.Should().Be(payment.TenantAccountId);
        matchedState.MatchedTenantLedgerEntryId.Should().Be(payment.Id);

        var cleared = await _sut.ClearMatchAsync(_scope, transactionId, Mutation(transactionId, "clear-match"));
        cleared.Should().NotBeNull();
        cleared!.MatchStatus.Should().Be("Unmatched");
        var replay = await _sut.MatchAsync(_scope, transactionId, matchRequest);
        replay.Should().NotBeNull();
        replay!.MatchStatus.Should().Be("Matched");
        var clearedState = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new
            {
                row.MatchStatus,
                row.MatchedTenantAccountId,
                row.MatchedTenantLedgerEntryId,
            })
            .SingleAsync();
        clearedState.MatchStatus.Should().Be("Unmatched");
        clearedState.MatchedTenantAccountId.Should().BeNull();
        clearedState.MatchedTenantLedgerEntryId.Should().BeNull();
    }

    [Fact]
    public async Task MatchAsync_DifferentBankTransactions_CannotClaimSameReceiptTarget()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Receipt Duplicate Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "receipt-duplicate-first",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Rent deposit first",
                    Amount = payment.Amount,
                },
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "receipt-duplicate-second",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Rent deposit second",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionIds = imported.Transactions
            .OrderBy(row => row.ProviderTransactionId)
            .Select(row => row.Id)
            .ToArray();
        foreach (var transactionId in transactionIds)
            AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);

        var first = await _sut.MatchAsync(_scope, transactionIds[0], new MatchBankTransactionRequest
        {
            OperationKey = "receipt-duplicate-first-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionIds[0]),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        });
        var second = await _sut.MatchAsync(_scope, transactionIds[1], new MatchBankTransactionRequest
        {
            OperationKey = "receipt-duplicate-second-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionIds[1]),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        });

        first.Should().NotBeNull();
        second.Should().BeNull();
        var matchedRows = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.PortfolioId == 1 && row.MatchedTenantLedgerEntryId == payment.Id)
            .Select(row => row.Id)
            .ToListAsync();
        matchedRows.Should().Equal([transactionIds[0]]);
    }

    [Fact]
    public async Task ClearMatchAsync_AccessDenialRollsBackReceiptAndMutation()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var propertyId = payment.TenantAccount!.LeaseManagement!.PropertyId;
        var imported = await _sut.ImportAsync(1, BankImport(
            "reconcile-denial-rollback",
            payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            "Emily Chen",
            payment.Amount));
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, propertyId);
        await _sut.MatchAsync(_scope, transactionId, new MatchBankTransactionRequest
        {
            OperationKey = "reconcile-denial-seed-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        });
        var propertyManager = _ctx.Db.SeedPropertyManagerScope(
            1, propertyId, "bank-clear-property-manager");

        var denied = async () => await _sut.ClearMatchAsync(
            propertyManager,
            transactionId,
            new BankTransactionMutationRequest
            {
                OperationKey = "property-manager-clear-denied",
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();

        _ctx.Db.ChangeTracker.Clear();
        var transaction = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new { row.MatchStatus, row.MatchedTenantLedgerEntryId })
            .SingleAsync();
        transaction.MatchStatus.Should().Be("Matched");
        transaction.MatchedTenantLedgerEntryId.Should().Be(payment.Id);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":property-manager-clear-denied")))
            .Should().Be(0);
    }

    [Fact]
    public async Task MatchAsync_ConcurrentSameOperation_ReplaysOneAuthorizedReconciliation()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Replay Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "concurrent-reconciliation-replay",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Rent deposit replay",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);
        var expectedUpdatedAt = TransactionUpdatedAt(_ctx, transactionId);

        await using var firstDb = new RentalCommand.Data.RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommand.Data.RentalCommandDbContext>()
                .UseNpgsql(_ctx.ConnectionString)
                .Options);
        await using var secondDb = new RentalCommand.Data.RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommand.Data.RentalCommandDbContext>()
                .UseNpgsql(_ctx.ConnectionString)
                .Options);
        var firstService = CreateServiceFor(firstDb, _ctx.ConnectionString);
        var secondService = CreateServiceFor(secondDb, _ctx.ConnectionString);
        var request = new MatchBankTransactionRequest
        {
            OperationKey = "same-authorized-reconciliation",
            ExpectedUpdatedAtUtc = expectedUpdatedAt,
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        };

        var outcomes = await Task.WhenAll(
            firstService.MatchAsync(_scope, transactionId, request),
            secondService.MatchAsync(_scope, transactionId, request));

        outcomes.Should().OnlyContain(outcome =>
            outcome != null && outcome.MatchStatus == "Matched");
        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.MatchedTenantLedgerEntryId)
            .SingleAsync()).Should().Be(payment.Id);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":same-authorized-reconciliation")))
            .Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == "banking.transaction.reconcile" &&
            audit.CommandIdempotencyKey.EndsWith(":same-authorized-reconciliation") &&
            audit.EntityType == nameof(BankTransaction)))
            .Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == "banking.transaction.reconcile" &&
            audit.CommandIdempotencyKey.EndsWith(":same-authorized-reconciliation") &&
            audit.EntityType == nameof(JournalEntry)))
            .Should().Be(1);
    }

    [Fact]
    public async Task RouteTransactionAsync_EnforcesTargetPropertyScopeAndDestructiveClearAuthority()
    {
        var postedAt = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var allowedPayment = SeedRentPaymentInto(
            _ctx, "Allowed", "Tenant", 1400m, postedAt, "L-route-allowed");
        var deniedPayment = SeedRentPaymentInto(
            _ctx, "Denied", "Tenant", 1500m, postedAt, "L-route-denied");
        var allowedPropertyId = allowedPayment.TenantAccount!.LeaseManagement!.PropertyId;
        var deniedPropertyId = deniedPayment.TenantAccount!.LeaseManagement!.PropertyId;
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Routing Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "administrator-routing-boundary",
                    PostedAt = allowedPayment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Needs property route",
                    Amount = allowedPayment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        var propertyManager = _ctx.Db.SeedPropertyManagerScope(
            1, allowedPropertyId, "bank-routing-property-manager");

        var routed = await _sut.RouteTransactionAsync(
            propertyManager,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "property-manager-route-applied",
                PropertyId = allowedPropertyId,
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        routed.Should().NotBeNull();
        routed!.PropertyId.Should().Be(allowedPropertyId);

        var crossScope = async () => await _sut.RouteTransactionAsync(
            propertyManager,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "property-manager-cross-scope-route",
                PropertyId = deniedPropertyId,
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        await crossScope.Should().ThrowAsync<UnauthorizedAccessException>();

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.PropertyId)
            .SingleAsync()).Should().Be(allowedPropertyId);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":property-manager-cross-scope-route")))
            .Should().Be(0);

        var routedOutsideManagerScope = await _sut.RouteTransactionAsync(
            _scope,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "administrator-route-outside-manager-scope",
                PropertyId = deniedPropertyId,
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        routedOutsideManagerScope.Should().NotBeNull();
        routedOutsideManagerScope!.PropertyId.Should().Be(deniedPropertyId);

        var sourceScopeEscape = async () => await _sut.RouteTransactionAsync(
            propertyManager,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "property-manager-source-scope-escape",
                PropertyId = allowedPropertyId,
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        await sourceScopeEscape.Should().ThrowAsync<UnauthorizedAccessException>(
            "a manager cannot pull a bank line out of another manager's property by routing it into their own scope");

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.PropertyId)
            .SingleAsync()).Should().Be(deniedPropertyId);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":property-manager-source-scope-escape")))
            .Should().Be(0);

        var cleared = await _sut.RouteTransactionAsync(
            _scope,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "administrator-route-cleared",
                PropertyId = null,
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            });
        cleared.Should().NotBeNull();
        cleared!.PropertyId.Should().BeNull();

        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":administrator-route-cleared")))
            .Should().Be(1);
    }

    [Fact]
    public async Task RouteTransactionAsync_ReplayAuthorizationFailsClosedAfterScopeRevocation()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var propertyId = payment.TenantAccount!.LeaseManagement!.PropertyId;
        var imported = await _sut.ImportAsync(1, BankImport(
            "route-replay-revocation",
            payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            "Emily Chen",
            payment.Amount));
        var transactionId = imported.Transactions.Single().Id;
        var propertyManager = _ctx.Db.SeedPropertyManagerScope(
            1, propertyId, "bank-routing-replay-property-manager");
        var request = new RouteBankTransactionRequest
        {
            OperationKey = "property-manager-route-replay",
            PropertyId = propertyId,
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
        };

        (await _sut.RouteTransactionAsync(propertyManager, transactionId, request))
            .Should().NotBeNull();

        var assignment = await _ctx.Db.MembershipRoleAssignments.SingleAsync(row =>
            row.WorkspaceMembership!.AccessContextId == propertyManager.AccessContextId);
        assignment.Status = MembershipRoleAssignmentStatus.Revoked;
        assignment.RevokedAtUtc = DateTime.UtcNow;
        await _ctx.Db.SaveChangesAsync();
        _ctx.Db.ChangeTracker.Clear();

        var replay = async () => await _sut.RouteTransactionAsync(
            propertyManager, transactionId, request);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();

        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":property-manager-route-replay")))
            .Should().Be(1, "the committed receipt remains but cannot bypass current authority");
        (await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.PropertyId)
            .SingleAsync()).Should().Be(propertyId);
    }

    [Fact]
    public async Task ImportAsync_AuditsConnectionAndImportedTransactions()
    {
        await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "audit-import-1",
                    PostedAt = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
                    Description = "ACH CREDIT RENT",
                    Amount = 1400m,
                },
            ],
        });

        var connection = _ctx.Db.BankConnections.Single();
        var transaction = _ctx.Db.BankTransactions.Single();

        var connectionAudit = _ctx.Db.AtomicAuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankConnection" &&
            a.EntityId == connection.Id &&
            a.Operation == AuditLogOperation.Created).Subject;
        using var connectionValues = JsonDocument.Parse(connectionAudit.NewValues!);
        connectionValues.RootElement.GetProperty("institutionName").GetString()
            .Should().Be("Test Bank");

        var transactionAudit = _ctx.Db.AtomicAuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankTransaction" &&
            a.EntityId == transaction.Id &&
            a.Operation == AuditLogOperation.Created).Subject;
        using var transactionValues = JsonDocument.Parse(transactionAudit.NewValues!);
        transactionValues.RootElement.GetProperty("providerTransactionId").GetString()
            .Should().Be("audit-import-1");
    }

    [Fact]
    public async Task MatchAsync_AuditsReconciliationStateChange()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "audit-match-1",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Rent deposit",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);
        _ctx.Db.AtomicAuditLogs.RemoveRange(_ctx.Db.AtomicAuditLogs);
        await _ctx.Db.SaveChangesAsync();

        await _sut.MatchAsync(_scope, transactionId, new MatchBankTransactionRequest
        {
            OperationKey = "audit-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        });

        var log = _ctx.Db.AtomicAuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankTransaction" &&
            a.EntityId == transactionId &&
            a.Operation == AuditLogOperation.Updated).Subject;
        using var oldValues = JsonDocument.Parse(log.OldValues!);
        using var newValues = JsonDocument.Parse(log.NewValues!);
        oldValues.RootElement.GetProperty("matchStatus").GetString()
            .Should().Be("Unmatched");
        newValues.RootElement.GetProperty("matchStatus").GetString()
            .Should().Be("Matched");
        newValues.RootElement.GetProperty("matchedTenantLedgerEntryId").GetInt64()
            .Should().Be(payment.Id);
        log.ChangeReason.Should().Contain("receipt");
    }

    [Fact]
    public async Task ReviewQueue_SurfacesSuggestedMatch_ConfirmLinksAndRemovesIt_DismissRemovesIt()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "queue-txn-1",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "ACH CREDIT EMILY CHEN RENT",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);
        await _ctx.ActivateApiScopeAsync(_scope);

        // A suggested match shows up in the review queue.
        var queue = await _sut.GetReviewQueueAsync(_scope);
        queue.Count.Should().Be(1);
        var item = queue.Items.Single();
        item.Transaction.Id.Should().Be(transactionId);
        item.Suggestion.Label.Should().Contain(payment.TenantAccount!.AccountNumber);

        // Confirming links the payment and removes the line from the queue.
        var confirmed = await _sut.ConfirmMatchAsync(_scope, transactionId, new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-suggestion",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
        });
        confirmed.Should().NotBeNull();
        confirmed!.MatchStatus.Should().Be("Matched");
        var confirmedRow = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new { row.MatchedTenantAccountId, row.MatchedTenantLedgerEntryId })
            .SingleAsync();
        confirmedRow.MatchedTenantAccountId.Should().Be(payment.TenantAccountId);
        confirmedRow.MatchedTenantLedgerEntryId.Should().Be(payment.Id);
        (await _sut.GetReviewQueueAsync(_scope)).Count.Should().Be(0);
    }

    [Fact]
    public async Task DismissMatch_MarksDismissed_AndRemovesFromQueue()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "queue-txn-2",
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "ACH CREDIT EMILY CHEN RENT",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.TenantAccount!.LeaseManagement!.PropertyId);
        await _ctx.ActivateApiScopeAsync(_scope);
        (await _sut.GetReviewQueueAsync(_scope)).Count.Should().Be(1);

        var dismissed = await _sut.DismissMatchAsync(_scope, transactionId, Mutation(transactionId, "dismiss-match"));

        dismissed.Should().NotBeNull();
        dismissed!.MatchStatus.Should().Be("Dismissed");
        dismissed.MatchedTenantLedgerEntryId.Should().BeNull();
        dismissed.SuggestedMatch.Should().BeNull();
        (await _sut.GetReviewQueueAsync(_scope)).Count.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmMatch_WithExplicitExpenseId_LinksExpense()
    {
        var expense = SeedExpense(new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc), 84.25m);
        var bankEffectiveAt = expense.IncurredAt.AddDays(1).AddHours(9);
        expense.Status = ExpenseStatus.Approved;
        expense.PaidAt = null;
        _ctx.Db.SaveChanges();
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "queue-txn-3",
                    PostedAt = bankEffectiveAt,
                    Description = "HARDWARE STORE",
                    Amount = -expense.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, expense.PropertyId!.Value);

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-expense",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            ExpenseId = expense.Id,
        };
        var confirmed = await _sut.ConfirmMatchAsync(_scope, transactionId, request);
        var replay = await _sut.ConfirmMatchAsync(_scope, transactionId, request);

        confirmed.Should().NotBeNull();
        replay.Should().BeEquivalentTo(confirmed);
        confirmed!.MatchStatus.Should().Be("Matched");
        var confirmedRow = await (
            from transaction in _ctx.Db.BankTransactions.AsNoTracking()
            join paidExpense in _ctx.Db.Expenses.AsNoTracking()
                on transaction.MatchedExpenseId equals paidExpense.Id
            where transaction.Id == transactionId
            select new
            {
                transaction.MatchedExpenseId,
                transaction.MatchedTenantLedgerEntryId,
                ExpenseStatus = paidExpense.Status,
                paidExpense.PaidAt,
            })
            .SingleAsync();
        confirmedRow.MatchedExpenseId.Should().Be(expense.Id);
        confirmedRow.MatchedTenantLedgerEntryId.Should().BeNull();
        confirmedRow.ExpenseStatus.Should().Be(ExpenseStatus.Paid);
        confirmedRow.PaidAt.Should().Be(bankEffectiveAt);
    }

    [Fact]
    public async Task ConfirmMatch_WithRejectedExpense_DoesNotMutateEitherRecord()
    {
        var effectiveAt = new DateTime(2026, 06, 02, 9, 0, 0, DateTimeKind.Utc);
        var expense = SeedExpense(effectiveAt, 84.25m);
        expense.Status = ExpenseStatus.Rejected;
        expense.PaidAt = null;
        _ctx.Db.SaveChanges();
        var imported = await _sut.ImportAsync(1, BankImport(
            "rejected-expense",
            effectiveAt,
            "HARDWARE STORE",
            -expense.Amount));
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, expense.PropertyId!.Value);

        var result = await _sut.ConfirmMatchAsync(_scope, transactionId, new ConfirmBankMatchRequest
        {
            OperationKey = "reject-ineligible-expense",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            ExpenseId = expense.Id,
        });

        result.Should().BeNull();
        var state = await (
            from transaction in _ctx.Db.BankTransactions.AsNoTracking()
            from rejectedExpense in _ctx.Db.Expenses.AsNoTracking()
            where transaction.Id == transactionId && rejectedExpense.Id == expense.Id
            select new
            {
                transaction.MatchStatus,
                transaction.MatchedExpenseId,
                ExpenseStatus = rejectedExpense.Status,
                rejectedExpense.PaidAt,
            })
            .SingleAsync();
        state.MatchStatus.Should().Be("Unmatched");
        state.MatchedExpenseId.Should().BeNull();
        state.ExpenseStatus.Should().Be(ExpenseStatus.Rejected);
        state.PaidAt.Should().BeNull();
    }

    [Fact]
    public async Task ConfirmMatch_WithSuggestedTenantTransfer_LinksTransferLedgerEntryAtomically()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var scope = ctx.Db.SeedAdministratorScope(1, "bank-transfer-match");
        var date = new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc);
        var transfer = SeedTenantTransfer(ctx, date, 325.15m, TenantLedgerEntryType.TransferOut, TenantLedgerDirection.Debit);
        var imported = await sut.ImportAsync(1, BankImport(
            "tenant-transfer-match",
            date,
            transfer.TenantAccount!.AccountNumber,
            -transfer.Amount));
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(ctx, transactionId, transfer.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();
        await ctx.ActivateApiScopeAsync(scope);

        var queue = await sut.GetReviewQueueAsync(scope);
        var item = queue.Items.Should().ContainSingle().Subject;
        item.Transaction.Id.Should().Be(transactionId);
        item.Suggestion.Label.Should().Contain("tenant-account transfer");
        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("TransferOut", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
            "tenant transfer suggestions must be narrowed, scored, and ranked by the DB-side suggestion query");

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-tenant-transfer",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(ctx, transactionId),
        };

        var confirmed = await sut.ConfirmMatchAsync(scope, transactionId, request);
        var replay = await sut.ConfirmMatchAsync(scope, transactionId, request);

        confirmed.Should().NotBeNull();
        replay.Should().NotBeNull();
        replay!.MatchStatus.Should().Be("Matched");
        var confirmedRow = await ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new
            {
                row.MatchStatus,
                row.MatchedTenantAccountId,
                row.MatchedTenantLedgerEntryId,
                row.MatchedExpenseId,
            })
            .SingleAsync();
        confirmedRow.MatchStatus.Should().Be("Matched");
        confirmedRow.MatchedTenantAccountId.Should().Be(transfer.TenantAccountId);
        confirmedRow.MatchedTenantLedgerEntryId.Should().Be(transfer.Id);
        confirmedRow.MatchedExpenseId.Should().BeNull();
        (await sut.GetReviewQueueAsync(scope)).Count.Should().Be(0);
    }

    [Fact]
    public async Task ConfirmMatch_WithSuggestedBankFeeExpense_LinksExpense()
    {
        var date = new DateTime(2026, 06, 04, 0, 0, 0, DateTimeKind.Utc);
        var fee = SeedExpense(date, 12.50m, "Monthly Bank Fee", "Bank service fee");
        fee.PropertyId = null;
        fee.Property = null;
        fee.OperationalScope = ExpenseOperationalScope.Portfolio;
        _ctx.Db.SaveChanges();
        var imported = await _sut.ImportAsync(1, BankImport(
            "bank-fee-match",
            date,
            "Monthly Bank Fee",
            -fee.Amount));
        var transactionId = imported.Transactions.Single().Id;

        var suggestion = (await _sut.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle(row => row.Id == transactionId).Subject
            .SuggestedMatch;
        suggestion.Should().NotBeNull();
        suggestion!.EntityType.Should().Be("Expense");
        suggestion.EntityId.Should().Be(fee.Id);
        await _ctx.ActivateApiScopeAsync(_scope);

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-bank-fee-expense",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
        };
        var confirmed = await _sut.ConfirmMatchAsync(_scope, transactionId, request);
        var replay = await _sut.ConfirmMatchAsync(_scope, transactionId, request);

        confirmed.Should().NotBeNull();
        replay.Should().NotBeNull();
        confirmed!.MatchStatus.Should().Be("Matched");
        var confirmedRow = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => new { row.MatchedExpenseId, row.MatchedTenantLedgerEntryId })
            .SingleAsync();
        confirmedRow.MatchedExpenseId.Should().Be(fee.Id);
        confirmedRow.MatchedTenantLedgerEntryId.Should().BeNull();
    }

    [Fact]
    public async Task MatchAsync_DifferentBankTransactions_CannotClaimSameExpenseTarget()
    {
        var date = new DateTime(2026, 06, 04, 0, 0, 0, DateTimeKind.Utc);
        var fee = SeedExpense(date, 12.50m, "Duplicate Bank Fee", "Duplicate bank service fee");
        fee.PropertyId = null;
        fee.Property = null;
        fee.OperationalScope = ExpenseOperationalScope.Portfolio;
        _ctx.Db.SaveChanges();
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Expense Duplicate Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "expense-duplicate-first",
                    PostedAt = date,
                    Description = "Monthly Bank Fee first",
                    Amount = -fee.Amount,
                },
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "expense-duplicate-second",
                    PostedAt = date,
                    Description = "Monthly Bank Fee second",
                    Amount = -fee.Amount,
                },
            ],
        });
        var transactionIds = imported.Transactions
            .OrderBy(row => row.ProviderTransactionId)
            .Select(row => row.Id)
            .ToArray();

        var first = await _sut.MatchAsync(_scope, transactionIds[0], new MatchBankTransactionRequest
        {
            OperationKey = "expense-duplicate-first-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionIds[0]),
            ExpenseId = fee.Id,
        });
        var second = await _sut.MatchAsync(_scope, transactionIds[1], new MatchBankTransactionRequest
        {
            OperationKey = "expense-duplicate-second-match",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionIds[1]),
            ExpenseId = fee.Id,
        });

        first.Should().NotBeNull();
        second.Should().BeNull();
        var matchedRows = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.PortfolioId == 1 && row.MatchedExpenseId == fee.Id)
            .Select(row => row.Id)
            .ToListAsync();
        matchedRows.Should().Equal([transactionIds[0]]);
    }

    [Fact]
    public async Task ConfirmMatch_WithSuggestedLoanPayment_LinksPaidInstallmentAndReplaysExactly()
    {
        var date = new DateTime(2026, 06, 20, 0, 0, 0, DateTimeKind.Utc);
        var payment = SeedLoanPayment(date, 1054m);
        var imported = await _sut.ImportAsync(1, BankImport(
            "loan-payment-match",
            date,
            payment.Loan!.Lender,
            -payment.TotalAmount));
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, payment.Loan.PropertyId);

        var suggestion = (await _sut.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle(row => row.Id == transactionId).Subject
            .SuggestedMatch;
        suggestion.Should().NotBeNull();
        suggestion!.EntityType.Should().Be("LoanPayment");
        suggestion.EntityId.Should().Be(payment.Id);
        await _ctx.ActivateApiScopeAsync(_scope);

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-loan-payment",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
        };
        var confirmed = await _sut.ConfirmMatchAsync(_scope, transactionId, request);
        var replay = await _sut.ConfirmMatchAsync(_scope, transactionId, request);

        confirmed.Should().NotBeNull();
        replay.Should().NotBeNull();
        replay!.MatchStatus.Should().Be("Matched");
        var row = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => new
            {
                transaction.MatchedLoanPaymentId,
                transaction.MatchedExpenseId,
                transaction.MatchedOwnerDistributionId,
            })
            .SingleAsync();
        row.MatchedLoanPaymentId.Should().Be(payment.Id);
        row.MatchedExpenseId.Should().BeNull();
        row.MatchedOwnerDistributionId.Should().BeNull();
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":confirm-loan-payment"))).Should().Be(1);
    }

    [Fact]
    public async Task ConfirmMatch_WithPortfolioOwnerDistribution_RequiresAllPropertiesAuthority()
    {
        var date = new DateTime(2026, 06, 25, 0, 0, 0, DateTimeKind.Utc);
        var distribution = SeedOwnerDistribution(date, 3050m);
        var imported = await _sut.ImportAsync(1, BankImport(
            "owner-distribution-match",
            date,
            distribution.OwnerEntity!.Name,
            -distribution.Amount));
        var transactionId = imported.Transactions.Single().Id;

        var suggestion = (await _sut.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle(row => row.Id == transactionId).Subject
            .SuggestedMatch;
        suggestion.Should().NotBeNull();
        suggestion!.EntityType.Should().Be("OwnerDistribution");

        var property = SeedRouteProperty(_ctx, "Limited manager property");
        var limited = _ctx.Db.SeedPropertyManagerScope(1, property.Id, "owner-distribution-limited");
        await _ctx.ActivateApiScopeAsync(limited);
        Func<Task> denied = async () => await _sut.MatchAsync(limited, transactionId, new MatchBankTransactionRequest
        {
            OperationKey = "owner-distribution-denied",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            OwnerDistributionId = distribution.Id,
        });
        await denied.Should().ThrowAsync<UnauthorizedAccessException>(
            "portfolio-wide money must be denied to a selected-property operator");
        await _ctx.Db.Database.CloseConnectionAsync();
        await _ctx.ActivateApiScopeAsync(_scope);

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "owner-distribution-confirm",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
        };
        (await _sut.ConfirmMatchAsync(_scope, transactionId, request)).Should().NotBeNull();
        (await _sut.ConfirmMatchAsync(_scope, transactionId, request)).Should().NotBeNull();
        var row = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => new
            {
                transaction.MatchStatus,
                transaction.MatchedOwnerDistributionId,
            })
            .SingleAsync();
        row.MatchStatus.Should().Be("Matched");
        row.MatchedOwnerDistributionId.Should().Be(distribution.Id);
    }

    [Fact]
    public async Task ConfirmMatch_WithInternalTransfer_UpdatesBothStatementLinesInOneReceipt()
    {
        var date = new DateTime(2026, 06, 30, 0, 0, 0, DateTimeKind.Utc);
        var operating = SeedBankConnectionInto(_ctx);
        var reserve = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Reserve savings",
            AccountMask = "7070",
            Status = "Active",
            CreatedAt = date,
            UpdatedAt = date,
        };
        _ctx.Db.BankConnections.Add(reserve);
        _ctx.Db.SaveChanges();
        var outgoing = new BankTransaction
        {
            PortfolioId = 1,
            BankConnectionId = operating.Id,
            ProviderTransactionId = "reserve-transfer-out",
            PostedAt = date,
            Description = "Transfer to reserve",
            Amount = -500m,
            MatchStatus = "Unmatched",
            CreatedAt = date,
            UpdatedAt = date,
        };
        var incoming = new BankTransaction
        {
            PortfolioId = 1,
            BankConnectionId = reserve.Id,
            ProviderTransactionId = "reserve-transfer-in",
            PostedAt = date.AddDays(1),
            Description = "Transfer from operating",
            Amount = 500m,
            MatchStatus = "Unmatched",
            CreatedAt = date,
            UpdatedAt = date,
        };
        _ctx.Db.BankTransactions.AddRange(outgoing, incoming);
        _ctx.Db.SaveChanges();
        await _ctx.ActivateApiScopeAsync(_scope);

        var request = new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-reserve-transfer",
            ExpectedUpdatedAtUtc = outgoing.UpdatedAt,
        };
        var confirmed = await _sut.ConfirmMatchAsync(_scope, outgoing.Id, request);
        var replay = await _sut.ConfirmMatchAsync(_scope, outgoing.Id, request);

        confirmed.Should().NotBeNull();
        replay.Should().NotBeNull();
        var pair = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == outgoing.Id || row.Id == incoming.Id)
            .OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.MatchStatus, row.MatchedBankTransactionId })
            .ToListAsync();
        pair.Should().HaveCount(2);
        pair.Should().OnlyContain(row => row.MatchStatus == "Matched");
        pair.Single(row => row.Id == outgoing.Id).MatchedBankTransactionId.Should().Be(incoming.Id);
        pair.Single(row => row.Id == incoming.Id).MatchedBankTransactionId.Should().Be(outgoing.Id);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":confirm-reserve-transfer"))).Should().Be(1);

        var clearRequest = new BankTransactionMutationRequest
        {
            OperationKey = "clear-reserve-transfer",
            ExpectedUpdatedAtUtc = confirmed!.UpdatedAt,
        };
        var cleared = await _sut.ClearMatchAsync(_scope, outgoing.Id, clearRequest);
        var clearReplay = await _sut.ClearMatchAsync(_scope, outgoing.Id, clearRequest);

        cleared.Should().NotBeNull();
        clearReplay.Should().NotBeNull();
        var clearedPair = await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == outgoing.Id || row.Id == incoming.Id)
            .Select(row => new { row.MatchStatus, row.MatchedBankTransactionId })
            .ToListAsync();
        clearedPair.Should().OnlyContain(row =>
            row.MatchStatus == "Unmatched" && row.MatchedBankTransactionId == null);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":clear-reserve-transfer"))).Should().Be(1);
    }

    [Fact]
    public async Task SuggestMatch_PrefersCandidateWhoseNameMatchesTheMerchantLine()
    {
        // Two rent payments, same $1,500 amount and same date. One tenant's name (Carlos Reyes)
        // appears on the bank line's merchant text; the other (Emily Chen) does not. The named
        // candidate must win, even though both clear the amount + date gate.
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var property = SeedRouteProperty(_ctx, "Name ranking");
        var emily = SeedRentPaymentInto(_ctx, "Emily", "Chen", 1500m, date, "L-EMILY", property);
        var carlos = SeedRentPaymentInto(_ctx, "Carlos", "Reyes", 1500m, date, "L-CARLOS", property);

        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "name-txn-1",
                    PostedAt = date,
                    Description = "ACH CREDIT",
                    MerchantName = "Carlos Reyes",
                    Amount = 1500m,
                },
            ],
        });

        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, property.Id);
        var suggestion = (await _sut.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle().Subject
            .SuggestedMatch;
        suggestion.Should().NotBeNull();
        suggestion!.EntityType.Should().Be("TenantLedgerEntry");
        suggestion.EntityId.Should().Be(carlos.Id);
        suggestion.EntityId.Should().NotBe(emily.Id);
    }

    [Fact]
    public async Task SuggestMatch_NameMatchYieldsHigherConfidenceThanDateOnlyMatch()
    {
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        // Named match: merchant text contains the tenant name.
        await using var namedCtx = await _fixture.CreateContextAsync();
        var namedPayment = SeedRentPaymentInto(namedCtx, "Carlos", "Reyes", 1500m, date, "L-1");
        var namedSvc = CreateServiceFor(namedCtx);
        var namedResult = await namedSvc.ImportAsync(1, BankImport("named-1", date, "Carlos Reyes", 1500m));
        var namedTransactionId = namedResult.Transactions.Single().Id;
        AssignRoute(namedCtx, namedTransactionId, namedPayment.TenantAccount!.LeaseManagement!.PropertyId);
        var namedScore = (await namedSvc.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle().Subject
            .SuggestedMatch!.Confidence;

        // Date-only match: no merchant name overlap, same amount + date.
        await using var anonCtx = await _fixture.CreateContextAsync();
        var anonPayment = SeedRentPaymentInto(anonCtx, "Carlos", "Reyes", 1500m, date, "L-1");
        var anonSvc = CreateServiceFor(anonCtx);
        var anonResult = await anonSvc.ImportAsync(1, BankImport("anon-1", date, null, 1500m));
        var anonTransactionId = anonResult.Transactions.Single().Id;
        AssignRoute(anonCtx, anonTransactionId, anonPayment.TenantAccount!.LeaseManagement!.PropertyId);
        var anonScore = (await anonSvc.ListTransactionsAsync(1, "Unmatched"))
            .Items.Should().ContainSingle().Subject
            .SuggestedMatch!.Confidence;

        namedScore.Should().BeGreaterThan(anonScore);
    }

    [Fact]
    public async Task IgnoreTransaction_MarksRemoved_DropsFromUnmatchedQueue_StaysListableUnderFilter()
    {
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "personal-txn-1",
                    PostedAt = new DateTime(2026, 06, 05, 0, 0, 0, DateTimeKind.Utc),
                    Description = "STARBUCKS",
                    MerchantName = "Starbucks",
                    Amount = -6.45m,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, SeedRouteProperty(_ctx, "Personal transactions").Id);

        var ignored = await _sut.IgnoreTransactionAsync(_scope, transactionId, Mutation(transactionId, "ignore-line"));

        ignored.Should().NotBeNull();
        ignored!.MatchStatus.Should().Be("Removed");
        ignored.MatchedTenantLedgerEntryId.Should().BeNull();
        ignored.MatchedExpenseId.Should().BeNull();
        ignored.SuggestedMatch.Should().BeNull();
        ignored.Notes.Should().Contain("personal");

        // Gone from the unmatched feed and the review queue...
        var unmatched = await _sut.ListTransactionsAsync(1, "Unmatched");
        unmatched.Items.Should().NotContain(t => t.Id == transactionId);
        (await _sut.GetReviewQueueAsync(_scope)).Items.Should().NotContain(i => i.Transaction.Id == transactionId);

        // ...but still listable under the Removed filter.
        var removed = await _sut.ListTransactionsAsync(1, "Removed");
        removed.Items.Should().ContainSingle(t => t.Id == transactionId);
    }

    [Fact]
    public async Task IgnoreTransaction_ReturnsNull_ForTransactionInAnotherPortfolio()
    {
        var imported = await _sut.ImportAsync(1, new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = "personal-txn-2",
                    PostedAt = new DateTime(2026, 06, 05, 0, 0, 0, DateTimeKind.Utc),
                    Description = "UBER",
                    Amount = -18.30m,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;

        // A different portfolio must not be able to ignore this line (IDOR guard).
        var otherScope = _scope with { PortfolioId = 999 };
        var result = await _sut.IgnoreTransactionAsync(otherScope, transactionId, Mutation(transactionId, "other-portfolio"));
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSummaryAsync_SuggestedMatchCount_CountsAllSuggestibleUnmatched_NotJustRecentPreview()
    {
        // Regression guard for the SQL-side rewrite of SuggestedMatchCount: it must count EVERY
        // unmatched line that has a plausible match across the whole portfolio — computed in the
        // database — not just the count within the 10-row "recent transactions" preview. Seed 12
        // unmatched deposits, each with its own matching rent payment, so a preview-capped count would
        // report 10 while the correct portfolio-wide count is 12.
        var baseDate = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 12; i++)
        {
            var amount = 1000m + i; // distinct amounts so each deposit pairs with exactly one payment
            var date = baseDate.AddDays(i);
            SeedRentPaymentFor($"Tenant{i}", "Renter", amount, date, $"L-{i:D2}");
            await _sut.ImportAsync(1, BankImport($"deposit-{i}", date, $"Tenant{i} Renter", amount));
        }

        var summary = await _sut.GetSummaryAsync(1);

        summary.UnmatchedCount.Should().Be(12);
        summary.SuggestedMatchCount.Should().Be(12);
        // The preview is still capped at 10 rows, which is exactly why the count must NOT be derived
        // from it.
        summary.RecentTransactions.Count.Should().Be(10);
    }

    [Fact]
    public async Task GetSummaryAsync_SuggestedMatchCount_ExcludesUnmatchedLinesWithNoCandidate()
    {
        // An unmatched deposit with no payment anywhere near its amount/date has no suggestion and must
        // not be counted.
        var date = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        await _sut.ImportAsync(1, BankImport("orphan-deposit", date, "Nobody", 4242.42m));

        var summary = await _sut.GetSummaryAsync(1);

        summary.UnmatchedCount.Should().Be(1);
        summary.SuggestedMatchCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesLastSyncedAtInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var older = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc);
        ctx.Db.BankConnections.AddRange(
            new BankConnection
            {
                PortfolioId = 1,
                Provider = "Plaid",
                InstitutionName = "Bank A",
                AccountName = "Checking",
                Status = "Active",
                LastSyncedAt = older,
                CreatedAt = older,
                UpdatedAt = older,
            },
            new BankConnection
            {
                PortfolioId = 1,
                Provider = "Plaid",
                InstitutionName = "Bank B",
                AccountName = "Savings",
                Status = "Active",
                LastSyncedAt = newer,
                CreatedAt = newer,
                UpdatedAt = newer,
            });
        await ctx.Db.SaveChangesAsync();
        executedSql.Clear();

        var summary = await sut.GetSummaryAsync(1);

        summary.LastSyncedAt.Should().Be(newer);
        executedSql.Should().Contain(sql =>
            sql.Contains("MAX", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LastSyncedAt", StringComparison.OrdinalIgnoreCase),
            "the summary must not load connections and aggregate LastSyncedAt in memory");
    }

    [Fact]
    public async Task GetSummaryAsync_ComputesConnectionCountInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var now = new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc);
        ctx.Db.BankConnections.AddRange(
            new BankConnection
            {
                PortfolioId = 1,
                Provider = "Plaid",
                InstitutionName = "Bank A",
                AccountName = "Checking",
                Status = "Active",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new BankConnection
            {
                PortfolioId = 1,
                Provider = "Plaid",
                InstitutionName = "Bank B",
                AccountName = "Savings",
                Status = "Active",
                CreatedAt = now,
                UpdatedAt = now,
            });
        await ctx.Db.SaveChangesAsync();
        executedSql.Clear();

        var summary = await sut.GetSummaryAsync(1);

        summary.ConnectionCount.Should().Be(2);
        executedSql.Should().Contain(sql =>
            sql.Contains("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"BankConnections\" AS", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("BankTransactions", StringComparison.OrdinalIgnoreCase),
            "the summary must count connections in SQL instead of using the materialized connection list");
    }

    [Fact]
    public async Task ReviewQueue_PrefiltersPaymentSuggestionCandidatesInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        var payment = SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-target");
        SeedRentPaymentInto(ctx, "Old", "Candidate", 1400m, postedAt.AddMonths(-6), "L-old");
        SeedRentPaymentInto(ctx, "Wrong", "Amount", 1999m, postedAt, "L-wrong");
        var scope = ctx.Db.SeedAdministratorScope(1, "bank-review-prefilter");

        var imported = await sut.ImportAsync(1, BankImport("queue-prefilter", postedAt, "Emily Chen", 1400m));
        AssignRoute(ctx, imported.Transactions.Single().Id,
            payment.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();
        await ctx.ActivateApiScopeAsync(scope);

        var queue = await sut.GetReviewQueueAsync(scope);

        var item = queue.Items.Should().ContainSingle().Subject;
        item.Suggestion.Label.Should().Contain(payment.TenantAccount!.AccountNumber);

        var reviewQueueSql = executedSql
            .Where(sql => sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        reviewQueueSql.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase),
            "the review queue count must count the DB-ranked suggestion relation instead of mapped rows");
        reviewQueueSql.Should().Contain(sql =>
            sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
            "bank lines and their top suggestions must be joined, ranked, and paged in one row query");

        var receiptCandidateSql = executedSql
            .Where(sql => sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        receiptCandidateSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COALESCE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LATERAL", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("ROW_NUMBER", StringComparison.OrdinalIgnoreCase)) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains(">=", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("<", StringComparison.OrdinalIgnoreCase),
            "bank suggestion candidates must be narrowed, scored, and ranked by a correlated top-one SQL query before materialization");
    }

    [Fact]
    public async Task ReviewQueue_RanksPaymentSuggestionCandidatesInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        var property = SeedRouteProperty(ctx, "Review queue ranking");
        SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-emily", property);
        var carlos = SeedRentPaymentInto(ctx, "Carlos", "Reyes", 1400m, postedAt, "L-carlos", property);
        var scope = ctx.Db.SeedAdministratorScope(1, "bank-review-ranking");

        var rankedImport = await sut.ImportAsync(1, BankImport("queue-rank", postedAt, "Carlos Reyes", 1400m));
        AssignRoute(ctx, rankedImport.Transactions.Single().Id,
            carlos.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();
        await ctx.ActivateApiScopeAsync(scope);

        var queue = await sut.GetReviewQueueAsync(scope);

        var item = queue.Items.Should().ContainSingle().Subject;
        item.Suggestion.Label.Should().Contain(carlos.TenantAccount!.AccountNumber);

        var receiptCandidateSql = executedSql
            .Where(sql => sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        receiptCandidateSql.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase),
            "bank suggestion candidate ranking must run in SQL, not after materializing every same-amount/date candidate");
    }

    [Fact]
    public async Task ReviewQueue_PagesSuggestibleTransactionsInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        var emily = SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-emily");
        var carlos = SeedRentPaymentInto(ctx, "Carlos", "Reyes", 1450m, postedAt.AddDays(1), "L-carlos");
        var maya = SeedRentPaymentInto(ctx, "Maya", "Patel", 1500m, postedAt.AddDays(2), "L-maya");
        var scope = ctx.Db.SeedAdministratorScope(1, "bank-review-paging");

        var first = await sut.ImportAsync(1, BankImport("queue-page-1", postedAt, "Emily Chen", 1400m));
        var second = await sut.ImportAsync(1, BankImport("queue-page-2", postedAt.AddDays(1), "Carlos Reyes", 1450m));
        var third = await sut.ImportAsync(1, BankImport("queue-page-3", postedAt.AddDays(2), "Maya Patel", 1500m));
        AssignRoute(ctx, first.Transactions.Single().Id, emily.TenantAccount!.LeaseManagement!.PropertyId);
        AssignRoute(ctx, second.Transactions.Single().Id, carlos.TenantAccount!.LeaseManagement!.PropertyId);
        AssignRoute(ctx, third.Transactions.Single().Id, maya.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();
        await ctx.ActivateApiScopeAsync(scope);

        var queue = await sut.GetReviewQueueAsync(scope, skip: 1, take: 1);

        queue.Count.Should().Be(3);
        queue.Skip.Should().Be(1);
        queue.Take.Should().Be(1);
        queue.Items.Should().ContainSingle();
        queue.Items.Single().Transaction.MerchantName.Should().Be("Carlos Reyes");

        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"TenantLedgerEntries\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
            "the review queue must page suggestible rows in SQL before mapping suggestions");
    }

    [Fact]
    public async Task PropertyManagerReviewQueue_FiltersAndPagesAuthorizedPropertyInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        var allowed = SeedRentPaymentInto(ctx, "Allowed", "Tenant", 1400m, postedAt, "L-allowed");
        var denied = SeedRentPaymentInto(ctx, "Denied", "Tenant", 1550m, postedAt, "L-denied");
        var scope = ctx.Db.SeedPropertyManagerScope(
            1,
            allowed.TenantAccount!.LeaseManagement!.PropertyId,
            "bank-review-selected-property");

        var allowedImport = await sut.ImportAsync(1, BankImport("scope-allowed", postedAt, "Allowed Tenant", 1400m));
        var deniedImport = await sut.ImportAsync(1, BankImport("scope-denied", postedAt, "Denied Tenant", 1550m));
        AssignRoute(ctx, allowedImport.Transactions.Single().Id,
            allowed.TenantAccount!.LeaseManagement!.PropertyId);
        AssignRoute(ctx, deniedImport.Transactions.Single().Id,
            denied.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();
        await ctx.ActivateApiScopeAsync(scope);

        var queue = await sut.GetReviewQueueAsync(scope, skip: 0, take: 20);

        queue.Count.Should().Be(1);
        queue.Items.Should().ContainSingle(item =>
            item.Transaction.Id == allowedImport.Transactions.Single().Id);
        queue.Items.Should().NotContain(item =>
            item.Transaction.Id == deniedImport.Transactions.Single().Id);
        (await sut.MatchAsync(
            scope,
            allowedImport.Transactions.Single().Id,
            new MatchBankTransactionRequest
            {
                OperationKey = "cross-property-match",
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(ctx, allowedImport.Transactions.Single().Id),
                TenantAccountId = denied.TenantAccountId,
                TenantLedgerEntryId = denied.Id,
            })).Should().BeNull();
        (await sut.ConfirmMatchAsync(
            scope,
            deniedImport.Transactions.Single().Id,
            new ConfirmBankMatchRequest
            {
                OperationKey = "unauthorized-confirm",
                ExpectedUpdatedAtUtc = TransactionUpdatedAt(ctx, deniedImport.Transactions.Single().Id),
            })).Should().BeNull();

        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("rc_api_effective_capability_scopes", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase),
            "property capability and selected-property scope must be part of the paged bank-line SQL");
    }

    [Fact]
    public async Task ListTransactionsAsync_PagesFilteredTransactionsInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var connection = SeedBankConnectionInto(ctx);
        SeedBankTransactionInto(ctx, connection.Id, "txn-1", new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc), "Unmatched");
        SeedBankTransactionInto(ctx, connection.Id, "txn-2", new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc), "Matched");
        SeedBankTransactionInto(ctx, connection.Id, "txn-3", new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc), "Unmatched");
        SeedBankTransactionInto(ctx, connection.Id, "txn-4", new DateTime(2026, 06, 04, 0, 0, 0, DateTimeKind.Utc), "Unmatched");
        await ctx.Db.SaveChangesAsync();
        executedSql.Clear();

        var page = await sut.ListTransactionsAsync(1, "Unmatched", skip: 1, take: 1);

        page.TotalCount.Should().Be(3);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items.Single().ProviderTransactionId.Should().Be("txn-3");
        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"MatchStatus\" = @", StringComparison.OrdinalIgnoreCase),
            "filtered transaction totals must be counted in SQL before paging");
        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
            "transaction rows must be filtered and paged in SQL before mapping suggestions");
        executedSql.Count(sql => sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase))
            .Should().Be(2,
                "the list must execute only its SQL count and one joined row query, never a follow-up suggestion query");
        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("TenantLedgerEntries", StringComparison.OrdinalIgnoreCase),
            "the current top suggestion must be joined/ranked inside the paged bank-line query");
    }

    [Fact]
    public async Task Reconciliation_InvalidOperationShape_IsAValidationFailure()
    {
        var imported = await _sut.ImportAsync(1, BankImport(
            "invalid-reconciliation-shape",
            new DateTime(2026, 06, 15, 0, 0, 0, DateTimeKind.Utc),
            "Invalid request",
            -25m));
        var transactionId = imported.Transactions.Single().Id;

        var action = async () => await _sut.IgnoreTransactionAsync(
            _scope,
            transactionId,
            new BankTransactionMutationRequest());

        await action.Should().ThrowAsync<DomainValidationException>()
            .WithMessage("*operationKey*");
    }

    [Fact]
    public async Task GetPlaidSettingsAsync_ReturnsSafeConfigStatus()
    {
        var settings = await _sut.GetPlaidSettingsAsync(1);

        settings.Configured.Should().BeTrue();
        settings.PlaidEnvironment.Should().Be("sandbox");
    }

    [Fact]
    public async Task ExchangePlaidPublicTokenAsync_StoresEncryptedAccessToken_AndCreatesConnection()
    {
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                "public-sandbox-token",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-sandbox-token", "item-id-1", "request-id-1"));

        var result = await _sut.ExchangePlaidPublicTokenAsync(_scope, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-store-encrypted",
            PublicToken = "public-sandbox-token",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
            AccountMask = "0000",
            AccountType = "depository",
            AccountSubtype = "checking",
        });

        var connection = _ctx.Db.BankConnections.Single();

        result.InstitutionName.Should().Be("Plaid Test Bank");
        connection.ExternalAccessTokenCipherText.Should().NotBe("access-sandbox-token");
        connection.ExternalAccessTokenCipherText.Should().NotContain("access-sandbox-token");
        connection.ExternalItemIdCipherText.Should().NotBe("item-id-1");
        connection.ExternalAccountIdCipherText.Should().NotBe("account-id-1");
        connection.ExternalItemIdHash.Should().HaveLength(64);
        connection.ExternalAccountIdHash.Should().HaveLength(64);
        connection.ExternalItemIdHash.Should().NotContain("item-id-1");
        connection.ExternalAccountIdHash.Should().NotContain("account-id-1");
    }

    [Fact]
    public async Task ExchangePlaidPublicTokenAsync_ReusesExistingConnectionByLookupHash()
    {
        await using var ctx = await _fixture.CreateContextAsync();
        var sut = CreateServiceFor(ctx);
        var scope = ctx.Db.SeedAdministratorScope(1, "plaid-relink");
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlaidRuntimeSettings _, string publicToken, CancellationToken _) =>
                new PlaidExchangeResult("access-sandbox-token", "item-id-1", $"request-{publicToken}"));

        await sut.ExchangePlaidPublicTokenAsync(scope, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-relink-1",
            PublicToken = "public-sandbox-token-1",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
        });
        await sut.ExchangePlaidPublicTokenAsync(scope, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-relink-2",
            PublicToken = "public-sandbox-token-2",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
        });

        ctx.Db.BankConnections.Should().ContainSingle();
    }

    [Fact]
    public async Task ExchangePlaidPublicTokenAsync_SameOperationReplaysWithoutReexchangingSingleUseToken()
    {
        _plaid.Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(), "single-use-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access", "item", "provider-request"));
        var request = new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "stable-exchange-operation",
            PublicToken = "single-use-token",
            InstitutionName = "Replay bank",
            AccountId = "account",
            AccountName = "Operating",
        };

        var first = await _sut.ExchangePlaidPublicTokenAsync(_scope, request);
        var replay = await _sut.ExchangePlaidPublicTokenAsync(_scope, request);

        replay.Id.Should().Be(first.Id);
        _plaid.Verify(p => p.ExchangePublicTokenAsync(
            It.IsAny<PlaidRuntimeSettings>(), "single-use-token", It.IsAny<CancellationToken>()), Times.Once);
        _ctx.Db.PlaidTokenExchangeAttempts.Single().Status.Should().Be("Completed");
    }

    [Fact]
    public async Task ExchangePlaidPublicTokenAsync_UnknownRemoteOutcomeNeverBlindlyReexchanges()
    {
        _plaid.Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(), "unknown-outcome-token", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection dropped"));
        var request = new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "unknown-outcome-operation",
            PublicToken = "unknown-outcome-token",
            InstitutionName = "Recovery bank",
            AccountId = "account",
            AccountName = "Operating",
        };

        await FluentActions.Invoking(() => _sut.ExchangePlaidPublicTokenAsync(_scope, request))
            .Should().ThrowAsync<HttpRequestException>();
        await FluentActions.Invoking(() => _sut.ExchangePlaidPublicTokenAsync(_scope, request))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*will not be exchanged again*");
        _plaid.Verify(p => p.ExchangePublicTokenAsync(
            It.IsAny<PlaidRuntimeSettings>(), "unknown-outcome-token", It.IsAny<CancellationToken>()), Times.Once);
        _ctx.Db.PlaidTokenExchangeAttempts.Single().Status.Should().Be("RemoteAdmitted");
    }

    [Fact]
    public async Task ImportAsync_TreatsCaseDistinctOpaqueProviderIdsAsDifferentTransactions()
    {
        var request = new ImportBankTransactionsRequest
        {
            Provider = "Manual",
            InstitutionName = "Ordinal bank",
            AccountName = "Operating",
            Transactions =
            [
                new() { ProviderTransactionId = "Txn-AbC", PostedAt = DateTime.UtcNow, Description = "Upper", Amount = 1m },
                new() { ProviderTransactionId = "txn-aBc", PostedAt = DateTime.UtcNow, Description = "Lower", Amount = 2m },
            ],
        };

        var result = await _sut.ImportAsync(1, request);

        result.ImportedCount.Should().Be(2);
        result.Transactions.Select(row => row.ProviderTransactionId)
            .Should().BeEquivalentTo(["Txn-AbC", "txn-aBc"]);
    }

    [Fact]
    public async Task SyncPlaidConnectionAsync_ImportsNewTransactions_UpdatesCursor_AndDeduplicates()
    {
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(It.IsAny<PlaidRuntimeSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-token", "item-id", "request-id"));
        var connection = await _sut.ExchangePlaidPublicTokenAsync(_scope, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-sync-import",
            PublicToken = "public-token",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id",
            AccountName = "Operating Checking",
        });
        _plaid
            .Setup(p => p.SyncTransactionsAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                "access-token",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidTransactionsSyncResult(
                "next-cursor",
                [
                    new PlaidSyncedTransaction(
                        "txn-1",
                        "account-id",
                        new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc),
                        null,
                        "ACH CREDIT RENT",
                        "Emily Chen",
                        -1400m,
                        "USD",
                        "Transfer",
                        "{\"transaction_id\":\"txn-1\"}"),
                    new PlaidSyncedTransaction(
                        "txn-1",
                        "account-id",
                        new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc),
                        null,
                        "Duplicate",
                        null,
                        1400m,
                        "USD",
                        "Transfer",
                        "{}"),
                    new PlaidSyncedTransaction(
                        "txn-other-account",
                        "savings-account-id",
                        new DateTime(2026, 06, 02, 0, 0, 0, DateTimeKind.Utc),
                        null,
                        "Other account transfer",
                        null,
                        -2500m,
                        "USD",
                        "Transfer",
                        "{}"),
                ],
                [],
                [],
                "request-id"));

        var result = await _sut.SyncPlaidConnectionAsync(1, connection.Id);

        result.Should().NotBeNull();
        var syncResult = result!;
        syncResult.ImportedCount.Should().Be(1);
        syncResult.SkippedCount.Should().Be(1);
        var imported = syncResult.Transactions.Should()
            .ContainSingle(t => t.ProviderTransactionId == "txn-1")
            .Which;
        imported.Amount.Should().Be(1400m,
            "Plaid reports credits as negative, while Rental Command stores deposits as positive");
        imported.Description.Should().Be("ACH CREDIT RENT",
            "a later duplicate provider id must not replace the first authoritative occurrence");

        var row = _ctx.Db.BankConnections.Single();
        row.LastSyncedAt.Should().NotBeNull();
        row.SyncCursorCipherText.Should().NotBeNull();
        row.SyncCursorCipherText.Should().NotContain("next-cursor");
    }

    [Fact]
    public async Task SyncPlaidConnectionAsync_UpdatesModifiedTransactions_AndMarksRemovedTransactions()
    {
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(It.IsAny<PlaidRuntimeSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-token", "item-id", "request-id"));
        var connection = await _sut.ExchangePlaidPublicTokenAsync(_scope, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-sync-modify",
            PublicToken = "public-token",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id",
            AccountName = "Operating Checking",
        });
        var bankConnection = _ctx.Db.BankConnections.Single();
        var existing = new BankTransaction
        {
            PortfolioId = 1,
            BankConnectionId = bankConnection.Id,
            ProviderTransactionId = "txn-modified",
            PostedAt = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            Description = "Pending rent",
            Amount = 1000m,
            IsoCurrencyCode = "USD",
            MatchStatus = "Matched",
            MatchedTenantLedgerEntryId = null,
            MatchConfidence = 1m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var removed = new BankTransaction
        {
            PortfolioId = 1,
            BankConnectionId = bankConnection.Id,
            ProviderTransactionId = "txn-removed",
            PostedAt = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc),
            Description = "Removed pending item",
            Amount = 50m,
            IsoCurrencyCode = "USD",
            MatchStatus = "Unmatched",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _ctx.Db.BankTransactions.AddRange(existing, removed);
        await _ctx.Db.SaveChangesAsync();

        _plaid
            .Setup(p => p.SyncTransactionsAsync(It.IsAny<PlaidRuntimeSettings>(), "access-token", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidTransactionsSyncResult(
                "next-cursor",
                [],
                [
                    new PlaidSyncedTransaction(
                        "txn-modified",
                        "account-id",
                        new DateTime(2026, 06, 03, 0, 0, 0, DateTimeKind.Utc),
                        null,
                        "Posted rent",
                        "Emily Chen",
                        -1400m,
                        "USD",
                        "Transfer",
                        "{}"),
                ],
                ["txn-removed"],
                "request-id"));

        await _sut.SyncPlaidConnectionAsync(1, connection.Id);

        await _ctx.Db.Entry(existing).ReloadAsync();
        await _ctx.Db.Entry(removed).ReloadAsync();
        existing.Amount.Should().Be(1400m);
        existing.Description.Should().Be("Posted rent");
        existing.MatchStatus.Should().Be("Unmatched");
        existing.MatchConfidence.Should().BeNull();
        existing.Notes.Should().Contain("review");
        removed.MatchStatus.Should().Be("Removed");
        removed.Notes.Should().Be("Removed by Plaid sync.");
    }

    private static ImportBankTransactionsRequest BankImport(string providerId, DateTime postedAt, string? merchant, decimal amount) =>
        new()
        {
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Transactions =
            [
                new ImportBankTransactionItem
                {
                    ProviderTransactionId = providerId,
                    PostedAt = postedAt,
                    Description = "ACH CREDIT",
                    MerchantName = merchant,
                    Amount = amount,
                },
            ],
        };

    private static BankConnection SeedBankConnectionInto(MigratedPostgreSqlTestContext ctx)
    {
        var now = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var connection = new BankConnection
        {
            PortfolioId = 1,
            Provider = "Manual",
            InstitutionName = "Test Bank",
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Db.BankConnections.Add(connection);
        ctx.Db.SaveChanges();
        return connection;
    }

    private static void SeedBankTransactionInto(
        MigratedPostgreSqlTestContext ctx,
        int connectionId,
        string providerTransactionId,
        DateTime postedAt,
        string matchStatus)
    {
        ctx.Db.BankTransactions.Add(new BankTransaction
        {
            PortfolioId = 1,
            BankConnectionId = connectionId,
            ProviderTransactionId = providerTransactionId,
            PostedAt = postedAt,
            Description = providerTransactionId,
            Amount = 100m,
            IsoCurrencyCode = "USD",
            MatchStatus = matchStatus,
            CreatedAt = postedAt,
            UpdatedAt = postedAt,
        });
    }

    private static void AssignRoute(MigratedPostgreSqlTestContext ctx, int transactionId, int propertyId)
    {
        var transaction = ctx.Db.BankTransactions.Single(row => row.Id == transactionId);
        transaction.PropertyId = propertyId;
        transaction.UpdatedAt = transaction.UpdatedAt.AddTicks(1);
        ctx.Db.SaveChanges();
    }

    private static DateTime TransactionUpdatedAt(MigratedPostgreSqlTestContext ctx, int transactionId) =>
        ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => row.UpdatedAt)
            .Single();

    private static Property SeedRouteProperty(MigratedPostgreSqlTestContext ctx, string name)
    {
        var now = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = 1,
            Name = name,
            AddressLine1 = "1 Route Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Db.Properties.Add(property);
        ctx.Db.SaveChanges();
        return property;
    }

    private TenantLedgerEntry SeedRentPaymentFor(string firstName, string lastName, decimal amount, DateTime paidAt, string leaseNumber) =>
        SeedRentPaymentInto(_ctx, firstName, lastName, amount, paidAt, leaseNumber);

    private static TenantLedgerEntry SeedRentPaymentInto(
        MigratedPostgreSqlTestContext ctx,
        string firstName,
        string lastName,
        decimal amount,
        DateTime paidAt,
        string leaseNumber,
        Property? property = null)
    {
        property ??= new Property
        {
            PortfolioId = 1,
            Name = "Short North Condo",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = leaseNumber,
            MarketRent = amount,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = firstName,
            LastName = lastName,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var relationship = new LeaseManagement
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            RelationshipNumber = leaseNumber,
            PossessionGivenAtUtc = paidAt.AddMonths(-12),
            CreatedAtUtc = paidAt,
            CreatedByUserId = 1,
            UpdatedAtUtc = paidAt,
            RowVersion = Guid.NewGuid(),
        };
        relationship.Parties.Add(new LeaseManagementParty
        {
            PortfolioId = 1,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(paidAt.AddMonths(-12)),
            ChangeReason = "Test setup",
            CreatedAtUtc = paidAt,
            CreatedByUserId = 1,
        });
        var account = new TenantAccount
        {
            PortfolioId = 1,
            LeaseManagement = relationship,
            AccountNumber = $"TA-{leaseNumber}",
            Currency = "USD",
            OpenedAtUtc = paidAt.AddMonths(-12),
            CreatedAtUtc = paidAt,
            CreatedByUserId = 1,
        };
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidAt),
            PostedAtUtc = paidAt,
            Description = "Rent payment",
            BusinessKey = $"test-receipt:{leaseNumber}",
            CreatedByUserId = 1,
        };
        ctx.Db.TenantLedgerEntries.Add(receipt);
        ctx.Db.SaveChanges();
        return receipt;
    }

    private BankingService CreateServiceFor(MigratedPostgreSqlTestContext ctx, PlaidOptions? options = null)
        => CreateServiceFor(ctx.Db, ctx.ConnectionString, options);

    private BankingService CreateServiceFor(
        RentalCommand.Data.RentalCommandDbContext db,
        string connectionString,
        PlaidOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<PreparePlaidTokenExchangeCommand, PreparePlaidTokenExchangeResult, PreparePlaidTokenExchangeHandler>();
        services.AddAtomicCommandHandler<AdmitPlaidTokenExchangeCommand, AdmitPlaidTokenExchangeResult, AdmitPlaidTokenExchangeHandler>();
        services.AddAtomicCommandHandler<RecordPlaidTokenExchangeReceiptCommand, RecordPlaidTokenExchangeReceiptResult, RecordPlaidTokenExchangeReceiptHandler>();
        services.AddAtomicCommandHandler<ApplyPlaidConnectionCommand, ApplyPlaidConnectionResult, ApplyPlaidConnectionHandler>();
        services.AddAtomicCommandHandler<ApplyPlaidSyncCommand, ApplyPlaidSyncResult, ApplyPlaidSyncHandler>();
        services.AddAtomicCommandHandler<ImportBankTransactionsCommand, ImportBankTransactionsResult, ImportBankTransactionsHandler>();
        services.AddAtomicCommandHandler<ReconcileBankTransactionCommand, ReconcileBankTransactionResult, ReconcileBankTransactionHandler>();
        services.AddAtomicCommandHandler<RouteBankTransactionCommand, RouteBankTransactionResult, RouteBankTransactionHandler>();
        services.AddDbContext<RentalCommand.Data.RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        _atomicHosts.Add(scope);
        _atomicHosts.Add(provider);
        return new BankingService(
            db,
            new EphemeralDataProtectionProvider(),
            _plaid.Object,
            scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>(),
            Options.Create(options ?? new PlaidOptions
            {
                Environment = "sandbox",
                ClientId = "client-id",
                Secret = "secret",
            }),
            TimeProvider.System);
    }

    private TenantLedgerEntry SeedRentPayment(DateTime paidAt) =>
        SeedRentPaymentInto(_ctx, "Emily", "Chen", 1400m, paidAt, "L2024-008");

    private BankTransactionMutationRequest Mutation(int transactionId, string key)
    {
        var updatedAt = _ctx.Db.BankTransactions.AsNoTracking()
            .Where(row => row.Id == transactionId)
            .Select(row => row.UpdatedAt)
            .Single();
        return new BankTransactionMutationRequest
        {
            OperationKey = key,
            ExpectedUpdatedAtUtc = updatedAt,
        };
    }

    private static TenantLedgerEntry SeedTenantTransfer(
        MigratedPostgreSqlTestContext ctx,
        DateTime postedAt,
        decimal amount,
        TenantLedgerEntryType entryType,
        TenantLedgerDirection direction)
    {
        var accountSeed = SeedRentPaymentInto(
            ctx,
            "Transfer",
            "Tenant",
            9999.99m,
            postedAt.AddMonths(-1),
            $"L-XFER-{Guid.NewGuid():N}"[..14]);
        var transfer = new TenantLedgerEntry
        {
            PortfolioId = 1,
            TenantAccountId = accountSeed.TenantAccountId,
            EntryType = entryType,
            Direction = direction,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(postedAt),
            PostedAtUtc = postedAt,
            Description = "Tenant account transfer",
            BusinessKey = $"test-transfer:{Guid.NewGuid():N}",
            TransferPublicId = Guid.NewGuid(),
            CreatedByUserId = 1,
        };
        ctx.Db.TenantLedgerEntries.Add(transfer);
        ctx.Db.SaveChanges();
        ctx.Db.Entry(transfer).Reference(row => row.TenantAccount).Load();
        ctx.Db.Entry(transfer.TenantAccount!).Reference(row => row.LeaseManagement).Load();
        ctx.Db.Entry(transfer.TenantAccount!.LeaseManagement!).Reference(row => row.Property).Load();
        return transfer;
    }

    private Expense SeedExpense(DateTime paidAt, decimal amount, string vendorName = "Hardware Store", string description = "Hardware supply")
    {
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Short North Condo",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var vendor = new Vendor
        {
            PortfolioId = 1,
            Name = vendorName,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var expense = new Expense
        {
            PortfolioId = 1,
            OperationalScope = ExpenseOperationalScope.Property,
            Property = property,
            Vendor = vendor,
            Category = ScheduleECategory.Repairs,
            Description = description,
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = paidAt,
            PaidAt = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _ctx.Db.Expenses.Add(expense);
        _ctx.Db.SaveChanges();
        return expense;
    }

    private LoanPayment SeedLoanPayment(DateTime paidAt, decimal amount)
    {
        var property = SeedRouteProperty(_ctx, $"Loan property {Guid.NewGuid():N}");
        var loan = new Loan
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            Lender = "First QA Mortgage",
            OriginalAmount = 200000m,
            CurrentBalance = 180000m,
            AnnualInterestRatePct = 6m,
            TermMonths = 360,
            StartDate = paidAt.AddYears(-1),
            DayOfMonthDue = paidAt.Day,
            MonthlyPrincipalInterest = amount,
            Status = LoanStatus.Active,
            CreatedAt = paidAt.AddYears(-1),
            UpdatedAt = paidAt,
        };
        var payment = new LoanPayment
        {
            PortfolioId = 1,
            Loan = loan,
            PeriodKey = paidAt.ToString("yyyy-MM"),
            DueDate = paidAt,
            PaidDate = paidAt,
            InterestAmount = 600m,
            PrincipalAmount = amount - 600m,
            TotalAmount = amount,
            BalanceAfter = 180000m - (amount - 600m),
            Status = LoanPaymentStatus.Paid,
            CreatedAt = paidAt,
        };
        _ctx.Db.LoanPayments.Add(payment);
        _ctx.Db.SaveChanges();
        return payment;
    }

    private OwnerDistribution SeedOwnerDistribution(DateTime paidAt, decimal amount)
    {
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            OwnerEntityType = OwnerEntityType.LLC,
            Name = "Blue Door Residential LLC",
            CreatedAt = paidAt.AddYears(-1),
            UpdatedAt = paidAt,
        };
        var distribution = new OwnerDistribution
        {
            PortfolioId = 1,
            OwnerEntity = owner,
            Date = paidAt,
            Amount = amount,
            Method = DistributionMethod.Ach,
            Status = OwnerDistributionStatus.Approved,
            ApprovedAt = paidAt,
            ApprovedBusinessDate = paidAt,
            ApprovedByUserId = 1,
            BankReference = "DIST-202606-O01",
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _ctx.Db.OwnerDistributions.Add(distribution);
        _ctx.Db.SaveChanges();
        return distribution;
    }

    private BankingService CreateService(PlaidOptions? options = null) => CreateServiceFor(_ctx, options);

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "banking-service-test";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
