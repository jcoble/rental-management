using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class NoticeDraft : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int TenantAccountId { get; set; }
    public int RecipientLeaseManagementPartyId { get; set; }
    public int? LeaseAgreementId { get; set; }
    public int? LeaseAddendumId { get; set; }
    public long? TenantLedgerEntryId { get; set; }
    public int? PropertyId { get; set; }
    public string NoticeType { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Provenance: the exact LLM prompt used to draft this notice's copy, or <c>null</c> when the
    /// deterministic template fallback produced it (no LLM key / LLM returned nothing usable). Kept
    /// for audit so a landlord/owner can see how an AI-written notice was generated.
    /// </summary>
    public string? GenerationPrompt { get; set; }

    public DateTime TriggerDate { get; set; }
    public int? ConversationId { get; set; }
    public int? TenantNoticePolicyId { get; set; }
    public int? WorkspaceNoticeTemplateVersionId { get; set; }
    public long? RenderedNoticeId { get; set; }
    public string? ApprovedChannels { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? DismissedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public LeaseManagementParty? RecipientLeaseManagementParty { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LeaseAddendum? LeaseAddendum { get; set; }
    public TenantLedgerEntry? TenantLedgerEntry { get; set; }
    public Property? Property { get; set; }
    public Conversation? Conversation { get; set; }
}
