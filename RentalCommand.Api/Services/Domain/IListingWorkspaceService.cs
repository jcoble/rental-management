using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IListingWorkspaceService
{
    Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GetAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> GenerateAsync(int portfolioId, int unitId, int userId, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> SaveAsync(int portfolioId, int unitId, SaveListingWorkspaceRequest request, int userId, CancellationToken ct = default);
    Task<ExternalListingSignalResponse?> IngestSignalAsync(int portfolioId, int unitId, int publicationId,
        IngestExternalListingSignalRequest request, CancellationToken ct = default);
    Task<ListingWorkspaceResponse?> ConfirmSignalAsync(int portfolioId, int unitId, int signalId, bool accept, int userId,
        CancellationToken ct = default);
}
