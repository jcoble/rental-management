namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Abstraction over an e-signature provider (e.g. DocuSign/Dropbox Sign). Phase 0 defines the
/// contract only; a concrete integration lands in a later phase.
/// </summary>
public interface IEsignProvider
{
    /// <summary>Send a document out for signature and return a reference to the signing request.</summary>
    Task<EsignResult> SendForSignatureAsync(
        EsignRequest request,
        CancellationToken ct = default);

    /// <summary>Fetch the current status of a previously sent signing request.</summary>
    Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default);
}

/// <summary>A document and its recipients submitted for signature.</summary>
public class EsignRequest
{
    public string DocumentName { get; set; } = string.Empty;
    public byte[] DocumentBytes { get; set; } = Array.Empty<byte>();
    public IReadOnlyList<EsignSigner> Signers { get; set; } = Array.Empty<EsignSigner>();
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
}
