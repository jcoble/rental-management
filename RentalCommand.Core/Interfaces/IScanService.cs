using RentalCommand.Core.Entities;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Orchestrates the flagship scan-&gt;LLM intake flow: store the upload, run extraction, and
/// produce a reviewable <see cref="ScanDraft"/>. Phase 0 defines the contract only;
/// the implementation is Phase 2.
/// </summary>
public interface IScanService
{
    /// <summary>
    /// Create a draft from an uploaded document for the given portfolio and target entity type
    /// (e.g. "Lease", "Expense"). Runs extraction and returns the persisted draft for review.
    /// </summary>
    Task<ScanDraft> CreateDraftAsync(
        int portfolioId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct = default);

    /// <summary>Confirm a reviewed draft, creating the real record (Receipt→Expense in Phase 2).</summary>
    Task<ScanConfirmResult> ConfirmAndCreateAsync(
        int portfolioId, int draftId, int userId,
        string overridesJson, CancellationToken ct = default);

    /// <summary>Reject a draft; no record is created.</summary>
    Task<bool> RejectDraftAsync(
        int portfolioId, int draftId, int userId, string? reason, CancellationToken ct = default);
}

/// <summary>Result returned from <see cref="IScanService.ConfirmAndCreateAsync"/>.</summary>
/// <param name="Success">Whether the confirm succeeded.</param>
/// <param name="CreatedEntityId">The id of the created entity, or null on failure.</param>
/// <param name="Error">Human-readable error message, or null on success.</param>
/// <param name="EntityType">The type of the created entity ("Expense", "Payment", "WorkOrder", or "Lease"), or null on failure.</param>
public sealed record ScanConfirmResult(bool Success, int? CreatedEntityId, string? Error, string? EntityType = null);
