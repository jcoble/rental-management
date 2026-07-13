using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical relationship reads. The signed access coordinates are untrusted inputs; every query
/// revalidates the current session, access revision, capability, and property scope in PostgreSQL.
/// </summary>
public interface ILeaseManagementQueryService
{
    Task<LeaseManagementListResponse> ListPageAsync(
        LeaseManagementReadContext access, LeaseManagementListQuery query,
        CancellationToken ct = default);
    Task<DateOnly> GetPortfolioBusinessDateAsync(
        LeaseManagementReadContext access, CancellationToken ct = default);
    Task<LeaseManagementDetailResponse?> GetAsync(
        LeaseManagementReadContext access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        LeaseManagementReadContext access, int leaseManagementId, int skip = 0, int? take = null,
        CancellationToken ct = default);
    Task<bool> CanReadAsync(
        LeaseManagementReadContext access, int leaseManagementId,
        CancellationToken ct = default);
    Task<ReturnPossessionContextResponse> GetReturnPossessionContextAsync(
        LeaseManagementReadContext access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseAgreementHistoryPageResponse?> ListAgreementHistoryPageAsync(
        LeaseManagementReadContext access, int leaseManagementId, LeaseLegalHistoryQuery query,
        CancellationToken ct = default);
    Task<LeaseAgreementDraftDetailResponse?> GetAgreementDraftAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LeaseAgreementSignatureProgressResponse?> GetAgreementSignatureProgressAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LeaseAgreementEffectiveAddendumSeriesResponse?> GetEffectiveAddendumSeriesAsync(
        LeaseManagementReadContext access, int leaseManagementId, int sourceAgreementId,
        CancellationToken ct = default);
    Task<LeaseAddendumHistoryPageResponse?> ListAddendumHistoryPageAsync(
        LeaseManagementReadContext access, int leaseManagementId, LeaseLegalHistoryQuery query,
        CancellationToken ct = default);
    Task<LeaseAddendumEligibleBaseAgreementPageResponse?> ListAddendumEligibleBaseAgreementsAsync(
        LeaseManagementReadContext access, int leaseManagementId, ListQuery query,
        CancellationToken ct = default);
    Task<LeaseAddendumSignerCandidatesResponse?> GetAddendumSignerCandidatesAsync(
        LeaseManagementReadContext access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseAddendumDraftDetailResponse?> GetAddendumDraftAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAddendumId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAgreementArtifactAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAgreementId, int artifactId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAgreementSourceScanAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAddendumArtifactAsync(
        LeaseManagementReadContext access, int leaseManagementId, int leaseAddendumId, int artifactId,
        CancellationToken ct = default);
}

public readonly record struct LeaseManagementReadContext(
    int PortfolioId,
    int UserId,
    Guid SessionId,
    int AccessContextId,
    long AccessRevision);

public sealed record LegalArtifactFileReference(
    int FileAuthorityId,
    string StorageKey,
    string FileName,
    string ContentType);
