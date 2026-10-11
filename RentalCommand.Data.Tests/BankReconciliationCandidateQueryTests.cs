using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Data.Banking;

namespace RentalCommand.Data.Tests;

public sealed class BankReconciliationCandidateQueryTests
{
    [Fact]
    public void Every_candidate_family_translates_as_one_server_side_statement()
    {
        using var db = Context();
        var transaction = new BankTransaction
        {
            Id = 41,
            PortfolioId = 7,
            BankConnectionId = 11,
            PropertyId = 19,
            PostedAt = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc),
            Amount = -125m,
            MatchStatus = "Unmatched",
        };

        var statements = new[]
        {
            BankReconciliationCandidateQuery.EligibleTenantLedgerEntries(
                    Transaction(db, transaction), db.TenantLedgerEntries.AsNoTracking())
                .Where(candidate => candidate.Target.Id == 101)
                .ToQueryString(),
            BankReconciliationCandidateQuery.EligibleExpenses(
                    Transaction(db, transaction), db.Expenses.AsNoTracking())
                .Where(candidate => candidate.Target.Id == 102)
                .ToQueryString(),
            BankReconciliationCandidateQuery.EligibleLoanPayments(
                    Transaction(db, transaction), LoanPaymentEffectiveQuery.From(db))
                .Where(candidate => candidate.Target.Id == 103)
                .ToQueryString(),
            BankReconciliationCandidateQuery.EligibleOwnerDistributions(
                    Transaction(db, transaction), db.OwnerDistributions.AsNoTracking())
                .Where(candidate => candidate.Target.Id == 104)
                .ToQueryString(),
            BankReconciliationCandidateQuery.EligibleTransfers(
                    Transaction(db, transaction), db.BankTransactions.AsNoTracking())
                .Where(candidate => candidate.Target.Id == 105)
                .ToQueryString(),
        };

        foreach (var sql in statements)
        {
            sql.Should().Contain("SELECT");
            sql.Should().Contain("WHERE");
            sql.Should().Contain("@transaction_Id");
            sql.Should().NotContain("ClientEvaluation");
            sql.TrimEnd().Should().NotEndWith(";",
                "each candidate selection and commit-time revalidation must be one translated SQL statement");
        }
    }

    private static IQueryable<BankTransaction> Transaction(
        RentalCommandDbContext db,
        BankTransaction transaction) =>
        db.BankTransactions.AsNoTracking().Where(row => row.Id == transaction.Id);

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);
}
