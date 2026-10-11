namespace RentalCommand.Core.Scanning;

public enum AtomicScanDraftClaimOutcome
{
    Claimed,
    NotFound,
    NotReady,
    Rejected,
    TargetMismatch,
    StalePreparation,
    AlreadyConfirmed,
    DuplicateSourceContent,
}

public sealed record AtomicScanDraftClaim(
    AtomicScanDraftClaimOutcome Outcome,
    int PortfolioId,
    int DraftId,
    string TargetEntityType,
    int? SourceStoredFileId,
    string? ExtractedFieldsJson,
    string? SourceLabel,
    string? CanonicalEntityType,
    int? CanonicalEntityId);
