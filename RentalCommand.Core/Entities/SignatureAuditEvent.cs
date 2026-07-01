using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// Append-only audit entry for the native e-sign trail (sent / viewed / signed / declined / completed).
/// Captures the actor (the optional <see cref="SignerId"/>), when, and the originating IP + user-agent —
/// the evidence that backs the ESIGN/UETA Certificate of Completion. Never updated or deleted.
/// </summary>
public class SignatureAuditEvent
{
    public int Id { get; set; }

    public int SignatureRequestId { get; set; }

    /// <summary>The signer the event is about; null for request-level events (e.g. system Completed).</summary>
    public int? SignerId { get; set; }

    public SignatureAuditEventType Type { get; set; }

    public DateTime AtUtc { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Free-form detail (e.g. signer name/email, signature type, decline reason).</summary>
    public string? Detail { get; set; }

    public SignatureRequest? SignatureRequest { get; set; }
    public SignatureSigner? Signer { get; set; }
}
