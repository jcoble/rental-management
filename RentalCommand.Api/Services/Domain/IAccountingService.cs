using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Read-only financial reporting for the caller's portfolio. Aggregates expenses by IRS Schedule E
/// category and rolls up payment-collection status. Always scoped to the caller's portfolio id (from the
/// JWT claim, never a client parameter). There is no create/update/delete for accounting.
/// </summary>
public interface IAccountingService
{
    Task<AccountingSummaryResponse> GetSummaryAsync(WorkspaceReadScope scope, CancellationToken ct = default);

    /// <summary>
    /// Plain-English money snapshot (month-to-date + trailing 30 days) for a one-glance card and the
    /// daily briefing: money in, money out, what's kept, and who's behind, each with a jargon-free
    /// explanation.
    /// </summary>
    Task<MoneySnapshotResponse> GetSnapshotAsync(WorkspaceReadScope scope, CancellationToken ct = default);

    /// <summary>
    /// The "Who's behind" list: one row per tenant account currently behind on rent, with amount owed,
    /// overdue-charge count, and a deep-link anchor. Uses the exact same past-due definition as the
    /// snapshot KPI's PastDueCount/PastDueAmount, so its exact totals always agree with the KPI.
    /// </summary>
    Task<PastDueResponse> GetPastDueAsync(
        WorkspaceReadScope scope,
        PastDueQuery query,
        CancellationToken ct = default);

    Task<AccountingReportsResponse> GetReportsAsync(WorkspaceReadScope scope, CancellationToken ct = default);
    Task<AccountingTransactionsResponse> GetTransactionsAsync(
        WorkspaceReadScope scope,
        AccountingTransactionsQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Gathers every section of the year-end accountant packet (Schedule E summary, per-property P&amp;L,
    /// month-by-month cash flow, and rent roll) for <paramref name="year"/>, scoped to the portfolio.
    /// Reuses the Schedule E computation so the packet reconciles with the existing CSV/report.
    /// </summary>
    Task<YearEndPacketData> GetYearEndPacketDataAsync(
        WorkspaceReadScope scope, int year, CancellationToken ct = default);

    /// <summary>
    /// Renders the year-end packet to PDF bytes (cover + Schedule E summary + per-property P&amp;L +
    /// cash-flow summary + rent roll) for <paramref name="year"/>, scoped to the portfolio.
    /// </summary>
    Task<byte[]> GetYearEndPacketAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default);
}
