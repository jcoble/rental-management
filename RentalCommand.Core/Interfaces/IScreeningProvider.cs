namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Swappable adapter for a provider-hosted applicant screening flow. Rental Command supplies only
/// reconciliation and invitation data; the provider collects sensitive identity data directly.
/// </summary>
public interface IScreeningProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    ScreeningProviderDescriptor Descriptor { get; }

    Task<ScreeningInvitationResult> CreateInvitationAsync(
        ScreeningInvitationRequest request,
        CancellationToken ct = default);
}

public sealed record ScreeningProviderDescriptor(
    string Key,
    string DisplayName,
    bool IsConfigured,
    ScreeningProviderCapabilities Capabilities);

public sealed record ScreeningProviderCapabilities(
    bool CreatesHostedInvitation,
    bool SupportsStatusWebhooks,
    bool SuppliesAdverseActionAgency,
    bool SupportsApplicantPaidOrders,
    bool SupportsLandlordPaidOrders);

/// <summary>
/// OperationKey remains stable across recovery retries and must be forwarded by an adapter as the
/// provider's idempotency key.
/// </summary>
public sealed record ScreeningInvitationRequest(
    int ApplicationId,
    string OperationKey,
    string ApplicantName,
    string ApplicantEmail,
    DateTime ConsentAtUtc);

public sealed record ScreeningInvitationResult(
    bool Accepted,
    string? ProviderReference,
    string? ProviderHostedUrl,
    DateTime? InvitedAtUtc,
    string? ErrorCode,
    string? CreditReportingAgencyName = null,
    string? CreditReportingAgencyAddress = null,
    string? CreditReportingAgencyPhone = null);

/// <summary>
/// Normalized result of a provider adapter's signature-verified delivery. The adapter may inspect
/// the remote payload transiently, but only this restricted metadata crosses into persistence.
/// </summary>
public sealed record ScreeningProviderStatusDelivery(
    string ProviderKey,
    string DeliveryId,
    string ProviderReference,
    string EventType,
    RentalCommand.Core.Enums.ApplicantScreeningStatus Status,
    DateTime OccurredAtUtc,
    string? ProviderHostedUrl = null,
    string? CreditReportingAgencyName = null,
    string? CreditReportingAgencyAddress = null,
    string? CreditReportingAgencyPhone = null);
