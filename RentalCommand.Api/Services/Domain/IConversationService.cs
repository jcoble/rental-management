using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Threaded messaging between the landlord and tenants. Conversations are scoped by topic — a tenant
/// can have multiple threads, each with its own back-and-forth history. All queries are portfolio-scoped;
/// tenant-facing methods are additionally scoped to the signed-in tenant's own conversations. Writes run
/// in a transaction, and landlord sends fan out to email/SMS via the outbox for channels the tenant has
/// contact info for.
/// </summary>
public interface IConversationService
{
    // --- Landlord ---

    /// <summary>List the portfolio's conversations, most-recently-active first. Unread = landlord's.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(int portfolioId, CancellationToken ct = default);
    Task<ConversationListResponse> ListPageAsync(int portfolioId, ListQuery query, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(int portfolioId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSummary>> ListAuthorizedAsync(
        WorkspaceReadScope scope, CancellationToken ct = default);
    Task<ConversationListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope, ListQuery query, CancellationToken ct = default);
    Task<int> GetUnreadCountAuthorizedAsync(
        WorkspaceReadScope scope, CancellationToken ct = default);

    /// <summary>
    /// Fetch one conversation with its full message history (ascending). Resets the landlord's unread
    /// count to 0 (mark-read). Returns null when not found in the portfolio.
    /// </summary>
    Task<ConversationDetail?> GetAsync(int portfolioId, int id, CancellationToken ct = default);
    Task<ConversationDetail?> GetAuthorizedAsync(
        WorkspaceReadScope scope, int id, CancellationToken ct = default);

    /// <summary>
    /// Open a new topic thread with a tenant and send the first (landlord) message, fanning out to
    /// email/SMS for the requested channels the tenant has contact info for. Returns null when the
    /// tenant is not in the portfolio (controller maps to 404).
    /// <para>
    /// Before sending, the subject+body are screened by the Fair Housing review. If it flags concerns
    /// and <paramref name="acknowledgedFairHousingReview"/> is false, the send is blocked with a
    /// <see cref="RentalCommand.Core.FairHousingBlockedException"/> (→ 422). When the flag is true the
    /// block is overridden and the override is logged. If the review is unavailable/errors, the send
    /// proceeds (fail-open).
    /// </para>
    /// </summary>
    Task<ConversationDetail?> StartAsync(
        int portfolioId, int tenantId, string subject, string body, List<string> channels,
        string operationKey, bool acknowledgedFairHousingReview = false, CancellationToken ct = default);
    Task<ConversationDetail?> StartAuthorizedAsync(
        WorkspaceReadScope scope, int tenantId, int? propertyId, string subject, string body,
        List<string> channels, string operationKey, bool acknowledgedFairHousingReview = false,
        CancellationToken ct = default);

    /// <summary>
    /// Append a landlord message to an existing conversation, bumping the tenant's unread count and
    /// fanning out to the requested channels. Returns null when the conversation is not in the portfolio.
    /// </summary>
    Task<ConversationDetail?> PostMessageAsync(
        int portfolioId, int id, string body, List<string> channels, string operationKey,
        CancellationToken ct = default);
    Task<ConversationDetail?> PostMessageAuthorizedAsync(
        WorkspaceReadScope scope, int id, string body, List<string> channels, string operationKey,
        CancellationToken ct = default);

    // --- Tenant ---

    /// <summary>List the signed-in tenant's own conversations, most-recently-active first. Unread = tenant's.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListForTenantAsync(int portfolioId, int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Fetch one of the tenant's own conversations with full history (ascending). Resets the tenant's
    /// unread count. Returns null when not found or not owned by this tenant.
    /// </summary>
    Task<ConversationDetail?> GetForTenantAsync(int portfolioId, int tenantId, int id, CancellationToken ct = default);

    /// <summary>
    /// Tenant opens a new topic thread. The first message is recorded as a Tenant message (in-app only —
    /// no channel fan-out). Bumps the landlord's unread count.
    /// </summary>
    Task<ConversationDetail?> TenantStartAsync(
        int portfolioId, int tenantId, string subject, string body, string operationKey,
        CancellationToken ct = default);

    /// <summary>
    /// Tenant appends a reply to one of their own conversations (in-app only). Bumps the landlord's
    /// unread count. Returns null when not found or not owned by this tenant.
    /// </summary>
    Task<ConversationDetail?> TenantPostAsync(
        int portfolioId, int tenantId, int id, string body, string operationKey,
        CancellationToken ct = default);
}
