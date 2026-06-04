using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Portfolio-scoped CRUD + smart-checklist workflow for <see cref="Core.Entities.Inspection"/>. Every
/// query is filtered by the caller's portfolio id. Inspections are not soft-deletable (no DeletedAt
/// column), so removal is a hard delete; mutations broadcast realtime updates.
/// </summary>
public interface IInspectionService
{
    Task<IReadOnlyList<InspectionResponse>> ListAsync(int portfolioId, int? propertyId, ListQuery query, CancellationToken ct = default);

    /// <summary>Inspection detail including its checklist <see cref="InspectionDetailResponse.Items"/>.</summary>
    Task<InspectionDetailResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Available templates: the code-defined built-ins plus any portfolio-custom templates, each with
    /// their items. Built-ins surface with a negative id and <c>IsBuiltIn = true</c>.
    /// </summary>
    Task<IReadOnlyList<InspectionTemplateResponse>> ListTemplatesAsync(int portfolioId, CancellationToken ct = default);

    /// <summary>
    /// Creates an inspection, optionally materializing checklist items (all Pending) from the template
    /// referenced by <see cref="CreateInspectionRequest.TemplateId"/>. Returns null when a referenced
    /// property/unit/lease or template is not in the caller's portfolio.
    /// </summary>
    Task<InspectionDetailResponse?> CreateAsync(int portfolioId, CreateInspectionRequest request, CancellationToken ct = default);

    Task<InspectionResponse?> UpdateAsync(int portfolioId, int id, UpdateInspectionRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>Sets a checklist item's result and/or note. Returns null when not found in portfolio.</summary>
    Task<InspectionItemResponse?> UpdateItemAsync(int portfolioId, int inspectionId, int itemId, UpdateInspectionItemRequest request, CancellationToken ct = default);

    /// <summary>
    /// Attaches a stored photo (a <see cref="Core.Entities.StoredFile"/> id) to a checklist item.
    /// Returns null when the item or the file is not in the caller's portfolio.
    /// </summary>
    Task<InspectionItemResponse?> AttachItemPhotoAsync(int portfolioId, int inspectionId, int itemId, int storedFileId, CancellationToken ct = default);

    /// <summary>
    /// Completes the inspection: marks it Completed, spawns a work order per Fail item (linking it back),
    /// generates a PDF report, stores it, and records the report file id. Returns null when not found,
    /// or a failure reason when already completed.
    /// </summary>
    Task<(CompleteInspectionResponse? Result, string? Error)> CompleteAsync(int portfolioId, int id, int userId, CancellationToken ct = default);

    /// <summary>
    /// Loads the generated report blob for download. Returns null when the inspection is not in the
    /// caller's portfolio or has no report (not completed).
    /// </summary>
    Task<(Stream Stream, string FileName, string ContentType)?> GetReportAsync(int portfolioId, int id, CancellationToken ct = default);
}
