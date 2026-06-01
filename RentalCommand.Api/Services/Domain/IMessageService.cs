using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Landlord-facing inbox operations for <see cref="Core.Entities.PortalMessage"/>. All queries are
/// portfolio-scoped. Replies set the message status to <c>InProgress</c> by default; a caller may
/// override to any valid <see cref="Core.Enums.PortalMessageStatus"/> value.
/// </summary>
public interface IMessageService
{
    /// <summary>List all messages in the portfolio, newest first. Optional case-insensitive status filter.</summary>
    Task<IReadOnlyList<MessageResponse>> ListAsync(int portfolioId, string? status, CancellationToken ct = default);

    /// <summary>Fetch a single message by id, scoped to the portfolio. Returns null when not found.</summary>
    Task<MessageResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default);

    /// <summary>
    /// Post a landlord reply to a message. Sets <c>Reply</c>, <c>UpdatedAt</c>, and advances <c>Status</c>
    /// to <c>InProgress</c> (or to the caller-supplied value if valid). Returns null when not found.
    /// </summary>
    Task<MessageResponse?> ReplyAsync(int portfolioId, int id, ReplyMessageRequest request, CancellationToken ct = default);

    /// <summary>
    /// Update the status of a message. Returns null when not found; throws <see cref="ArgumentException"/>
    /// on an unrecognised status string.
    /// </summary>
    Task<MessageResponse?> UpdateStatusAsync(int portfolioId, int id, string status, CancellationToken ct = default);
}
