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
}
