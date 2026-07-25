using RentalCommand.Core.Authorization;
using RentalCommand.Core.Scanning;

namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Prepares confirmation facts and handles review lifecycle transitions after an upload has been
/// durably admitted by <see cref="IScanUploadService"/>.
/// </summary>
public interface IScanService
{
    /// <summary>
    /// Reads the reviewed draft and applies API overrides into the sealed command accepted by the
    /// atomic confirmation boundary. This method prepares immutable facts only; it never writes.
    /// </summary>
    Task<ScanConfirmationPreparation> PrepareConfirmationAsync(
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
        WorkspaceReadScope scope, int draftId, int userId, string? reason, CancellationToken ct = default);
}

public enum ScanConfirmationPreparationOutcome
{
    Ready,
    DraftNotFound,
    UnsupportedTarget,
    TemporarilyUnavailable,
}

public sealed record ScanConfirmationPreparation(
    ScanConfirmationPreparationOutcome Outcome,
    ConfirmScanDraftCommand? Command = null,
    string? Error = null);

/// <summary>
/// How a scanned lease resolves its Property and Unit, surfaced to the review UI before committing.
/// Canonical lease import links existing physical inventory; extracted labels remain suggestions when
/// the reviewer still needs to select (or first add) the correct Property or Unit.
/// </summary>
public sealed record LeaseImportProposal(ProposedRecord Property, ProposedRecord Unit);

/// <summary>
/// One resolved or proposed entity in a <see cref="LeaseImportProposal"/>. <see cref="Action"/> is
/// "link" when an existing in-portfolio record was matched (its <see cref="ExistingId"/> is set), or
/// "select" when the reviewer must choose an inventory row. Extracted suggestions may still appear in
/// <see cref="Label"/> / <see cref="Detail"/> for a select action.
/// </summary>
public sealed record ProposedRecord(
    string Action,
    int? ExistingId,
    string? Label,
    string? Detail);
