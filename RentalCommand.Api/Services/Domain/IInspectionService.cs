using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Inspection"/>. Every query is filtered by the
/// caller's portfolio id. Inspections are not soft-deletable (no DeletedAt column), so removal is a
/// hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IInspectionService
{
    Task<IReadOnlyList<InspectionResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<InspectionResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<InspectionResponse?> CreateAsync(int portfolioId, CreateInspectionRequest request, CancellationToken ct = default);
    Task<InspectionResponse?> UpdateAsync(int portfolioId, int id, UpdateInspectionRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
