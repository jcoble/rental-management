using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Continuous-account autopay authorization, replaced by canceling and adding a new row.</summary>
public class TenantAutopayEnrollment : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int TenantAccountId { get; set; }
    public int AuthorizingPartyId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderCustomerId { get; set; } = string.Empty;
    public string ProviderPaymentMethodId { get; set; } = string.Empty;
    public int? AuthorizationArtifactId { get; set; }
    public DateTime EnrolledAtUtc { get; set; }
    public DateTime? CanceledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public TenantAccount? TenantAccount { get; set; }
    public LeaseManagementParty? AuthorizingParty { get; set; }
    public StoredFile? AuthorizationArtifact { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
}
