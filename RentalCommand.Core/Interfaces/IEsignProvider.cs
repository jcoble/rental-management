namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over an e-signature provider (e.g. Dropbox Sign / HelloSign). The integration is GATED
/// like Stripe and the LLM provider: when no API key is configured a disabled implementation is used
/// that returns a clear "not configured" result and never contacts the provider.
/// </summary>
public interface IEsignProvider : RentalCommand.Core.Atomic.IAtomicRemoteDependency
{
    /// <summary>True when the provider is configured (an API key is present) and will make real calls.</summary>
    bool IsConfigured { get; }

    /// <summary>Send a document out for signature and return a reference to the signing request.</summary>
    Task<EsignResult> SendForSignatureAsync(
        EsignRequest request,
        CancellationToken ct = default);

    /// <summary>Fetch the current status of a previously sent signing request.</summary>
    Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default);

    /// <summary>
    /// Download the fully-signed document for a completed signing request. Returns null when the
    /// provider is not configured or the signed file cannot be retrieved.
    /// </summary>
    Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default);
}

/// <summary>A document and its recipients submitted for signature.</summary>
public class EsignRequest
{
    public string DocumentName { get; set; } = string.Empty;
    public byte[] DocumentBytes { get; set; } = Array.Empty<byte>();
    public IReadOnlyList<EsignSigner> Signers { get; set; } = Array.Empty<EsignSigner>();

    /// <summary>Optional human-readable subject/title shown to the signer (e.g. "Lease L-2026-7").</summary>
    public string? Subject { get; set; }

    /// <summary>Document template used to render the submitted PDF, when applicable.</summary>
    public int? DocumentTemplateId { get; set; }

    /// <summary>Template version frozen when the submitted PDF was rendered.</summary>
    public int? DocumentTemplateVersion { get; set; }

    /// <summary>JSON snapshot of the template fields used for this signing request.</summary>
    public string? TemplateFieldSnapshotJson { get; set; }
}

/// <summary>A single signer on an e-sign request.</summary>
public class EsignSigner
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

/// <summary>Outcome/status of an e-sign operation.</summary>
public class EsignResult
{
    /// <summary>Provider-side identifier for the signing request/envelope.</summary>
    public string? EnvelopeId { get; set; }

    /// <summary>Provider status (e.g. "Sent", "Completed", "Declined").</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>False when the provider is not configured; the operation was a no-op (never a false success).</summary>
    public bool IsConfigured { get; set; } = true;

    /// <summary>Human-readable error/explanation when the operation could not be performed.</summary>
    public string? Error { get; set; }

    /// <summary>A successful, configured result carrying the provider envelope id + status.</summary>
    public static EsignResult Sent(string envelopeId, string status)
        => new() { EnvelopeId = envelopeId, Status = status, IsConfigured = true };

    /// <summary>The provider is not configured — a clear no-op result, never a false success.</summary>
    public static EsignResult NotConfigured()
        => new() { IsConfigured = false, Status = "NotConfigured", Error = "E-sign provider is not configured." };
}
