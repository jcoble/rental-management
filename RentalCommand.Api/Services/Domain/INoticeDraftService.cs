using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeDraftService
{
    Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(int portfolioId, string? status, CancellationToken ct = default);

    /// <summary>
    /// Generates notice drafts. With <paramref name="request"/> null/empty this runs portfolio-wide for
    /// every applicable lease and late payment. Supplying a tenant id scopes it to that tenant; supplying
    /// a notice type generates only that type (forcing renewal/move-out outside their usual window).
    /// </summary>
    Task<GenerateNoticeDraftsResponse> GenerateAsync(
        int portfolioId,
        GenerateNoticeDraftsRequest? request = null,
        CancellationToken ct = default);
    Task<NoticeDraftResponse?> UpdateAsync(int portfolioId, int id, UpdateNoticeDraftRequest request, CancellationToken ct = default);
    Task<NoticeDraftResponse?> DismissAsync(int portfolioId, int id, CancellationToken ct = default);
}
