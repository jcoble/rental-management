using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A single message within a <see cref="Conversation"/>. The full thread is the ordered set of
/// these (ascending by <see cref="CreatedAt"/>).
/// </summary>
public class ConversationMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }

    /// <summary>Who wrote this message (Landlord or Tenant).</summary>
    public ConversationSenderRole SenderRole { get; set; }

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated channels a landlord message was actually delivered on (e.g. "Portal,Email,Sms").
    /// Null for tenant messages (tenant replies are in-app only).
    /// </summary>
    public string? Channels { get; set; }

    public DateTime CreatedAt { get; set; }

    public Conversation? Conversation { get; set; }
}
