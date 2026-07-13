using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeDraftService
{
    Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(
        int portfolioId,
        string? status,
        ListQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Runs one set-based PostgreSQL generation command. With <paramref name="request"/> null/empty
    /// it evaluates every due enabled policy in the portfolio. Canonical relationship/account/ledger
    /// identifiers narrow the same server-side command; an explicit lifecycle type in a selected
    /// tenant/relationship/account scope permits an operator-requested draft outside its scheduled
    /// lead window.
    /// </summary>
    Task<GenerateNoticeDraftsResponse> GenerateAsync(
        int portfolioId,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default);
    Task<NoticeDraftResponse?> UpdateAsync(int portfolioId, int id, UpdateNoticeDraftRequest request, CancellationToken ct = default);
    Task<NoticeDraftResponse?> DismissAsync(int portfolioId, int id, CancellationToken ct = default);
}
