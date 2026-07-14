using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD for <see cref="Core.Entities.Appointment"/>. Every query is filtered by the
/// caller's portfolio id. Appointments are not soft-deletable (no DeletedAt column), so removal is a
/// hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IAppointmentService
{
    Task<IReadOnlyList<AppointmentResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope, int? propertyId, int? tenantId, ListQuery query,
        CancellationToken ct = default);
    Task<AppointmentListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, AppointmentListQuery query, CancellationToken ct = default);
    Task<AppointmentScheduleSummaryResponse> GetScheduleSummaryAuthorizedAsync(
        WorkspaceReadScope scope, CancellationToken ct = default);
    Task<AppointmentResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<AppointmentResponse?> CreateAuthorizedAsync(
        WorkspaceReadScope scope, CreateAppointmentRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<AppointmentResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope, int id, UpdateAppointmentRequest request, string idempotencyKey,
        CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope, int id, int? expectedPropertyId, string idempotencyKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<AppointmentResponse>> ListAsync(int portfolioId, int? propertyId, int? tenantId, ListQuery query, CancellationToken ct = default);
    Task<AppointmentListResponse> ListPageAsync(int portfolioId, AppointmentListQuery query, CancellationToken ct = default);
    Task<AppointmentResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
}
