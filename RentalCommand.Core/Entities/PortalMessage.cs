using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class PortalMessage
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>
    /// Canonical access context of the authenticated author. Null for system-authored messages.
    /// </summary>
    public int? AuthorAccessContextId { get; set; }

    /// <summary>The tenant a landlord message is addressed to; null for tenant-initiated messages.</summary>
    public int? RecipientTenantId { get; set; }

    /// <summary>True when the landlord sent this message to a tenant; false for tenant-initiated messages.</summary>
    public bool FromLandlord { get; set; }

    /// <summary>
    /// Comma-separated list of the channels actually used to deliver a landlord message
    /// ("Portal","Email","Sms"). Null for tenant-initiated messages.
    /// </summary>
    public string? Channels { get; set; }

    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public PortalMessageStatus Status { get; set; } = PortalMessageStatus.Open;
    public string? Reply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public WorkspaceAccessContext? AuthorAccessContext { get; set; }
    public Tenant? RecipientTenant { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
}
