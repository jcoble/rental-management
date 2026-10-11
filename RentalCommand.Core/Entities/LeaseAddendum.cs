using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>One version in a stable legal-addendum series attached to an exact base Agreement.</summary>
public class LeaseAddendum : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public Guid SeriesPublicId { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseManagementId { get; set; }
    public int BaseAgreementId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public string AddendumNumber { get; set; } = string.Empty;
    public LeaseAddendumPurpose Purpose { get; set; }
    public int? ReplacesAddendumId { get; set; }
    public DateOnly EffectiveFromOn { get; set; }
    public DateOnly? EffectiveThroughOn { get; set; }
    public DateOnly? SupersededEffectiveOn { get; set; }
    public int? SupersededByAddendumId { get; set; }
    public DateTime? SupersessionRecordedAtUtc { get; set; }
    public int TermsSchemaVersion { get; set; }
    public string TermsPayload { get; set; } = string.Empty;
    public int DocumentSourceVersionId { get; set; }
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
    public LeaseAgreement? BaseAgreement { get; set; }
    public LeaseAddendum? ReplacesAddendum { get; set; }
    public LeaseAddendum? SupersededByAddendum { get; set; }
    public LegalDocumentSourceVersion? DocumentSourceVersion { get; set; }
    public LegalDocumentArtifact? IssuedArtifact { get; set; }
    public LegalDocumentArtifact? ExecutedArtifact { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<LeaseAddendum> ReplacementVersions { get; set; } = [];
    public List<LeaseAddendum> SupersededVersions { get; set; } = [];
    public List<LeaseAddendumSigner> Signers { get; set; } = [];
    public List<LeaseAddendumFinancialEffect> FinancialEffects { get; set; } = [];
    public List<LeaseRenewalAddendumDecision> ReplacementDecisions { get; set; } = [];
}
