using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only financial reporting for the caller's portfolio. Aggregates expenses by IRS Schedule E
/// category and rolls up payment-collection status. Always scoped to the caller's portfolio id (from the
/// JWT claim, never a client parameter). There is no create/update/delete for accounting.
/// </summary>
public interface IAccountingService
{
    Task<AccountingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Plain-English money snapshot (month-to-date + trailing 30 days) for a one-glance card and the
    /// daily briefing: money in, money out, what's kept, and who's behind, each with a jargon-free
    /// explanation.
    /// </summary>
    Task<MoneySnapshotResponse> GetSnapshotAsync(int portfolioId, CancellationToken ct = default);

    Task<AccountingReportsResponse> GetReportsAsync(int portfolioId, CancellationToken ct = default);
    Task<AccountingTransactionsResponse> GetTransactionsAsync(
        int portfolioId,
        AccountingTransactionsQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Gathers every section of the year-end accountant packet (Schedule E summary, per-property P&amp;L,
    /// month-by-month cash flow, and rent roll) for <paramref name="year"/>, scoped to the portfolio.
    /// Reuses the Schedule E computation so the packet reconciles with the existing CSV/report.
    /// </summary>
    Task<YearEndPacketData> GetYearEndPacketDataAsync(int portfolioId, int year, CancellationToken ct = default);

    /// <summary>
    /// Renders the year-end packet to PDF bytes (cover + Schedule E summary + per-property P&amp;L +
    /// cash-flow summary + rent roll) for <paramref name="year"/>, scoped to the portfolio.
    /// </summary>
    Task<byte[]> GetYearEndPacketAsync(int portfolioId, int year, CancellationToken ct = default);
}
