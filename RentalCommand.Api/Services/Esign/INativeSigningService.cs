using RentalCommand.Api.DTOs;

namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// Drives the PUBLIC, token-scoped native e-sign signing flow used by <c>SignController</c>. Every method
/// resolves the signer by their opaque single-use token ONLY (never a client-supplied id), so a token can
/// only ever expose/act on its own signer's document (IDOR-safe). Tokens are single-use and expiring.
/// </summary>
public interface INativeSigningService
{
    /// <summary>
    /// The signing package for a signer (name, subject, document name, consent disclosure). Marks the
    /// signer Viewed and writes an audit event (capturing IP + user-agent) on the first view.
    /// </summary>
    Task<SignTokenResult<SignPackageResponse>> GetPackageAsync(
        string token, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>Stream the document the signer is reviewing (original until completed; executed afterwards).</summary>
    Task<SignTokenResult<(Stream Stream, string FileName, string ContentType)>> GetDocumentAsync(
        string token, CancellationToken ct = default);

    /// <summary>
    /// Apply the signer's signature + consent. On the LAST signer this generates + stores the executed PDF
    /// (signatures + Certificate of Completion + SHA-256), completes the request, and updates the lease
    /// (agreement execution state, signed artifact, and lifecycle facts).
    /// </summary>
    Task<SignTokenResult<SignActionResponse>> SignAsync(
        string token, SubmitSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>Record the signer's decline; reflects Declined on the request and the lease.</summary>
    Task<SignTokenResult<SignActionResponse>> DeclineAsync(
        string token, DeclineSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
}

/// <summary>Outcome of a token-scoped signing operation, mapped to HTTP by the controller.</summary>
public enum SignTokenOutcome
{
    Ok,

    /// <summary>The token does not match any signer (404).</summary>
    NotFound,

    /// <summary>The token has expired or was already used/finalized (410 Gone).</summary>
    Expired,

    /// <summary>The request body failed validation (400).</summary>
    Invalid,
}

/// <summary>Result wrapper carrying an outcome + an optional payload + an error message.</summary>
public sealed class SignTokenResult<T>
{
    public SignTokenOutcome Outcome { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }

    public static SignTokenResult<T> Ok(T value) => new() { Outcome = SignTokenOutcome.Ok, Value = value };
    public static SignTokenResult<T> NotFound() => new() { Outcome = SignTokenOutcome.NotFound, Error = "This signing link is invalid." };
    public static SignTokenResult<T> Expired(string error) => new() { Outcome = SignTokenOutcome.Expired, Error = error };
    public static SignTokenResult<T> Invalid(string error) => new() { Outcome = SignTokenOutcome.Invalid, Error = error };
}
