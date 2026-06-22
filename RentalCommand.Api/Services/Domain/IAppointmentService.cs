using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Appointment"/>. Every query is filtered by the
/// caller's portfolio id. Appointments are not soft-deletable (no DeletedAt column), so removal is a
/// hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IAppointmentService
{
    Task<IReadOnlyList<AppointmentResponse>> ListAsync(int portfolioId, int? propertyId, int? tenantId, ListQuery query, CancellationToken ct = default);
    Task<AppointmentListResponse> ListPageAsync(int portfolioId, AppointmentListQuery query, CancellationToken ct = default);
    Task<AppointmentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<AppointmentResponse?> CreateAsync(int portfolioId, CreateAppointmentRequest request, CancellationToken ct = default);
    Task<AppointmentResponse?> UpdateAsync(int portfolioId, int id, UpdateAppointmentRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);
}
