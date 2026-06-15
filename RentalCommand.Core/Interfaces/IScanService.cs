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

    /// <summary>
    /// Create a draft for one file inside a bulk-scan batch: stores the file, generates the preview,
    /// and persists a Pending <see cref="ScanDraft"/> linked to <paramref name="batchId"/>. The Engine
    /// worker then extracts it the same way it processes any Pending draft (no batch awareness needed).
    /// </summary>
    Task<ScanDraft> CreateBatchDraftAsync(
        int portfolioId,
        int batchId,
        byte[] fileBytes,
        string contentType,
        string targetEntityType,
        CancellationToken ct = default);

    /// <summary>Confirm a reviewed draft, creating the real record (Receipt→Expense in Phase 2).</summary>
    Task<ScanConfirmResult> ConfirmAndCreateAsync(
        int portfolioId, int draftId, int userId,
        string overridesJson, CancellationToken ct = default);

    /// <summary>
    /// Read-only preview of what confirming a LEASE draft would do with the property and unit
    /// (link an existing in-portfolio record vs. create a new one from the extracted document). Returns
    /// null when the draft is missing, out of portfolio, or not a lease. Writes nothing. The web review
    /// screen uses this so a landlord scanning into an empty portfolio sees + can correct the proposed
    /// property/unit before committing.
    /// </summary>
    Task<LeaseImportProposal?> BuildLeaseProposalAsync(
        int portfolioId, int draftId, string overridesJson, CancellationToken ct = default);

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

/// <summary>
/// What confirming a scanned lease would do with the property/unit, surfaced to the review UI so a
/// brand-new landlord scanning into an empty portfolio can SEE that a Property/Unit will be created
/// (vs. linked to an existing one) and correct it via overrides before committing. The confirm path
/// performs the same match-or-create; this is the read-only "what will happen" preview.
/// </summary>
public sealed record LeaseImportProposal(ProposedRecord Property, ProposedRecord Unit);

/// <summary>
/// One proposed entity in a <see cref="LeaseImportProposal"/>. <see cref="Action"/> is "link" when an
/// existing in-portfolio record was matched (its <see cref="ExistingId"/> is set), "create" when one
/// would be created from the extracted document fields (shown in <see cref="Label"/> / <see cref="Detail"/>),
/// or "select" when there isn't enough on the document to match or create so the reviewer must choose
/// (e.g. no property address was extracted).
/// </summary>
public sealed record ProposedRecord(
    string Action,
    int? ExistingId,
    string? Label,
    string? Detail);
