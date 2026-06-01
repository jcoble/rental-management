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
    public int? UserAccountId { get; set; }
    public string? SenderName { get; set; }

    /// <summary>True when the landlord sent this message to a tenant (outbound); false for tenant inquiries.</summary>
    public bool FromLandlord { get; set; }

    /// <summary>Channels a landlord message was sent on (e.g. <c>"Portal,Email"</c>); null for tenant inquiries.</summary>
    public string? Channels { get; set; }

    /// <summary>The tenant a landlord message was addressed to (when applicable).</summary>
    public int? RecipientTenantId { get; set; }

    /// <summary>Display name of the recipient tenant for landlord messages (when applicable).</summary>
    public string? RecipientTenantName { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>message-1</c>.</summary>
    public string TestId => $"message-{Id}";
}

/// <summary>Landlord → tenant: send a new message over one or more channels.</summary>
public class CreateMessageRequest : IValidatableObject
{
    /// <summary>The tenant (in the caller's portfolio) to send to.</summary>
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>One or more of "Portal", "Email", "Sms" (case-insensitive).</summary>
    public List<string> Channels { get; set; } = [];

    private static readonly HashSet<string> AllowedChannels =
        new(StringComparer.OrdinalIgnoreCase) { "Portal", "Email", "Sms" };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Channels is null || Channels.Count == 0)
        {
            yield return new ValidationResult(
                "At least one channel is required.", [nameof(Channels)]);
            yield break;
        }

        foreach (var c in Channels)
        {
            if (string.IsNullOrWhiteSpace(c) || !AllowedChannels.Contains(c.Trim()))
            {
                yield return new ValidationResult(
                    $"Invalid channel '{c}'. Allowed channels: Portal, Email, Sms.", [nameof(Channels)]);
            }
        }
    }
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
    [MaxLength(50)]
    public string? Status { get; set; }
}

/// <summary>Landlord: post a reply to a tenant message and optionally advance status.</summary>
public class ReplyMessageRequest
{
    [Required]
    [MaxLength(5000)]
    public string Reply { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Status { get; set; }
}

/// <summary>Landlord: change the status of a message.</summary>
public class UpdateMessageStatusRequest
{
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = string.Empty;
}
