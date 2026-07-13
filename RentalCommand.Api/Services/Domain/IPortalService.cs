using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant-facing access to the portal. Leasing reads retain the validated access-context id through
/// the translated query so tenant identity alone never broadens relationship access. Tenant messaging
/// lives in the threaded <see cref="IConversationService"/>.
/// </summary>
public interface IPortalService
{
    /// <summary>Resolves the tenant relationship from the validated context; never from a token claim.</summary>
    Task<int?> ResolveTenantIdAsync(int portfolioId, int accessContextId, CancellationToken ct = default);

    Task<IReadOnlyList<PortalLeaseRelationshipResponse>> GetLeasesAsync(
        int portfolioId, int accessContextId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(
        int portfolioId, int accessContextId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default);

    /// <summary>
    /// One of the tenant's OWN work orders with its status timeline. Ownership is enforced in the
    /// query (portfolio + tenant id), so a work order on another tenant's lease/unit returns null
    /// (→ 404) without revealing whether it exists. IDOR-critical.
    /// </summary>
    Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(int portfolioId, int tenantId, int workOrderId, CancellationToken ct = default);

    Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(int portfolioId, int tenantId, CreateTenantWorkOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Answers a tenant's question grounded in an agreement on their effective rental relationship.
    /// When <paramref name="leaseManagementId"/> is null, the current relationship is selected by the
    /// database lifecycle projection. A relationship outside the access context yields null (→ 404).
    /// </summary>
    Task<LeaseQuestionResponse?> AskLeaseAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        int? leaseManagementId,
        string question,
        CancellationToken ct = default);

    /// <summary>
    /// The tenant's autopay enrollment for one canonical account. Ownership is proven through the
    /// effective LeaseManagement party and explicit TenantUserAccess in the translated query.
    /// </summary>
    Task<AutopayStatusResponse?> GetAutopayStatusAsync(int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default);

    /// <summary>
    /// Cancels the active enrollment on the tenant's canonical account. The account ownership gate
    /// and enrollment lookup execute as one translated statement.
    /// </summary>
    Task<AutopayStatusResponse?> CancelAutopayAsync(int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default);
}
