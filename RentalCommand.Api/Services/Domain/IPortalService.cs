using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Tenant/owner-facing access to the portal. Every query is doubly scoped: by the caller's portfolio id
/// AND by their tenant id (from JWT claims), so a tenant only ever sees their own leases, payments/balance,
/// work orders, and portal messages. Write operations allow tenants to open new message threads and update
/// the status of their own messages.
/// </summary>
public interface IPortalService
{
    Task<IReadOnlyList<LeaseResponse>> GetLeasesAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PortalMessageResponse>> GetMessagesAsync(int portfolioId, int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Create a new message thread on behalf of the signed-in tenant. The service resolves the
    /// <see cref="Core.Entities.UserAccount.Id"/> for this tenant in the portfolio automatically.
    /// Returns null when the optional PropertyId is supplied but is not in the portfolio, or when
    /// no UserAccount exists for the tenant in this portfolio.
    /// </summary>
    Task<PortalMessageResponse?> CreateMessageAsync(int portfolioId, int tenantId, CreatePortalMessageRequest request, CancellationToken ct = default);

    /// <summary>
    /// Allow a tenant to update the status of one of their own messages. Only <c>Open</c> and <c>Closed</c>
    /// are permitted; other values are silently ignored and the status is left unchanged. Returns null when
    /// not found or not owned by this tenant's messages scope.
    /// </summary>
    Task<PortalMessageResponse?> UpdateMessageStatusAsync(int portfolioId, int tenantId, int id, string? status, CancellationToken ct = default);
}
