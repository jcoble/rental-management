using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IListingWorkspaceService
{
    Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GenerateAsync(int portfolioId, int unitId, int userId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> SaveAsync(int portfolioId, int unitId, SaveListingWorkspaceRequest request, int userId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> AttachPhotoAsync(int portfolioId, int unitId, int photoId, int userId,
        string clientOperationId, string fileName, string contentType, byte[] bytes, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> UpdatePhotoAsync(int portfolioId, int unitId, int photoId,
        UpdateListingPhotoRequest request, int userId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> RemovePhotoAsync(int portfolioId, int unitId, int photoId, int userId,
        CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> ReorderPhotosAsync(int portfolioId, int unitId,
        ReorderListingPhotosRequest request, int userId, CancellationToken ct = default);
    Task<ListingPhotoFileResult?> OpenPhotoAsync(int portfolioId, int unitId, int photoId, CancellationToken ct = default);
    Task<ExternalListingSignalResponse?> IngestSignalAsync(int portfolioId, int unitId, int publicationId,
        IngestExternalListingSignalRequest request, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> ConfirmSignalAsync(int portfolioId, int unitId, int signalId, bool accept, int userId,
        CancellationToken ct = default);
}

public sealed record ListingPhotoFileResult(Stream Content, string ContentType, string FileName);
