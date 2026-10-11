namespace RentalCommand.Core.Enums;

/// <summary>
/// Who authored a <see cref="Entities.ConversationMessage"/> in a threaded conversation.
/// Stored as its string name (HasConversion&lt;string&gt;()) to match the app-wide convention.
/// </summary>
public enum ConversationSenderRole
{
    Landlord,
    Tenant,
    Technician,
}
