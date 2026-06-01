using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// Landlord-facing projection of a <see cref="Core.Entities.PortalMessage"/>, enriched with property name,
/// unit label, and sender display name for use in the staff inbox.
/// </summary>
public class MessageResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public int? UnitId { get; set; }
    public string? UnitLabel { get; set; }
    public int UserAccountId { get; set; }
    public string? SenderName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>message-1</c>.</summary>
    public string TestId => $"message-{Id}";
}

/// <summary>Tenant → landlord: open a new message thread.</summary>
public class CreatePortalMessageRequest
{
    [Required]
    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [MaxLength(5000)]
    public string Body { get; set; } = string.Empty;

    public int? PropertyId { get; set; }
}

/// <summary>Tenant: update the status of their own message (e.g. re-open or close).</summary>
public class UpdatePortalMessageStatusRequest
{
    public string? Status { get; set; }
}

/// <summary>Landlord: post a reply to a tenant message and optionally advance status.</summary>
public class ReplyMessageRequest
{
    [Required]
    [MaxLength(5000)]
    public string Reply { get; set; } = string.Empty;

    public string? Status { get; set; }
}

/// <summary>Landlord: change the status of a message.</summary>
public class UpdateMessageStatusRequest
{
    [Required]
    public string Status { get; set; } = string.Empty;
}
