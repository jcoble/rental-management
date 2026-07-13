using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

public class Tenant : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? EmergencyContact { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public List<LeaseManagementParty> LeaseManagementParties { get; set; } = [];
    public List<LeaseAgreementSigner> AgreementSignerSnapshots { get; set; } = [];
    public List<LeaseAddendumSigner> AddendumSignerSnapshots { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<Appointment> Appointments { get; set; } = [];
    public List<UserAccount> UserAccounts { get; set; } = [];
}
