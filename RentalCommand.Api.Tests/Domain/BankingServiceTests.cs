using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class BankingServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly BankingService _sut;
    private readonly Mock<IPlaidBankingProvider> _plaid = new();

    public BankingServiceTests()
    {
        _plaid
            .Setup(p => p.SyncTransactionsAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidTransactionsSyncResult(null, [], [], [], "request-id"));
        _sut = CreateService();
    }

    public void Dispose() => _ctx.Dispose();

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
                    PostedAt = payment.PaidDate!.Value,
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
                    PostedAt = payment.PaidDate!.Value,
                    Description = "Duplicate line",
                    Amount = payment.Amount,
                },
            ],
        });

        first.ImportedCount.Should().Be(1);
        first.Transactions.Single().SuggestedMatch.Should().NotBeNull();
        first.Transactions.Single().SuggestedMatch!.EntityType.Should().Be("Payment");
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
                    PostedAt = payment.PaidDate!.Value,
                    Description = "Rent deposit",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;

        var matched = await _sut.MatchAsync(1, transactionId, new MatchBankTransactionRequest
        {
            EntityType = "Payment",
            EntityId = payment.Id,
        });
        var cleared = await _sut.ClearMatchAsync(1, transactionId);

        matched.Should().NotBeNull();
        matched!.MatchStatus.Should().Be("Matched");
        matched.MatchedPaymentId.Should().Be(payment.Id);
        cleared.Should().NotBeNull();
        cleared!.MatchStatus.Should().Be("Unmatched");
        cleared.MatchedPaymentId.Should().BeNull();
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

        _ctx.Db.AuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankConnection" &&
            a.EntityId == connection.Id &&
            a.Operation == AuditLogOperation.Created &&
            a.NewValues != null &&
            a.NewValues.Contains("\"institutionName\":\"Test Bank\""));
        _ctx.Db.AuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankTransaction" &&
            a.EntityId == transaction.Id &&
            a.Operation == AuditLogOperation.Created &&
            a.NewValues != null &&
            a.NewValues.Contains("\"providerTransactionId\":\"audit-import-1\""));
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
                    PostedAt = payment.PaidDate!.Value,
                    Description = "Rent deposit",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        _ctx.Db.AuditLogs.RemoveRange(_ctx.Db.AuditLogs);
        await _ctx.Db.SaveChangesAsync();

        await _sut.MatchAsync(1, transactionId, new MatchBankTransactionRequest
        {
            EntityType = "Payment",
            EntityId = payment.Id,
        });

        var log = _ctx.Db.AuditLogs.Should().ContainSingle(a =>
            a.EntityType == "BankTransaction" &&
            a.EntityId == transactionId &&
            a.Operation == AuditLogOperation.Updated).Subject;
        log.OldValues.Should().Contain("\"matchStatus\":\"Unmatched\"");
        log.NewValues.Should().Contain("\"matchStatus\":\"Matched\"");
        log.NewValues.Should().Contain($"\"matchedPaymentId\":{payment.Id}");
        log.ChangeReason.Should().Contain("matched to Payment");
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
                    PostedAt = payment.PaidDate!.Value,
                    Description = "ACH CREDIT EMILY CHEN RENT",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;

        // A suggested match shows up in the review queue.
        var queue = await _sut.GetReviewQueueAsync(1);
        queue.Count.Should().Be(1);
        var item = queue.Items.Single();
        item.Transaction.Id.Should().Be(transactionId);
        item.Suggestion.EntityType.Should().Be("Payment");
        item.Suggestion.EntityId.Should().Be(payment.Id);

        // Confirming links the payment and removes the line from the queue.
        var confirmed = await _sut.ConfirmMatchAsync(1, transactionId, new ConfirmBankMatchRequest());
        confirmed.Should().NotBeNull();
        confirmed!.MatchStatus.Should().Be("Matched");
        confirmed.MatchedPaymentId.Should().Be(payment.Id);
        confirmed.SuggestedMatch.Should().BeNull();
        (await _sut.GetReviewQueueAsync(1)).Count.Should().Be(0);
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
                    PostedAt = payment.PaidDate!.Value,
                    Description = "ACH CREDIT EMILY CHEN RENT",
                    Amount = payment.Amount,
                },
            ],
        });
        var transactionId = imported.Transactions.Single().Id;
        (await _sut.GetReviewQueueAsync(1)).Count.Should().Be(1);

        var dismissed = await _sut.DismissMatchAsync(1, transactionId);

        dismissed.Should().NotBeNull();
        dismissed!.MatchStatus.Should().Be("Dismissed");
        dismissed.MatchedPaymentId.Should().BeNull();
        dismissed.SuggestedMatch.Should().BeNull();
        (await _sut.GetReviewQueueAsync(1)).Count.Should().Be(0);
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

        var confirmed = await _sut.ConfirmMatchAsync(1, transactionId, new ConfirmBankMatchRequest
        {
            ExpenseId = expense.Id,
        });

        confirmed.Should().NotBeNull();
        confirmed!.MatchStatus.Should().Be("Matched");
        confirmed.MatchedExpenseId.Should().Be(expense.Id);
        confirmed.MatchedPaymentId.Should().BeNull();
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
        suggestion!.EntityType.Should().Be("Payment");
        suggestion.EntityId.Should().Be(carlos.Id);
        suggestion.EntityId.Should().NotBe(emily.Id);
    }

    [Fact]
    public async Task SuggestMatch_NameMatchYieldsHigherConfidenceThanDateOnlyMatch()
    {
        var date = new DateTime(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);

        // Named match: merchant text contains the tenant name.
        var namedCtx = new SqliteTestContext();
        try
        {
            SeedRentPaymentInto(namedCtx, "Carlos", "Reyes", 1500m, date, "L-1");
            var namedSvc = CreateServiceFor(namedCtx);
            var namedResult = await namedSvc.ImportAsync(1, BankImport("named-1", date, "Carlos Reyes", 1500m));
            var namedScore = namedResult.Transactions.Single().SuggestedMatch!.Confidence;

            // Date-only match: no merchant name overlap, same amount + date.
            var anonCtx = new SqliteTestContext();
            try
            {
                SeedRentPaymentInto(anonCtx, "Carlos", "Reyes", 1500m, date, "L-1");
                var anonSvc = CreateServiceFor(anonCtx);
                var anonResult = await anonSvc.ImportAsync(1, BankImport("anon-1", date, null, 1500m));
                var anonScore = anonResult.Transactions.Single().SuggestedMatch!.Confidence;

                namedScore.Should().BeGreaterThan(anonScore);
            }
            finally { anonCtx.Dispose(); }
        }
        finally { namedCtx.Dispose(); }
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

        var ignored = await _sut.IgnoreTransactionAsync(1, transactionId);

        ignored.Should().NotBeNull();
        ignored!.MatchStatus.Should().Be("Removed");
        ignored.MatchedPaymentId.Should().BeNull();
        ignored.MatchedExpenseId.Should().BeNull();
        ignored.SuggestedMatch.Should().BeNull();
        ignored.Notes.Should().Contain("personal");

        // Gone from the unmatched feed and the review queue...
        var unmatched = await _sut.ListTransactionsAsync(1, "Unmatched");
        unmatched.Items.Should().NotContain(t => t.Id == transactionId);
        (await _sut.GetReviewQueueAsync(1)).Items.Should().NotContain(i => i.Transaction.Id == transactionId);

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
        var result = await _sut.IgnoreTransactionAsync(999, transactionId);
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
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
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
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
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
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        var payment = SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-target");
        SeedRentPaymentInto(ctx, "Old", "Candidate", 1400m, postedAt.AddMonths(-6), "L-old");
        SeedRentPaymentInto(ctx, "Wrong", "Amount", 1999m, postedAt, "L-wrong");

        await sut.ImportAsync(1, BankImport("queue-prefilter", postedAt, "Emily Chen", 1400m));
        executedSql.Clear();

        var queue = await sut.GetReviewQueueAsync(1);

        var item = queue.Items.Should().ContainSingle().Subject;
        item.Transaction.SuggestedMatch.Should().NotBeNull();
        item.Transaction.SuggestedMatch!.EntityType.Should().Be("Payment");
        item.Transaction.SuggestedMatch.EntityId.Should().Be(payment.Id);

        var reviewQueueSql = executedSql
            .Where(sql => sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase))
            .ToList();
        reviewQueueSql.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase),
            "the review queue count must use the DB-side suggestion predicate instead of counting mapped rows");
        reviewQueueSql.Should().Contain(sql =>
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase),
            "the review queue row query must prefilter suggestible transactions in SQL before scoring");

        var paymentCandidateSql = executedSql
            .Where(sql => sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        paymentCandidateSql.Should().Contain(sql =>
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
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-emily");
        var carlos = SeedRentPaymentInto(ctx, "Carlos", "Reyes", 1400m, postedAt, "L-carlos");

        await sut.ImportAsync(1, BankImport("queue-rank", postedAt, "Carlos Reyes", 1400m));
        executedSql.Clear();

        var queue = await sut.GetReviewQueueAsync(1);

        var item = queue.Items.Should().ContainSingle().Subject;
        item.Transaction.SuggestedMatch.Should().NotBeNull();
        item.Transaction.SuggestedMatch!.EntityType.Should().Be("Payment");
        item.Transaction.SuggestedMatch.EntityId.Should().Be(carlos.Id);

        var paymentCandidateSql = executedSql
            .Where(sql => sql.Contains("FROM \"Payments\"", StringComparison.OrdinalIgnoreCase))
            .ToList();

        paymentCandidateSql.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("CASE", StringComparison.OrdinalIgnoreCase),
            "bank suggestion candidate ranking must run in SQL, not after materializing every same-amount/date candidate");
    }

    [Fact]
    public async Task ReviewQueue_PagesSuggestibleTransactionsInSql()
    {
        var executedSql = new List<string>();
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        var postedAt = new DateTime(2026, 06, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedRentPaymentInto(ctx, "Emily", "Chen", 1400m, postedAt, "L-emily");
        SeedRentPaymentInto(ctx, "Carlos", "Reyes", 1450m, postedAt.AddDays(1), "L-carlos");
        SeedRentPaymentInto(ctx, "Maya", "Patel", 1500m, postedAt.AddDays(2), "L-maya");

        await sut.ImportAsync(1, BankImport("queue-page-1", postedAt, "Emily Chen", 1400m));
        await sut.ImportAsync(1, BankImport("queue-page-2", postedAt.AddDays(1), "Carlos Reyes", 1450m));
        await sut.ImportAsync(1, BankImport("queue-page-3", postedAt.AddDays(2), "Maya Patel", 1500m));
        executedSql.Clear();

        var queue = await sut.GetReviewQueueAsync(1, skip: 1, take: 1);

        queue.Count.Should().Be(3);
        queue.Skip.Should().Be(1);
        queue.Take.Should().Be(1);
        queue.Items.Should().ContainSingle();
        queue.Items.Single().Transaction.MerchantName.Should().Be("Carlos Reyes");

        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankTransactions\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase),
            "the review queue must page suggestible rows in SQL before mapping suggestions");
    }

    [Fact]
    public async Task ListTransactionsAsync_PagesFilteredTransactionsInSql()
    {
        var executedSql = new List<string>();
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
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
        var executedSql = new List<string>();
        using var ctx = new SqliteTestContext([new RecordingCommandInterceptor(executedSql)]);
        var sut = CreateServiceFor(ctx);
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(
                It.IsAny<PlaidRuntimeSettings>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-sandbox-token", "item-id-1", "request-id-1"));

        await sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
        {
            PublicToken = "public-sandbox-token-1",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
        });
        executedSql.Clear();

        await sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
        {
            PublicToken = "public-sandbox-token-2",
            InstitutionName = "Plaid Test Bank",
            AccountId = "account-id-1",
            AccountName = "Operating Checking",
        });

        ctx.Db.BankConnections.Should().ContainSingle();
        executedSql.Should().Contain(sql =>
            sql.Contains("FROM \"BankConnections\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ExternalItemIdHash", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ExternalAccountIdHash", StringComparison.OrdinalIgnoreCase),
            "Plaid reconnect must use queryable lookup hashes instead of materializing rows and decrypting each one");
    }

    [Fact]
    public async Task SyncPlaidConnectionAsync_ImportsNewTransactions_UpdatesCursor_AndDeduplicates()
    {
        _plaid
            .Setup(p => p.ExchangePublicTokenAsync(It.IsAny<PlaidRuntimeSettings>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaidExchangeResult("access-token", "item-id", "request-id"));
        var connection = await _sut.ExchangePlaidPublicTokenAsync(1, new ExchangePlaidPublicTokenRequest
        {
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
        syncResult.Transactions.Should().ContainSingle(t => t.ProviderTransactionId == "txn-1")
            .Which.Amount.Should().Be(1400m);

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
            MatchedPaymentId = null,
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

    private static BankConnection SeedBankConnectionInto(SqliteTestContext ctx)
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
        SqliteTestContext ctx,
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

    private Payment SeedRentPaymentFor(string firstName, string lastName, decimal amount, DateTime paidAt, string leaseNumber) =>
        SeedRentPaymentInto(_ctx, firstName, lastName, amount, paidAt, leaseNumber);

    private static Payment SeedRentPaymentInto(
        SqliteTestContext ctx, string firstName, string lastName, decimal amount, DateTime paidAt, string leaseNumber)
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
        var lease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = leaseNumber,
            Status = LeaseStatus.Active,
            StartDate = paidAt.AddMonths(-12),
            EndDate = paidAt.AddMonths(12),
            MonthlyRent = amount,
            SecurityDeposit = amount,
            LateFeeAmount = 70m,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var payment = new Payment
        {
            PortfolioId = 1,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = amount,
            DueDate = paidAt,
            PaidDate = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        ctx.Db.Payments.Add(payment);
        ctx.Db.SaveChanges();
        return payment;
    }

    private BankingService CreateServiceFor(SqliteTestContext ctx) =>
        new(
            ctx.Db,
            new EphemeralDataProtectionProvider(),
            _plaid.Object,
            new RentalCommand.Api.Services.AuditTrailService(ctx.Db, new RentalCommand.Data.Auditing.AuditScope()),
            Options.Create(new PlaidOptions
            {
                Environment = "sandbox",
                ClientId = "client-id",
                Secret = "secret",
            }));

    private Payment SeedRentPayment(DateTime paidAt)
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
            MarketRent = 1400m,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Emily",
            LastName = "Chen",
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var lease = new Lease
        {
            PortfolioId = 1,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L2024-008",
            Status = LeaseStatus.Active,
            StartDate = paidAt.AddMonths(-12),
            EndDate = paidAt.AddMonths(12),
            MonthlyRent = 1400m,
            SecurityDeposit = 1400m,
            LateFeeAmount = 70m,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        var payment = new Payment
        {
            PortfolioId = 1,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1400m,
            DueDate = paidAt,
            PaidDate = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _ctx.Db.Payments.Add(payment);
        _ctx.Db.SaveChanges();
        return payment;
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

    private BankingService CreateService(PlaidOptions? options = null) =>
        new(
            _ctx.Db,
            new EphemeralDataProtectionProvider(),
            _plaid.Object,
            new RentalCommand.Api.Services.AuditTrailService(_ctx.Db, new RentalCommand.Data.Auditing.AuditScope()),
            Options.Create(options ?? new PlaidOptions
            {
                Environment = "sandbox",
                ClientId = "client-id",
                Secret = "secret",
            }));

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
