using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Delivery and signer-workflow evidence for exactly one immutable legal artifact.</summary>
public class SignatureRequest : IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public int? LeaseAgreementId { get; set; }
    public int? LeaseAddendumId { get; set; }
    public string Provider { get; set; } = "native";
    public string? ProviderEnvelopeId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public SignatureRequestStatus Status { get; set; } = SignatureRequestStatus.Prepared;
    public string Subject { get; set; } = string.Empty;
    public int IssuedArtifactId { get; set; }
    public int? ExecutedArtifactId { get; set; }
    public DateTime PreparedAtUtc { get; set; }
    public DateTime? ProviderAcceptedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? DeclinedAtUtc { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public string? FailureCode { get; set; }
    public string? LastError { get; set; }
    public string? ExecutionClaimOwner { get; set; }
    public Guid? ExecutionClaimToken { get; set; }
    public DateTime? ExecutionClaimExpiresAtUtc { get; set; }
    public int ExecutionAttemptCount { get; set; }
    public DateTime? ExecutionLastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public int CreatedByUserId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public LeaseAgreement? LeaseAgreement { get; set; }
    public LeaseAddendum? LeaseAddendum { get; set; }
    public LegalDocumentArtifact? IssuedArtifact { get; set; }
    public LegalDocumentArtifact? ExecutedArtifact { get; set; }
    public ApplicationUser? CreatedByUser { get; set; }
    public List<SignatureSigner> Signers { get; set; } = [];
    public List<SignatureAuditEvent> AuditEvents { get; set; } = [];
}
