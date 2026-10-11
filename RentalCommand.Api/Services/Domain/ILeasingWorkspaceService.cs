using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

public interface ILeasingWorkspaceService
{
    Task<LeasingTodayResponse?> GetTodayAsync(WorkspaceReadScope scope, CancellationToken ct = default);
    Task<LeasingPipelinePageResponse> ListPipelineAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<LeasingRentalPageResponse> ListRentalsAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<LeasingCalendarPageResponse> ListCalendarAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<LeasingInboxPageResponse> ListInboxAsync(WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<LeasingRentalDetailResponse?> GetRentalAsync(WorkspaceReadScope scope, int unitId, CancellationToken ct = default);
    Task<LeasingApplicationDetailResponse?> GetApplicationAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<LeasingAppointmentDetailResponse?> GetAppointmentAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<LeasingConversationDetailResponse?> GetConversationAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<bool> CanAccessConversationAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<LeasingMoveInDetailResponse?> GetMoveInAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
}
