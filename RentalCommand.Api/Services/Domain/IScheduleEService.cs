using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Builds a read-only IRS Schedule E income/expense report for a portfolio year. All data is derived
/// from existing Payments and Expenses — no new records are created.
/// </summary>
public interface IScheduleEService
{
    /// <summary>
    /// Returns a per-property Schedule E report for <paramref name="year"/>, with every source
    /// constrained by the caller's current
    /// database-validated report capability and property scope.
    /// </summary>
    Task<ScheduleEReport> GetReportAsync(
        WorkspaceReadScope scope,
        int year,
        int? propertyId = null,
        CancellationToken ct = default);
}
