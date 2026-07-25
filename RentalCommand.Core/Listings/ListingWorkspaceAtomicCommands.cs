using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Listings;

public interface IListingWorkspaceAtomicCommand : IAtomicCommandData
{
    int PortfolioId { get; }
    int UnitId { get; }
    int ActorUserId { get; }
    Guid AuthSessionId { get; }
    int AccessContextId { get; }
    long AccessRevision { get; }
}

public sealed record GenerateListingWorkspaceCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision) : IListingWorkspaceAtomicCommand;

public sealed record GuidedListingValues(
    string? Status,
    string? ExternalListingId,
    string? ListingUrl,
    string? ApplicationUrl,
    string? ManagementUrl,
    string? LastConfirmedExternalStatus,
    DateTime? LastConfirmedAtUtc,
    bool? CopyConfirmed,
    bool? TermsConfirmed,
    bool? PhotosConfirmed,
    bool? ProviderWorkspaceOpened,
    bool? MarkCurrentVersionPublished) : IAtomicCommandData;

public sealed record SaveListingWorkspaceCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision,
    string? Status, string? Headline, string? Description,
    decimal? Rent, decimal? SecurityDeposit, DateTime? AvailableOn,
    string? LeaseTerms, string? PetPolicy, string? Utilities,
    string? Parking, string? Amenities, GuidedListingValues? ZillowGuided)
    : IListingWorkspaceAtomicCommand;

public sealed record FinalizeListingPhotoUploadCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision,
    int PhotoId, Guid PendingUploadId, string Purpose, string OperationKeyHash,
    string RequestFingerprint, string StoragePath, string FileName,
    string ContentType, long SizeBytes, string Sha256)
    : IListingWorkspaceAtomicCommand;

public sealed record UpdateListingPhotoCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision, int PhotoId, string Category, string? Caption)
    : IListingWorkspaceAtomicCommand;

public sealed record RemoveListingPhotoCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision, int PhotoId)
    : IListingWorkspaceAtomicCommand;

public sealed record ReorderListingPhotosCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision, int[] PhotoIds)
    : IListingWorkspaceAtomicCommand;

public enum ConnectedListingIntentOperation
{
    Prepare = 1,
    Publish = 2,
    Update = 3,
    Unpublish = 4,
}

public sealed record AdmitConnectedListingIntentCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision, int PublicationId,
    int RentalListingId, int ExpectedContentVersion, ConnectedListingIntentOperation Operation)
    : IListingWorkspaceAtomicCommand;

public sealed record PersistConnectedListingResultCommand(
    int PortfolioId, int PropertyId, int UnitId, int AdmittedActorUserId,
    int PublicationId,
    int RentalListingId, int ExpectedContentVersion,
    Guid AdmissionAttemptId, string AdmissionCommandType, string AdmissionIdempotencyKey,
    string AdmissionResultContract,
    ListingPublicationStatus Status, string DeliveryKey, string DeliveryStatus,
    string? DeliveryError, string? ExternalListingId, string? ListingUrl,
    bool MarkPublishedVersion, string Reason)
    : IAtomicCommandData;

public sealed record ConfirmExternalListingSignalCommand(
    int PortfolioId, int UnitId, int ActorUserId, Guid AuthSessionId,
    int AccessContextId, long AccessRevision, int SignalId, bool Accept)
    : IListingWorkspaceAtomicCommand;

public enum ListingWorkspaceMutationOutcome
{
    Applied = 1,
    NotFound = 2,
    NoChange = 3,
}

public sealed record ListingWorkspaceMutationResult(
    ListingWorkspaceMutationOutcome Outcome,
    int PortfolioId,
    int UnitId,
    int? RentalListingId) : IAtomicResultData;

public sealed record ConnectedListingIntentResult(
    ListingWorkspaceMutationOutcome Outcome,
    int PortfolioId,
    int? PropertyId,
    int UnitId,
    int? RentalListingId,
    int? PublicationId,
    int? ContentVersion) : IAtomicResultData;

public sealed record ConnectedListingPersistenceResult(
    ListingWorkspaceMutationOutcome Outcome,
    int PortfolioId,
    int PropertyId,
    int UnitId,
    int RentalListingId,
    int PublicationId,
    Guid AdmissionAttemptId,
    bool AppliedToCurrentPublication,
    bool ReconciliationRequired,
    ListingPublicationStatus ProviderStatus,
    string DeliveryKey,
    string DeliveryStatus,
    string? DeliveryError,
    string? ExternalListingId,
    string? ListingUrl) : IAtomicResultData;

public sealed record ApplyConnectedListingResultCommand(
    int PortfolioId,
    int PropertyId,
    int UnitId,
    int AdmittedActorUserId,
    int ExpectedContentVersion,
    Guid ProviderResultAttemptId,
    string ProviderResultCommandType,
    string ProviderResultIdempotencyKey,
    string ProviderResultContract,
    ConnectedListingPersistenceResult ProviderResult,
    bool MarkPublishedVersion,
    string Reason) : IAtomicCommandData;
