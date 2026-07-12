using RentalCommand.Core.Atomic;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Core.Accounting;

/// <summary>
/// Immutable, bounded provider response admitted after every remote call has completed. The pull
/// claim token and provider batch identity fence both ownership and replay.
/// </summary>
public sealed record ApplyAccountingPullResultCommand(
    int PortfolioId,
    int AccountingConnectionId,
    Guid PullClaimToken,
    string ProviderBatchIdentity,
    IReadOnlyList<ExtCustomerDto> Customers,
    IReadOnlyList<ExtVendorDto> Vendors,
    IReadOnlyList<ExtAccountDto> Accounts,
    IReadOnlyList<ExtPaymentDto> Payments,
    IReadOnlyList<ExtExpenseDto> Expenses,
    string NextCursorsJson,
    DateTime AppliedAtUtc) : IAtomicCommandData;

public sealed record ApplyAccountingPullResult(
    int CustomersMapped,
    int VendorsMapped,
    int AccountsMapped,
    int PaymentsImported,
    int ExpensesImported,
    int NeedsReview) : IAtomicResultData;

/// <summary>
/// PostgreSQL-owned accounting merge boundary. Its implementation executes one set statement;
/// handlers cannot obtain a DbContext, connection, transaction, or raw-SQL escape hatch.
/// </summary>
public interface IAtomicAccountingPersistence
{
    Task<ApplyAccountingPullResult> ApplyPullAsync(
        ApplyAccountingPullResultCommand command,
        CancellationToken ct = default);
}
