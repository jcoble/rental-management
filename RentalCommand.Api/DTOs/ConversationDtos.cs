using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// List-row projection of a <see cref="Core.Entities.Conversation"/>: a topic thread between the
/// landlord and one tenant. <c>UnreadCount</c> is the viewer's own unread count (landlord vs tenant).
/// </summary>
public class ConversationSummary
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;

    /// <summary>The conversation topic (e.g. "Rent", "Maintenance").</summary>
    public string Subject { get; set; } = string.Empty;

    public string? PropertyName { get; set; }
    public string? LastMessagePreview { get; set; }
    public DateTime LastMessageAt { get; set; }

    /// <summary>Unread message count for the viewer requesting this list (landlord or tenant).</summary>
    public int UnreadCount { get; set; }

    /// <summary>Total messages in the thread.</summary>
    public int MessageCount { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>conversation-1</c>.</summary>
    public string TestId => $"conversation-{Id}";
}

public class ConversationListResponse
{
    public IReadOnlyList<ConversationSummary> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public sealed record ConversationUnreadCountResponse(int Count);

/// <summary>A conversation summary plus its full ordered message history (ascending by time).</summary>
public class ConversationDetail : ConversationSummary
{
    public List<ConversationMessageDto> Messages { get; set; } = [];
}

/// <summary>One message within a conversation thread.</summary>
public class ConversationMessageDto
{
    public int Id { get; set; }

    /// <summary>"Landlord" or "Tenant" (string-serialized enum).</summary>
    public string SenderRole { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>Channels a landlord message was delivered on (e.g. "Portal,Email"); null for tenant messages.</summary>
    public string? Channels { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>conversation-message-1</c>.</summary>
    public string TestId => $"conversation-message-{Id}";
}

/// <summary>Landlord → tenant: open a new topic thread and send the first message over one or more channels.</summary>
public class StartConversationRequest : IValidatableObject
{
    /// <summary>The tenant (in the caller's portfolio) to start a conversation with.</summary>
    [Range(1, int.MaxValue)]
    public int TenantId { get; set; }

    /// <summary>The conversation topic.</summary>
    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>The first message body.</summary>
    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>One or more of "Portal", "Email", "Sms" (case-insensitive).</summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>
    /// Set true to send even when the Fair Housing review flags the message copy. The landlord has
    /// reviewed the concerns and is consciously overriding the block. The override is logged server-side.
    /// Default false → flagged copy is blocked with a 422 carrying the concerns.
    /// </summary>
    public bool AcknowledgedFairHousingReview { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ConversationChannelValidation.Validate(Channels);
}

/// <summary>Landlord: append a message to an existing conversation over one or more channels.</summary>
public class PostMessageRequest : IValidatableObject
{
    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>One or more of "Portal", "Email", "Sms" (case-insensitive).</summary>
    public List<string> Channels { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ConversationChannelValidation.Validate(Channels);
}

/// <summary>Tenant → landlord: open a new topic thread (no channel selection — tenant messages are in-app).</summary>
public class TenantStartConversationRequest
{
    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;
}

/// <summary>Tenant: append a message to one of their own conversations (no channel selection).</summary>
public class TenantPostMessageRequest
{
    [Required]
    [MaxLength(4000)]
    public string Body { get; set; } = string.Empty;
}

/// <summary>Shared channel-list validation for landlord conversation requests.</summary>
internal static class ConversationChannelValidation
{
    private static readonly HashSet<string> AllowedChannels =
        new(StringComparer.OrdinalIgnoreCase) { "Portal", "Email", "Sms" };

    public static IEnumerable<ValidationResult> Validate(List<string>? channels)
    {
        if (channels is null || channels.Count == 0)
        {
            yield return new ValidationResult(
                "At least one channel is required.", [nameof(StartConversationRequest.Channels)]);
            yield break;
        }

        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c) || !AllowedChannels.Contains(c.Trim()))
            {
                yield return new ValidationResult(
                    $"Invalid channel '{c}'. Allowed channels: Portal, Email, Sms.",
                    [nameof(StartConversationRequest.Channels)]);
            }
        }
    }
}
