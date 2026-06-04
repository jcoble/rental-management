using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

/// <summary>
/// One recipient on a native e-sign <see cref="SignatureRequest"/>. Each signer gets a fresh opaque,
/// single-use, expiring <see cref="Token"/> embedded in the public signing link
/// (<c>{webBaseUrl}/sign/{token}</c>). The public endpoints resolve the signer by token ONLY — never by
/// a client-supplied id — so one token exposes only its own signer's document (IDOR-safe). The captured
/// signature plus the ESIGN/UETA audit fields (consent, IP, user-agent, timestamps) live here.
/// </summary>
public class SignatureSigner
{
    public int Id { get; set; }

    public int SignatureRequestId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Opaque, URL-safe, unique token that authorizes this signer's signing session. Single-use: once the
    /// signer signs or declines the token no longer grants signing. Expires at <see cref="ExpiresAtUtc"/>.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>When the signing link stops working (typically 14 days after sending).</summary>
    public DateTime ExpiresAtUtc { get; set; }

    public SignatureSignerStatus Status { get; set; } = SignatureSignerStatus.Pending;

    // --- Captured signature ---

    public SignatureSignatureType SignatureType { get; set; } = SignatureSignatureType.None;

    /// <summary>The name the signer typed (for a Typed signature), rendered in script on the executed PDF.</summary>
    public string? TypedName { get; set; }

    /// <summary>Raw PNG bytes of the drawn signature (for a Drawn signature); null otherwise.</summary>
    public byte[]? DrawnSignatureImage { get; set; }

    /// <summary>Explicit ESIGN/UETA electronic-records consent, captured at signing time.</summary>
    public bool ConsentGiven { get; set; }

    public DateTime? SignedAtUtc { get; set; }

    // --- Audit capture (ESIGN/UETA trail) ---

    /// <summary>When this signer first opened the signing page; null until viewed.</summary>
    public DateTime? ViewedAtUtc { get; set; }

    /// <summary>IP address recorded when the signer signed (falls back to the view IP).</summary>
    public string? IpAddress { get; set; }

    /// <summary>User-agent recorded when the signer signed (falls back to the view user-agent).</summary>
    public string? UserAgent { get; set; }

    public SignatureRequest? SignatureRequest { get; set; }
}
