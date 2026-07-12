using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>One draft or immutable issued base-agreement version in a lease relationship.</summary>
public class LeaseAgreement : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public string AgreementNumber { get; set; } = string.Empty;
    public LeaseAgreementChangeType ChangeType { get; set; }
    /// <summary>The executed Agreement copied into this destination draft during a Unit transfer.</summary>
    public int? TransferredFromAgreementId { get; set; }
    public int? ReplacesAgreementId { get; set; }
    public int? RenewsAgreementId { get; set; }
    public LeaseAgreementTermType TermType { get; set; }
    public DateOnly TermStartOn { get; set; }
    public DateOnly? TermEndOn { get; set; }
    public DateOnly GoverningFromOn { get; set; }
    public DateOnly? SupersededEffectiveOn { get; set; }
    public int? SupersededByAgreementId { get; set; }
    public DateTime? SupersessionRecordedAtUtc { get; set; }
    public decimal BaseRentAmount { get; set; }
    public short RentDueDay { get; set; }
    public decimal SecurityDepositObligation { get; set; }
    public decimal LateFeeAmount { get; set; }
    public short GracePeriodDays { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int TermsSchemaVersion { get; set; }
    public string TermsPayload { get; set; } = string.Empty;
    /// <summary>Template provenance for generated agreements; null for an imported external original.</summary>
    public int? DocumentTemplateId { get; set; }
    public int? DocumentTemplateVersion { get; set; }
    public int? IssuedArtifactId { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
    public int? ExecutedArtifactId { get; set; }
    public DateTime? FullyExecutedAtUtc { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public string? VoidReasonCode { get; set; }
    public string? VoidNote { get; set; }
    public DateTime? DraftCanceledAtUtc { get; set; }
    public string? DraftCancellationReason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int CreatedByUserId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int DraftRevision { get; set; } = 1;

    public Portfolio? Portfolio { get; set; }
    public LeaseManagement? LeaseManagement { get; set; }
    public LeaseAgreement? ReplacesAgreement { get; set; }
    public LeaseAgreement? TransferredFromAgreement { get; set; }
    public List<LeaseAgreement> TransferSuccessors { get; set; } = [];
    public LeaseAgreement? RenewsAgreement { get; set; }
    public LeaseAgreement? SupersededByAgreement { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }
    public LegalDocumentArtifact? IssuedArtifact { get; set; }
    public LegalDocumentArtifact? ExecutedArtifact { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<LeaseAgreement> CorrectionsAndRestatements { get; set; } = [];
    public List<LeaseAgreement> Renewals { get; set; } = [];
    public List<LeaseAgreement> SupersededAgreements { get; set; } = [];
    public List<LeaseAgreementSigner> Signers { get; set; } = [];
    public List<LeaseAddendum> Addenda { get; set; } = [];
    public List<LeaseRenewalAddendumDecision> RenewalAddendumDecisions { get; set; } = [];
}
