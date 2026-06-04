using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant/owner-facing access to the portal. Every query is doubly scoped: by the caller's portfolio id
/// AND by their tenant id (from JWT claims), so a tenant only ever sees their own leases, payments/balance,
/// and work orders. Tenant messaging now lives in the threaded <see cref="IConversationService"/>.
/// </summary>
public interface IPortalService
{
    Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
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
    /// The tenant's autopay enrollment for one of their leases. When <paramref name="leaseId"/> is
    /// null, resolves the tenant's most relevant (active-preferred) lease. Returns a status with
    /// Active=false when there is no enrollment. Ownership-checked: a lease not belonging to this
    /// tenant yields null (→ caller maps to a not-enrolled/404 result). IDOR-critical.
    /// </summary>
    Task<AutopayStatusResponse?> GetAutopayStatusAsync(int portfolioId, int tenantId, int? leaseId, CancellationToken ct = default);

    /// <summary>
    /// Deactivates any active autopay enrollment on the tenant's own lease. Returns the resulting
    /// (Active=false) status, or null when the lease isn't the tenant's (→ 404). Stripe-free: this
    /// only flips the local enrollment so the Engine stops charging. IDOR-critical.
    /// </summary>
    Task<AutopayStatusResponse?> CancelAutopayAsync(int portfolioId, int tenantId, int leaseId, CancellationToken ct = default);
}
