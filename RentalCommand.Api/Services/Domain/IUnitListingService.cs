using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

public interface IUnitListingService
{
    Task<bool> UnitExistsInPortfolioAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<UnitListingResponse?> GetForUnitAsync(int portfolioId, int unitId, CancellationToken ct = default);
    Task<UnitListingResponse?> GenerateForUnitAsync(int portfolioId, int unitId, int userId, CancellationToken ct = default);
    Task<UnitListingResponse?> SaveAsync(int portfolioId, int unitId, SaveUnitListingRequest request, int userId, CancellationToken ct = default);
}
