using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface IListingWorkspaceService
{
    Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GenerateAsync(WorkspaceReadScope scope, int unitId, string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> SaveAsync(WorkspaceReadScope scope, int unitId, SaveListingWorkspaceRequest request,
        string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> AttachPhotoAsync(WorkspaceReadScope scope, int unitId, int photoId,
        string clientOperationId, string fileName, string contentType, byte[] bytes, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> UpdatePhotoAsync(WorkspaceReadScope scope, int unitId, int photoId,
        UpdateListingPhotoRequest request, string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> RemovePhotoAsync(WorkspaceReadScope scope, int unitId, int photoId,
        string clientOperationId,
        CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> ReorderPhotosAsync(WorkspaceReadScope scope, int unitId,
        ReorderListingPhotosRequest request, string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> PrepareConnectedAsync(WorkspaceReadScope scope, int unitId, int publicationId,
        string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> PublishConnectedAsync(WorkspaceReadScope scope, int unitId, int publicationId,
        string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> UpdateConnectedAsync(WorkspaceReadScope scope, int unitId, int publicationId,
        string clientOperationId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> UnpublishConnectedAsync(WorkspaceReadScope scope, int unitId, int publicationId,
        string clientOperationId, CancellationToken ct = default);
    Task<ListingPhotoFileResult?> OpenPhotoAsync(int portfolioId, int unitId, int photoId, CancellationToken ct = default);
    Task<ExternalListingSignalResponse?> IngestSignalAsync(WorkspaceReadScope scope, int unitId, int publicationId,
        string providerMessageKey, IngestExternalListingSignalRequest request, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> ConfirmSignalAsync(WorkspaceReadScope scope, int unitId, int signalId, bool accept,
        string clientOperationId,
        CancellationToken ct = default);
}

public sealed record ListingPhotoFileResult(Stream Content, string ContentType, string FileName);
