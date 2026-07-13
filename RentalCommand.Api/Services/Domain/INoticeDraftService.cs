using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeDraftService
{
    Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(
        WorkspaceReadScope scope,
        string? status,
        ListQuery query,
        CancellationToken ct = default);

    Task<NoticeDraftResponse?> GetAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default);

    /// <summary>
    /// Runs one set-based PostgreSQL generation command. With <paramref name="request"/> null/empty
    /// it evaluates every due enabled policy in the portfolio. Canonical relationship/account/ledger
    /// identifiers narrow the same server-side command; an explicit lifecycle type in a selected
    /// tenant/relationship/account scope permits an operator-requested draft outside its scheduled
    /// lead window.
    /// </summary>
    Task<GenerateNoticeDraftsResponse> GenerateAsync(
        WorkspaceReadScope scope,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default);
    Task<NoticeDraftResponse?> UpdateAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateNoticeDraftRequest request,
        CancellationToken ct = default);
    Task<NoticeDraftResponse?> DismissAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default);
}
