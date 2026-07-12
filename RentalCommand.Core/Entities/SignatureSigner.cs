using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Frozen signer identity, token digest, consent, and signature evidence.</summary>
public class SignatureSigner : IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int SignatureRequestId { get; set; }
    public int? AgreementSignerId { get; set; }
    public int? AddendumSignerId { get; set; }
    public string NameSnapshot { get; set; } = string.Empty;
    public string EmailSnapshot { get; set; } = string.Empty;
    public short SigningOrder { get; set; }
    public bool IsRequired { get; set; } = true;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime TokenExpiresAtUtc { get; set; }
    public SignatureSignerStatus Status { get; set; } = SignatureSignerStatus.Pending;
    public DateTime? ConsentGivenAtUtc { get; set; }
    public DateTime? ViewedAtUtc { get; set; }
    public DateTime? SignedAtUtc { get; set; }
    public DateTime? DeclinedAtUtc { get; set; }
    public SignatureSignatureType SignatureType { get; set; } = SignatureSignatureType.None;
    public string? TypedName { get; set; }
    public int? DrawnSignatureStoredFileId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public SignatureRequest? SignatureRequest { get; set; }
    public LeaseAgreementSigner? AgreementSigner { get; set; }
    public LeaseAddendumSigner? AddendumSigner { get; set; }
    public StoredFile? DrawnSignatureStoredFile { get; set; }
}
