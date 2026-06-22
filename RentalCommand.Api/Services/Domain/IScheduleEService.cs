using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Builds a read-only IRS Schedule E income/expense report for a portfolio year. All data is derived
/// from existing Payments and Expenses — no new records are created.
/// </summary>
public interface IScheduleEService
{
    /// <summary>
    /// Returns a per-property Schedule E report for <paramref name="year"/>. Only properties that
    /// have at least one qualifying rent payment or expense in that year are included.
    /// </summary>
    Task<ScheduleEReport> GetReportAsync(int portfolioId, int year, int? propertyId = null, CancellationToken ct = default);
}
