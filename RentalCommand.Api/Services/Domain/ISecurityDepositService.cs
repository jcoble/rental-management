using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Manages security deposit holdings through their lifecycle: held → deductions applied → returned.
/// All operations are portfolio-scoped.
/// </summary>
public interface ISecurityDepositService
{
    /// <summary>List all holdings for the portfolio, optionally filtered by lease.</summary>
    Task<IReadOnlyList<SecurityDepositAccountResponse>> ListAsync(WorkspaceReadScope scope, int? leaseManagementId, CancellationToken ct = default);

    /// <summary>List one page of holdings with SQL-side sorting/paging and a total count.</summary>
    Task<SecurityDepositListResponse> ListPageAsync(WorkspaceReadScope scope, int? leaseManagementId, ListQuery query, CancellationToken ct = default);

    /// <summary>Get a single holding by id. Returns null when not found or out of scope.</summary>
    Task<SecurityDepositAccountResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    /// <summary>
    /// Render an itemised, photo-backed move-out statement PDF for the holding: deposit held, each
    /// deduction, any photos attached to the deposit, and the net refund (or amount owed). Returns null
    /// when the holding is not found or out of scope.
    /// </summary>
    Task<byte[]?> GetMoveOutStatementAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
}
