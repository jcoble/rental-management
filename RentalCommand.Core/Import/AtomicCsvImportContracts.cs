using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Core.Import;

public sealed record AtomicUnitImportRow(
    int RowNumber,
    int? PropertyId,
    string? PropertyName,
    string UnitNumber,
    decimal Bedrooms,
    decimal Bathrooms,
    decimal MarketRent,
    string[] Errors) : IAtomicCommandData;

public sealed record AtomicUnitImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    int? CreatedId,
    int? PropertyId,
    string UnitNumber,
    decimal Bedrooms,
    decimal Bathrooms,
    decimal MarketRent,
    string[] Errors);

public sealed record AtomicUnitImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicUnitImportRowResult> Rows,
    IReadOnlyList<AtomicUnitImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows);

public enum AtomicCoreCsvImportDomain
{
    Property,
    Tenant,
    Expense,
    Loan,
}

public sealed record AtomicCoreCsvImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    int? CreatedId,
    int? RelatedId,
    string[] Errors);

public sealed record AtomicCoreCsvImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicCoreCsvImportRowResult> Rows,
    IReadOnlyList<AtomicCoreCsvImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows);

public sealed record AtomicPaymentCsvImportRowResult(
    int RowNumber,
    bool Valid,
    bool IsDuplicate,
    long? CreatedId,
    int? TenantAccountId,
    string[] Errors);

public sealed record AtomicPaymentCsvImportBatchResult(
    bool Authorized,
    IReadOnlyList<AtomicPaymentCsvImportRowResult> Rows,
    IReadOnlyList<AtomicPaymentCsvImportRowResult> CreatedRows,
    int TotalRows,
    int ValidRows,
    int CreatedCount,
    int DuplicateRows);

/// <summary>
/// Read-only Unit CSV preview. It uses the same PostgreSQL validator as the import,
/// but opens no command receipt and inserts no Units.
/// </summary>
public interface IUnitCsvImportPreviewQuery
{
    Task<AtomicUnitImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<AtomicUnitImportRow> rows,
        CancellationToken ct = default);
}

/// <summary>Read-only typed CSV preview with no receipt or write permit.</summary>
public interface ICoreCsvImportPreviewQuery
{
    Task<AtomicCoreCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        AtomicCoreCsvImportDomain domain,
        string rowsJson,
        CancellationToken ct = default);
}

/// <summary>Read-only payment CSV preview with no receipt or write permit.</summary>
public interface IPaymentCsvImportPreviewQuery
{
    Task<AtomicPaymentCsvImportBatchResult> PreviewAsync(
        WorkspaceReadScope scope,
        string rowsJson,
        CancellationToken ct = default);
}
