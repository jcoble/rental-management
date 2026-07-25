using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Capability- and property-scoped CRUD + smart-checklist workflow for
/// <see cref="Core.Entities.Inspection"/>. Every entry point requires a server-derived workspace
/// scope; no caller-supplied portfolio-only authorization path is exposed.
/// </summary>
public interface IInspectionService
{
    Task<IReadOnlyList<InspectionResponse>> ListAuthorizedAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<InspectionListResponse> ListPageAuthorizedAsync(WorkspaceReadScope scope, int? propertyId, ListQuery query, CancellationToken ct = default);
    Task<InspectionDetailResponse?> GetAuthorizedAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAuthorizedAsync(WorkspaceReadScope scope, CancellationToken ct = default);
    Task<InspectionTemplateResponse?> GetTemplateAuthorizedAsync(WorkspaceReadScope scope, int templateId, CancellationToken ct = default);
    Task<InspectionTemplateResponse?> CreateTemplateAuthorizedAsync(WorkspaceReadScope scope, CreateInspectionTemplateRequest request, string operationKey, CancellationToken ct = default);
    Task<InspectionTemplateResponse?> UpdateTemplateAuthorizedAsync(WorkspaceReadScope scope, int templateId, UpdateInspectionTemplateRequest request, string operationKey, CancellationToken ct = default);
    Task<bool> DeleteTemplateAuthorizedAsync(WorkspaceReadScope scope, int templateId, string operationKey, CancellationToken ct = default);
    Task<InspectionDetailResponse?> CreateAuthorizedAsync(WorkspaceReadScope scope, CreateInspectionRequest request, string operationKey, CancellationToken ct = default);
    Task<InspectionResponse?> UpdateAuthorizedAsync(WorkspaceReadScope scope, int id, UpdateInspectionRequest request, string operationKey, CancellationToken ct = default);
    Task<bool> DeleteAuthorizedAsync(WorkspaceReadScope scope, int id, string operationKey, CancellationToken ct = default);
    Task<InspectionItemResponse?> CreateItemAuthorizedAsync(WorkspaceReadScope scope, int inspectionId, CreateInspectionItemRequest request, string operationKey, CancellationToken ct = default);
    Task<InspectionItemResponse?> UpdateItemAuthorizedAsync(WorkspaceReadScope scope, int inspectionId, int itemId, UpdateInspectionItemRequest request, string operationKey, CancellationToken ct = default);
    Task<bool> DeleteItemAuthorizedAsync(WorkspaceReadScope scope, int inspectionId, int itemId, string operationKey, CancellationToken ct = default);
    Task<IReadOnlyList<InspectionItemResponse>?> ReorderItemsAuthorizedAsync(WorkspaceReadScope scope, int inspectionId, ReorderInspectionItemsRequest request, string operationKey, CancellationToken ct = default);
    Task<InspectionItemResponse?> AttachItemPhotoAuthorizedAsync(WorkspaceReadScope scope, int inspectionId, int itemId, int storedFileId, string operationKey, CancellationToken ct = default);
    Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAuthorizedAsync(WorkspaceReadScope scope, int id, int userId, string operationKey, CancellationToken ct = default);
    Task<(Stream Stream, string FileName, string ContentType)?> GetReportAuthorizedAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);
}
