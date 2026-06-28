using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A native e-sign "envelope": one document sent to one or more signers for electronic signature.
/// Created by <c>NativeEsignProvider</c> from a lease agreement PDF; the provider envelope id surfaced
/// on <see cref="Lease.EsignEnvelopeId"/> is this row's <see cref="PublicId"/> (an opaque GUID string).
/// Portfolio-scoped. The signer-level trail (who viewed/signed, when, from where) lives on
/// <see cref="SignatureSigner"/> and <see cref="SignatureAuditEvent"/>.
/// </summary>
public class SignatureRequest
{
    public int Id { get; set; }

    /// <summary>Owning portfolio (IDOR scope). Always set from the lease's portfolio.</summary>
    public int PortfolioId { get; set; }

    /// <summary>
    /// Opaque, stable public identifier returned as the e-sign envelope id (maps to
    /// <see cref="Lease.EsignEnvelopeId"/>). A GUID string so it is unguessable and webhook/status code
    /// can resolve the request without leaking the integer key.
    /// </summary>
    public string PublicId { get; set; } = string.Empty;

    /// <summary>The lease this signature request executes (the only document type in v1).</summary>
    public int LeaseId { get; set; }

    /// <summary>Display name of the document the signer is asked to sign (e.g. "lease-7-agreement.pdf").</summary>
    public string DocumentName { get; set; } = string.Empty;

    /// <summary>Human-readable subject/title shown to the signer (e.g. "Lease L-2026-7").</summary>
    public string? Subject { get; set; }

    /// <summary>The <see cref="StoredFile"/> id of the original (unsigned) document under review.</summary>
    public int OriginalStoredFileId { get; set; }

    /// <summary>The <see cref="StoredFile"/> id of the final executed PDF (signatures + certificate). Null until completed.</summary>
    public int? SignedStoredFileId { get; set; }

    /// <summary>Document template used for this request, if the lease was rendered from a template.</summary>
    public int? DocumentTemplateId { get; set; }

    /// <summary>Template version frozen when this request was created.</summary>
    public int? DocumentTemplateVersion { get; set; }

    /// <summary>
    /// JSON snapshot of the field anchors/signing tabs used for this request. Freezes placement even
    /// when the landlord later edits the template.
    /// </summary>
    public string? TemplateFieldSnapshotJson { get; set; }

    /// <summary>Lower-case hex SHA-256 of the final executed PDF bytes, set when the request completes.</summary>
    public string? ContentSha256 { get; set; }

    public SignatureRequestStatus Status { get; set; } = SignatureRequestStatus.Sent;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When every signer had signed and the executed document was produced; null until then.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }
    public StoredFile? OriginalStoredFile { get; set; }
    public StoredFile? SignedStoredFile { get; set; }
    public List<SignatureSigner> Signers { get; set; } = [];
    public List<SignatureAuditEvent> AuditEvents { get; set; } = [];
}
