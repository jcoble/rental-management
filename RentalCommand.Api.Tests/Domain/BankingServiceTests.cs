using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
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
            Options.Create(options ?? new PlaidOptions
            {
                Environment = "sandbox",
                ClientId = "client-id",
                Secret = "secret",
            }));
}
