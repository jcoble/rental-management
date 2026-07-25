using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Provider-neutral screening workflow metadata. Sensitive identity answers, SSNs, and consumer
/// report contents remain in the provider's hosted experience and are never persisted here.
/// </summary>
public sealed class ApplicantScreening : IPortfolioScoped, IAuditable
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int ApplicationId { get; set; }
    public ScreeningMode Mode { get; set; }
    public ApplicantScreeningStatus Status { get; set; } = ApplicantScreeningStatus.Created;

    /// <summary>Stable adapter discriminator for integrated screening; null for an external workflow.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>User-facing provider name. External entries may be Zillow or any checker the landlord used.</summary>
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public string? ProviderHostedUrl { get; set; }

    /// <summary>Client-generated business key used for safe retries of the create workflow.</summary>
    public string OperationKey { get; set; } = string.Empty;

    public bool ConsentConfirmed { get; set; }
    public DateTime? ConsentAtUtc { get; set; }
    public DateTime? InvitedAtUtc { get; set; }
    public DateTime? ApplicantSubmittedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? FailedAtUtc { get; set; }
    public DateTime LastStatusAtUtc { get; set; }

    /// <summary>Optional landlord decision metadata; not a copy or summary of the consumer report.</summary>
    public ScreeningDecision? Decision { get; set; }
    public string? DecisionReason { get; set; }
    public int? DecisionRecordedByUserId { get; set; }
    public DateTime? DecisionRecordedAtUtc { get; set; }
    public bool ConsumerReportUsedForDecision { get; set; }

    /// <summary>CRA contact snapshot needed later for an adverse-action notice.</summary>
    public string? CreditReportingAgencyName { get; set; }
    public string? CreditReportingAgencyAddress { get; set; }
    public string? CreditReportingAgencyPhone { get; set; }

    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public RentalApplication? Application { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public ApplicationUser? DecisionRecordedByUser { get; set; }
    public List<ApplicantScreeningMilestone> Milestones { get; set; } = [];
}

/// <summary>
/// Idempotent, content-free provider delivery/milestone receipt. It proves what notification was
/// applied and when without retaining a restricted report payload.
/// </summary>
public sealed class ApplicantScreeningMilestone : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int ApplicantScreeningId { get; set; }
    public string Source { get; set; } = string.Empty;
    public string DeliveryId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public ApplicantScreeningStatus Status { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public ApplicantScreening? ApplicantScreening { get; set; }
}
