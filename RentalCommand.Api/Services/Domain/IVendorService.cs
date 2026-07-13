using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Vendor"/>. Every query is filtered by the caller's
/// portfolio id. Soft-delete is used for removal; mutations broadcast realtime updates.
/// </summary>
public interface IVendorService
{
    Task<IReadOnlyList<VendorResponse>> ListAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<VendorListResponse> ListPageAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<VendorResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<VendorResponse?> CreateAsync(WorkspaceReadScope scope, CreateVendorRequest request, CancellationToken ct = default);
    Task<VendorResponse?> UpdateAsync(WorkspaceReadScope scope, int id, UpdateVendorRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    /// <summary>
    /// Enqueue a friendly SMS asking the vendor to send their W-9 for 1099 tax reporting, and audit it.
    /// Sending the text is the action — nothing is persisted on the vendor. Returns an outcome that drives
    /// the HTTP mapping (not found / no phone on file / queued).
    /// </summary>
    Task<RequestW9Result> RequestW9Async(
        WorkspaceReadScope scope,
        int id,
        string clientOperationId,
        int? changedByUserId,
        CancellationToken ct = default);
}

/// <summary>Outcome of a W-9 request attempt; <see cref="RequestW9Outcome"/> drives the HTTP mapping.</summary>
public sealed record RequestW9Result(RequestW9Outcome Outcome, string? Phone)
{
    public static RequestW9Result Queued(string phone) => new(RequestW9Outcome.Queued, phone);
    public static RequestW9Result NotFound() => new(RequestW9Outcome.NotFound, null);
    public static RequestW9Result NoPhone() => new(RequestW9Outcome.VendorHasNoPhone, null);
}

public enum RequestW9Outcome
{
    Queued,
    NotFound,
    VendorHasNoPhone,
}
