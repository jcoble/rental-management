using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface INoticeDraftService
{
    Task<IReadOnlyList<NoticeDraftResponse>> ListAsync(int portfolioId, string? status, CancellationToken ct = default);
    Task<GenerateNoticeDraftsResponse> GenerateAsync(int portfolioId, CancellationToken ct = default);
    Task<NoticeDraftResponse?> UpdateAsync(int portfolioId, int id, UpdateNoticeDraftRequest request, CancellationToken ct = default);
    Task<NoticeDraftResponse?> ApproveAsync(int portfolioId, int id, ApproveNoticeDraftRequest request, CancellationToken ct = default);
    Task<NoticeDraftResponse?> DismissAsync(int portfolioId, int id, CancellationToken ct = default);
}
