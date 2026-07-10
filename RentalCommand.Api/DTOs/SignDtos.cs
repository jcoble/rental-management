using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// The signing "package" returned by <c>GET /api/v1/sign/{token}</c> — everything the public signing page
/// needs to render the document, the ESIGN/UETA consent disclosure, and the signer's identity. The
/// document bytes themselves are streamed separately via <c>GET /api/v1/sign/{token}/document</c>.
/// </summary>
public sealed class SignPackageResponse
{
    /// <summary>Signer's display name (pre-fills the typed-signature field).</summary>
    public string SignerName { get; init; } = string.Empty;

    /// <summary>Signer's email (shown for confirmation; not editable).</summary>
    public string SignerEmail { get; init; } = string.Empty;

    /// <summary>Human-readable subject/title of the request (e.g. "Lease L-2026-7").</summary>
    public string? Subject { get; init; }

    /// <summary>Display name of the document under review.</summary>
    public string DocumentName { get; init; } = string.Empty;

    /// <summary>Management company / sender name, for context on the signing page.</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>Where this signer currently sits: Pending | Viewed | Signed | Declined (string on the wire).</summary>
    public string SignerStatus { get; init; } = string.Empty;

    /// <summary>Overall request status: Sent | Viewed | PartiallySigned | Completed | Declined | Voided.</summary>
    public string RequestStatus { get; init; } = string.Empty;

    /// <summary>True once this signer has already signed (the page should show a "signed" confirmation).</summary>
    public bool AlreadySigned { get; init; }

    /// <summary>Relative URL to stream the PDF for review.</summary>
    public string DocumentUrl { get; init; } = string.Empty;

    /// <summary>The full ESIGN/UETA electronic-records consent disclosure the signer must agree to.</summary>
    public string ConsentDisclosure { get; init; } = string.Empty;

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => "sign-package";
}

/// <summary>Body for <c>POST /api/v1/sign/{token}</c> — the captured signature + consent.</summary>
public sealed class SubmitSignatureRequest
{
    [Required]
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>ESIGN/UETA consent. Must be true; the controller rejects a missing/false value.</summary>
    public bool Consent { get; set; }

    /// <summary>"Typed" or "Drawn".</summary>
    [Required]
    [MaxLength(20)]
    public string SignatureType { get; set; } = string.Empty;

    /// <summary>The typed name (required when <see cref="SignatureType"/> is "Typed").</summary>
    [MaxLength(200)]
    public string? TypedName { get; set; }

    /// <summary>
    /// The drawn signature as a PNG data URL or bare base64 (required when <see cref="SignatureType"/> is
    /// "Drawn"). Accepts "data:image/png;base64,AAAA..." or just "AAAA...".
    /// </summary>
    public string? DrawnImage { get; set; }
}

/// <summary>Body for <c>POST /api/v1/sign/{token}/decline</c>.</summary>
public sealed class DeclineSignatureRequest
{
    [Required]
    [MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Optional free-text reason captured on the audit trail.</summary>
    [MaxLength(1000)]
    public string? Reason { get; set; }
}

/// <summary>Result returned after a sign/decline action — the new coarse status the page renders.</summary>
public sealed class SignActionResponse
{
    /// <summary>This signer's new status: Signed | Declined.</summary>
    public string SignerStatus { get; init; } = string.Empty;

    /// <summary>The request's new status after this action.</summary>
    public string RequestStatus { get; init; } = string.Empty;

    /// <summary>True when this action completed the whole request (all signers done).</summary>
    public bool RequestCompleted { get; init; }

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => "sign-action";
}
