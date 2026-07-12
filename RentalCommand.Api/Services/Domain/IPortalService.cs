using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant/owner-facing access to the portal. Every query is doubly scoped: by the caller's portfolio id
/// AND by their tenant id (from JWT claims), so a tenant only ever sees their own leases, payments/balance,
/// and work orders. Tenant messaging now lives in the threaded <see cref="IConversationService"/>.
/// </summary>
public interface IPortalService
{
    /// <summary>Resolves the tenant relationship from the validated context; never from a token claim.</summary>
    Task<int?> ResolveTenantIdAsync(int portfolioId, int accessContextId, CancellationToken ct = default);

    Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default);

    /// <summary>
    /// One of the tenant's OWN work orders with its status timeline. Ownership is enforced in the
    /// query (portfolio + tenant id), so a work order on another tenant's lease/unit returns null
    /// (→ 404) without revealing whether it exists. IDOR-critical.
    /// </summary>
    Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(int portfolioId, int tenantId, int workOrderId, CancellationToken ct = default);

    Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(int portfolioId, int tenantId, CreateTenantWorkOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// Answers a tenant's question grounded in their OWN lease. The lease must belong to this tenant
    /// (when <paramref name="leaseId"/> is null, the tenant's most relevant lease is used); a lease id
    /// that isn't the tenant's yields null (→ 404) so a tenant can never query another tenant's lease.
    /// Returns null when the tenant has no lease or the question is empty. IDOR-critical.
    /// </summary>
    Task<LeaseQuestionResponse?> AskLeaseAsync(int portfolioId, int tenantId, int? leaseId, string question, CancellationToken ct = default);

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
