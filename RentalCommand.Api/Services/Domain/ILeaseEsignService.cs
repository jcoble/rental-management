using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Drives the lease e-sign workflow: send a generated agreement out for electronic signature, report its
/// status, stream the signed PDF, and — from the provider webhook — flip a signed lease to Active. Every
/// operation is portfolio-scoped from the JWT; the webhook resolves the lease by the provider envelope id
/// (never a client parameter). The provider is gated: when no key is configured, <see cref="SendForSignatureAsync"/>
/// returns a "not configured" outcome and the lease is left untouched.
/// </summary>
public interface ILeaseEsignService
{
    Task<SendForSignatureResult> SendForSignatureAsync(
        int portfolioId, int leaseId, SendForSignatureRequest request, int? changedByUserId, string? ipAddress, CancellationToken ct = default);

    /// <summary>Current signature status for a lease; null when the lease is not in the portfolio.</summary>
    Task<LeaseSignatureStatusResponse?> GetSignatureStatusAsync(int portfolioId, int leaseId, CancellationToken ct = default);

    /// <summary>Stream the stored signed agreement PDF; null when the lease is out of scope or has no signed document.</summary>
    Task<(Stream Stream, string FileName, string ContentType)?> GetSignedDocumentAsync(int portfolioId, int leaseId, CancellationToken ct = default);

    /// <summary>
    /// Process a provider "signed/completed" event by envelope id: download + store the signed PDF, set
    /// <c>EsignStatus=Signed</c>, and flip a PendingSignature lease to Active. Idempotent and safe to call
    /// for unknown envelopes (no-op). Returns true when a matching lease was advanced.
    /// </summary>
    Task<bool> HandleSignedEventAsync(string envelopeId, CancellationToken ct = default);

    /// <summary>Record a provider "declined" event by envelope id (sets EsignStatus=Declined). No-op for unknown envelopes.</summary>
    Task<bool> HandleDeclinedEventAsync(string envelopeId, CancellationToken ct = default);
}
