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
        first.Transactions.Single().SuggestedMatch.Should().NotBeNull();
        first.Transactions.Single().SuggestedMatch!.EntityType.Should().Be("TenantLedgerEntry");
        first.Transactions.Single().SuggestedMatch!.EntityId.Should().Be(payment.Id);
        duplicate.ImportedCount.Should().Be(0);
        duplicate.SkippedCount.Should().Be(1);
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

        var matched = await _sut.MatchAsync(_scope, transactionId, new MatchBankTransactionRequest
        {
            OperationKey = "match-payment",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            TenantAccountId = payment.TenantAccountId,
            TenantLedgerEntryId = payment.Id,
        });
        var cleared = await _sut.ClearMatchAsync(_scope, transactionId, Mutation(transactionId, "clear-match"));

        matched.Should().NotBeNull();
        matched!.MatchStatus.Should().Be("Matched");
        var matchedRow = _ctx.Db.BankTransactions.Single(row => row.Id == transactionId);
        matchedRow.MatchedTenantAccountId.Should().Be(payment.TenantAccountId);
        matchedRow.MatchedTenantLedgerEntryId.Should().Be(payment.Id);
        cleared.Should().NotBeNull();
        cleared!.MatchStatus.Should().Be("Unmatched");
        cleared.MatchedTenantLedgerEntryId.Should().BeNull();
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
            .SingleAsync(transaction => transaction.Id == transactionId))
            .MatchedTenantLedgerEntryId.Should().Be(payment.Id);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.reconcile" &&
            receipt.IdempotencyKey.EndsWith(":same-authorized-reconciliation")))
            .Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync(audit =>
            audit.CommandType == "banking.transaction.reconcile" &&
            audit.CommandIdempotencyKey.EndsWith(":same-authorized-reconciliation")))
            .Should().Be(1);
    }

    [Fact]
    public async Task RouteTransactionAsync_RequiresWorkspaceAdministratorAuthority()
    {
        var payment = SeedRentPayment(new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc));
        var propertyId = payment.TenantAccount!.LeaseManagement!.PropertyId;
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
                    PostedAt = payment.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    Description = "Needs property route",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        var propertyManager = _ctx.Db.SeedPropertyManagerScope(
            1, propertyId, "bank-routing-property-manager");
        var expectedUpdatedAt = TransactionUpdatedAt(_ctx, transactionId);

        var denied = async () => await _sut.RouteTransactionAsync(
            propertyManager,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "property-manager-route-denied",
                PropertyId = propertyId,
                ExpectedUpdatedAtUtc = expectedUpdatedAt,
            });
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();

        _ctx.Db.ChangeTracker.Clear();
        (await _ctx.Db.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.PropertyId)
            .SingleAsync()).Should().BeNull();
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":property-manager-route-denied")))
            .Should().Be(0);

        var routed = await _sut.RouteTransactionAsync(
            _scope,
            transactionId,
            new RouteBankTransactionRequest
            {
                OperationKey = "administrator-route-applied",
                PropertyId = propertyId,
                ExpectedUpdatedAtUtc = expectedUpdatedAt,
            });

        routed.Should().NotBeNull();
        routed!.PropertyId.Should().Be(propertyId);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "banking.transaction.route" &&
            receipt.IdempotencyKey.EndsWith(":administrator-route-applied")))
            .Should().Be(1);
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
        var confirmedRow = _ctx.Db.BankTransactions.Single(row => row.Id == transactionId);
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
                    PostedAt = expense.PaidAt!.Value,
                    Description = "HARDWARE STORE",
                    Amount = -expense.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        AssignRoute(_ctx, transactionId, expense.PropertyId!.Value);

        var confirmed = await _sut.ConfirmMatchAsync(_scope, transactionId, new ConfirmBankMatchRequest
        {
            OperationKey = "confirm-expense",
            ExpectedUpdatedAtUtc = TransactionUpdatedAt(_ctx, transactionId),
            ExpenseId = expense.Id,
        });

        confirmed.Should().NotBeNull();
        confirmed!.MatchStatus.Should().Be("Matched");
        var confirmedRow = _ctx.Db.BankTransactions.Single(row => row.Id == transactionId);
        confirmedRow.MatchedExpenseId.Should().Be(expense.Id);
        confirmedRow.MatchedTenantLedgerEntryId.Should().BeNull();
    }

    [Fact]
    public async Task SuggestMatch_PrefersCandidateWhoseNameMatchesTheMerchantLine()
    {
        // Two rent payments, same $1,500 amount and same date. One tenant's name (Carlos Reyes)
        // appears on the bank line's merchant text; the other (Emily Chen) does not. The named
        // candidate must win, even though both clear the amount + date gate.
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);
        var emily = SeedRentPaymentFor("Emily", "Chen", 1500m, date, "L-EMILY");
        var carlos = SeedRentPaymentFor("Carlos", "Reyes", 1500m, date, "L-CARLOS");

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

        var suggestion = imported.Transactions.Single().SuggestedMatch;
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
        SeedRentPaymentInto(namedCtx, "Carlos", "Reyes", 1500m, date, "L-1");
        var namedSvc = CreateServiceFor(namedCtx);
        var namedResult = await namedSvc.ImportAsync(1, BankImport("named-1", date, "Carlos Reyes", 1500m));
        var namedScore = namedResult.Transactions.Single().SuggestedMatch!.Confidence;

        // Date-only match: no merchant name overlap, same amount + date.
        await using var anonCtx = await _fixture.CreateContextAsync();
        SeedRentPaymentInto(anonCtx, "Carlos", "Reyes", 1500m, date, "L-1");
        var anonSvc = CreateServiceFor(anonCtx);
        var anonResult = await anonSvc.ImportAsync(1, BankImport("anon-1", date, null, 1500m));
        var anonScore = anonResult.Transactions.Single().SuggestedMatch!.Confidence;

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
            sql.Contains("ROW_NUMBER", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains(">=", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("<", StringComparison.OrdinalIgnoreCase),
            "bank suggestion candidates must be narrowed, scored, and ranked with the bank line in SQL before materialization");
    }

    [Fact]
    public async Task ReviewQueue_RanksPaymentSuggestionCandidatesInSql()
    {
        var executedSql = new List<string>();
        await using var ctx = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-emily");
        var carlos = SeedRentPaymentInto(ctx, "Carlos", "Reyes", 1400m, postedAt, "L-carlos");
        var scope = ctx.Db.SeedAdministratorScope(1, "bank-review-ranking");

        var rankedImport = await sut.ImportAsync(1, BankImport("queue-rank", postedAt, "Carlos Reyes", 1400m));
        AssignRoute(ctx, rankedImport.Transactions.Single().Id,
            carlos.TenantAccount!.LeaseManagement!.PropertyId);
        executedSql.Clear();

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
            sql.Contains("MembershipRoleAssignmentProperties", StringComparison.OrdinalIgnoreCase) &&
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

        var result = await _sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
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
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlaidRuntimeSettings _, string publicToken, CancellationToken _) =>
                new PlaidExchangeResult("access-sandbox-token", "item-id-1", $"request-{publicToken}"));

        await sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
        {
            ClientOperationId = "exchange-relink-1",
            PublicToken = "public-sandbox-token-1",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
        });
        await sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
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

        var first = await _sut.ExchangePlaidPublicTokenAsync(1, request);
        var replay = await _sut.ExchangePlaidPublicTokenAsync(1, request);

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

        await FluentActions.Invoking(() => _sut.ExchangePlaidPublicTokenAsync(1, request))
            .Should().ThrowAsync<HttpRequestException>();
        await FluentActions.Invoking(() => _sut.ExchangePlaidPublicTokenAsync(1, request))
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
        var connection = await _sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
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
        var connection = await _sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
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
        string leaseNumber)
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
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "4B",
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
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
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

    private Expense SeedExpense(DateTime paidAt, decimal amount)
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
            Name = "Hardware Store",
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var expense = new Expense
        {
            PortfolioId = 1,
            Property = property,
            Vendor = vendor,
            Category = ScheduleECategory.Repairs,
            Description = "Hardware supply",
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
