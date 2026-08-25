using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Canonical relationship reads. The signed access coordinates are untrusted inputs; every query
/// revalidates the current session, access revision, capability, and property scope in PostgreSQL.
/// </summary>
public interface ILeaseManagementQueryService
{
    Task<LeaseManagementListResponse> ListPageAsync(
        WorkspaceReadScope access, LeaseManagementListQuery query,
        CancellationToken ct = default);
    Task<DateOnly> GetPortfolioBusinessDateAsync(
        WorkspaceReadScope access, CancellationToken ct = default);
    Task<LeaseManagementDetailResponse?> GetAsync(
        WorkspaceReadScope access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseLedgerResponse?> GetLedgerAsync(
        WorkspaceReadScope access, int leaseManagementId, int skip = 0, int? take = null,
        CancellationToken ct = default);
    Task<bool> CanReadAsync(
        WorkspaceReadScope access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseQaAgreementFacts?> GetLeaseQaAgreementAsync(
        WorkspaceReadScope access, int leaseManagementId,
        CancellationToken ct = default);
    Task<IReadOnlyList<int>> ListAuthorizedAgreementIssueSignerIdsAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<IReadOnlyList<int>> ListAuthorizedAddendumIssueSignerIdsAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAddendumId,
        CancellationToken ct = default);
    Task<ReturnPossessionContextResponse> GetReturnPossessionContextAsync(
        WorkspaceReadScope access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseAgreementHistoryPageResponse?> ListAgreementHistoryPageAsync(
        WorkspaceReadScope access, int leaseManagementId, LeaseLegalHistoryQuery query,
        CancellationToken ct = default);
    Task<LeaseAgreementDraftDetailResponse?> GetAgreementDraftAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LeasePartyLegalBasisPageResponse?> ListEligiblePartyLegalBasisPageAsync(
        WorkspaceReadScope access, int leaseManagementId, ListQuery query,
        CancellationToken ct = default);
    Task<LeaseAgreementSignatureProgressResponse?> GetAgreementSignatureProgressAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LeaseAgreementEffectiveAddendumSeriesResponse?> GetEffectiveAddendumSeriesAsync(
        WorkspaceReadScope access, int leaseManagementId, int sourceAgreementId,
        CancellationToken ct = default);
    Task<LeaseAddendumHistoryPageResponse?> ListAddendumHistoryPageAsync(
        WorkspaceReadScope access, int leaseManagementId, LeaseLegalHistoryQuery query,
        CancellationToken ct = default);
    Task<LeaseAddendumEligibleBaseAgreementPageResponse?> ListAddendumEligibleBaseAgreementsAsync(
        WorkspaceReadScope access, int leaseManagementId, ListQuery query,
        CancellationToken ct = default);
    Task<LeaseAddendumSignerCandidatesResponse?> GetAddendumSignerCandidatesAsync(
        WorkspaceReadScope access, int leaseManagementId,
        CancellationToken ct = default);
    Task<LeaseAddendumDraftDetailResponse?> GetAddendumDraftAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAddendumId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAgreementArtifactAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAgreementId, int artifactId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAgreementSourceScanAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAgreementId,
        CancellationToken ct = default);
    Task<LegalArtifactFileReference?> GetAddendumArtifactAsync(
        WorkspaceReadScope access, int leaseManagementId, int leaseAddendumId, int artifactId,
        CancellationToken ct = default);
}

public sealed record LegalArtifactFileReference(
    int FileAuthorityId,
    string StorageKey,
    string FileName,
    string ContentType);

/// <summary>
/// Exact governing Agreement facts admitted by the same SQL statement that proves the staff
/// session, access revision, RentalsRead capability, and owning-property scope.
/// </summary>
public sealed record LeaseQaAgreementFacts(
    int LeaseAgreementId,
    int LeaseManagementId,
    string AgreementNumber,
    DateOnly TermStartOn,
    DateOnly? TermEndOn,
    decimal BaseRentAmount,
    decimal SecurityDepositObligation,
    decimal LateFeeAmount,
    short RentDueDay,
    string TermsPayload,
    int ExecutedStoredFileId,
    string ExecutedFileName);
