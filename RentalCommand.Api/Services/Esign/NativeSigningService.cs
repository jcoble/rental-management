using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Documents;
using RentalCommand.Api.Writes;
using RentalCommand.Data.Esign;

namespace RentalCommand.Api.Services.Esign;

/// <inheritdoc cref="INativeSigningService"/>
public sealed class NativeSigningService : INativeSigningService
{
    private readonly RentalCommandDbContext _db;
    private readonly IRequestWriteExecutor _writes;
    private readonly IFileStorage _storage;
    private readonly ILogger<NativeSigningService> _logger;
    private readonly IPendingFileUploadStore _pendingUploads;

    public NativeSigningService(
        RentalCommandDbContext db,
        IRequestWriteExecutor writes,
        IFileStorage storage,
        IPendingFileUploadStore pendingUploads,
        ILogger<NativeSigningService> logger)
    {
        _db = db;
        _writes = writes;
        _storage = storage;
        _pendingUploads = pendingUploads;
        _logger = logger;
    }

    public async Task<SignTokenResult<SignPackageResponse>> GetPackageAsync(
        string token, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return SignTokenResult<SignPackageResponse>.NotFound();
        }

        var now = DateTime.UtcNow;
        // Public tokens are attacker-controlled. Reject unknown tokens with one indexed DB query
        // before entering the atomic kernel so random probes cannot create durable receipts or
        // audit rows. The handler still resolves the request and rechecks state under its aggregate
        // lock; this preflight is only an admission boundary and does not replace that protection.
        var tokenExists = await _db.SignatureSigners.AsNoTracking()
            .AnyAsync(signer => signer.TokenHash == TokenHash(token), ct);
        if (!tokenExists)
        {
            return SignTokenResult<SignPackageResponse>.NotFound();
        }

        var viewCommand = new RecordNativeEsignViewCommand(TokenHash(token), ipAddress, userAgent, now);
        await _writes.ExecuteAsync(TokenIdentity(token),
            NativeEsignWriteSupport.Write<RecordNativeEsignViewCommand, RecordNativeEsignViewResult>(_db, viewCommand), ct);
        // The durable receipt proves only that the first-view command ran. Always project current state
        // after commit so later opens never replay stale status or stale expiry decisions.
        var current = await _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.TokenHash == TokenHash(token))
            .Select(signer => new
            {
                signer.NameSnapshot,
                signer.EmailSnapshot,
                signer.TokenExpiresAtUtc,
                SignerStatus = signer.Status,
                RequestStatus = signer.SignatureRequest!.Status,
                signer.SignatureRequest.Subject,
                DocumentName = signer.SignatureRequest.IssuedArtifact!.FileName,
                SenderName = signer.SignatureRequest.Portfolio!.ManagementCompanyName
                    ?? signer.SignatureRequest.Portfolio.Name,
            })
            .SingleOrDefaultAsync(ct);
        if (current is null)
        {
            return SignTokenResult<SignPackageResponse>.NotFound();
        }
        if (current.TokenExpiresAtUtc <= now
            && current.SignerStatus is not (SignatureSignerStatus.Signed or SignatureSignerStatus.Declined)
            && current.RequestStatus is not (SignatureRequestStatus.Completed
                or SignatureRequestStatus.Declined
                or SignatureRequestStatus.Voided))
        {
            return SignTokenResult<SignPackageResponse>.Expired(
                "This signing link has expired. Please ask the sender for a new one.");
        }

        var package = new SignPackageResponse
        {
            SignerName = current.NameSnapshot,
            SignerEmail = current.EmailSnapshot,
            Subject = current.Subject,
            DocumentName = current.DocumentName,
            SenderName = current.SenderName ?? "Rental Command",
            SignerStatus = current.SignerStatus.ToString(),
            RequestStatus = current.RequestStatus.ToString(),
            AlreadySigned = current.SignerStatus == SignatureSignerStatus.Signed,
            DocumentUrl = $"/api/v1/sign/{token}/document",
            ConsentDisclosure = EsignConsentText.ConsentDisclosure,
        };

        return SignTokenResult<SignPackageResponse>.Ok(package);
    }

    public async Task<SignTokenResult<(Stream Stream, string FileName, string ContentType)>> GetDocumentAsync(
        string token, CancellationToken ct = default)
    {
        // The document may be reviewed before signing AND retrieved after — don't require "still active".
        var (signer, request, error) = await ResolveAsync(token, requireActive: false, ct);
        if (error is not null)
        {
            return error.Cast<(Stream, string, string)>();
        }

        // Once executed, show the signed document; otherwise the original under review.
        var artifactId = request!.ExecutedArtifactId ?? request.IssuedArtifactId;
        var fileId = await _db.LegalDocumentArtifacts.AsNoTracking()
            .Where(artifact => artifact.Id == artifactId && artifact.PortfolioId == request.PortfolioId)
            .Select(artifact => artifact.StoredFileId)
            .SingleAsync(ct);
        var file = await _db.StoredFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.PortfolioId == request.PortfolioId && f.DeletedAt == null, ct);
        if (file is null)
        {
            return SignTokenResult<(Stream, string, string)>.NotFound();
        }

        try
        {
            var stream = await _storage.DownloadAsync(file.FilePath, ct);
            return SignTokenResult<(Stream, string, string)>.Ok((stream, file.FileName, file.ContentType));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native e-sign: document blob missing for token signer {SignerId}.", signer!.Id);
            return SignTokenResult<(Stream, string, string)>.NotFound();
        }
    }

    public async Task<SignTokenResult<SignActionResponse>> SignAsync(
        string token, SubmitSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (!request.Consent)
        {
            return SignTokenResult<SignActionResponse>.Invalid("You must agree to use electronic records and signatures to sign.");
        }

        var typed = string.Equals(request.SignatureType, "Typed", StringComparison.OrdinalIgnoreCase);
        var drawn = string.Equals(request.SignatureType, "Drawn", StringComparison.OrdinalIgnoreCase);
        if (!typed && !drawn)
        {
            return SignTokenResult<SignActionResponse>.Invalid("signatureType must be 'Typed' or 'Drawn'.");
        }

        byte[]? drawnBytes = null;
        if (typed)
        {
            if (string.IsNullOrWhiteSpace(request.TypedName))
            {
                return SignTokenResult<SignActionResponse>.Invalid("typedName is required for a typed signature.");
            }
        }
        else
        {
            drawnBytes = DecodeDataUrl(request.DrawnImage);
            if (drawnBytes is null || drawnBytes.Length == 0)
            {
                return SignTokenResult<SignActionResponse>.Invalid("drawnImage is required for a drawn signature.");
            }
        }

        var now = DateTime.UtcNow;
        var operationKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey;
        Guid? drawnAdmissionId = null;
        string? drawnFingerprint = null;
        string? drawnStorageKey = null;
        long? drawnFileSize = null;
        if (drawnBytes is not null)
        {
            var portfolioId = await _db.SignatureSigners.AsNoTracking()
                .Where(signer => signer.TokenHash == TokenHash(token))
                .Select(signer => (int?)signer.PortfolioId)
                .SingleOrDefaultAsync(ct);
            if (!portfolioId.HasValue) return SignTokenResult<SignActionResponse>.NotFound();
            drawnFingerprint = Convert.ToHexString(SHA256.HashData(drawnBytes)).ToLowerInvariant();
            var admission = await _pendingUploads.PrepareAsync(portfolioId.Value, 0,
                "native-esign-drawn-signature", $"{TokenHash(token)}:{operationKey}", drawnFingerprint,
                "drawn-signature.png", "image/png", drawnBytes.LongLength, now, ct);
            drawnAdmissionId = admission.Id;
            drawnStorageKey = admission.StoragePath;
            drawnFileSize = drawnBytes.LongLength;
            if (admission.State == PendingFileUploadState.Prepared)
            {
                await using var stream = new MemoryStream(drawnBytes);
                await _storage.UploadAtAsync(stream, admission.StoragePath, "drawn-signature.png", "image/png", ct);
            }
        }
        var command = new RecordNativeSignatureCommand(
                TokenHash(token),
                typed ? SignatureSignatureType.Typed : SignatureSignatureType.Drawn,
                typed ? request.TypedName!.Trim() : null,
                drawnAdmissionId,
                drawnFingerprint,
                drawnStorageKey,
                drawnFileSize,
                ipAddress,
                userAgent,
                now);
        var outcome = await _writes.ExecuteAsync(OperationIdentity(token, operationKey),
            NativeEsignWriteSupport.Write<RecordNativeSignatureCommand, NativeSignerActionResult>(_db, command), ct);
        if (outcome.Value.Outcome != NativeSignerActionOutcome.Applied)
        {
            return MapSignerActionError(outcome.Value);
        }

        return SignTokenResult<SignActionResponse>.Ok(new SignActionResponse
        {
            SignerStatus = outcome.Value.SignerStatus.ToString(),
            RequestStatus = outcome.Value.RequestStatus.ToString(),
            // Executed-PDF generation is deliberately reconciled by the Engine after the signer
            // transaction commits. The signature page can safely report success immediately while
            // the durable ExecutionPending packet is finalized without widening anonymous RLS to
            // the complete lease graph.
            RequestCompleted = outcome.Value.RequestStatus == SignatureRequestStatus.Completed,
        });
    }

    public async Task<SignTokenResult<SignActionResponse>> DeclineAsync(
        string token, DeclineSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var operationKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey;
        var command = new RecordNativeDeclineCommand(
            TokenHash(token), request.Reason?.Trim(), ipAddress, userAgent, now);
        var outcome = await _writes.ExecuteAsync(OperationIdentity(token, operationKey),
            NativeEsignWriteSupport.Write<RecordNativeDeclineCommand, NativeSignerActionResult>(_db, command), ct);
        if (outcome.Value.Outcome != NativeSignerActionOutcome.Applied)
        {
            return MapSignerActionError(outcome.Value);
        }

        return SignTokenResult<SignActionResponse>.Ok(new SignActionResponse
        {
            SignerStatus = outcome.Value.SignerStatus.ToString(),
            RequestStatus = outcome.Value.RequestStatus.ToString(),
            RequestCompleted = false,
        });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the signer + its tracked request by token. When <paramref name="requireActive"/> is true,
    /// the token must be usable for signing: not expired, not already signed/declined, and the request not
    /// finalized — otherwise a 410 Gone outcome is returned (single-use enforcement).
    /// </summary>
    private async Task<(SignatureSigner? Signer, SignatureRequest? Request, SignTokenError? Error)> ResolveAsync(
        string token, bool requireActive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (null, null, SignTokenError.NotFound());
        }

        var signer = await _db.SignatureSigners.AsNoTracking()
            .Include(s => s.SignatureRequest!)
            .FirstOrDefaultAsync(s => s.TokenHash == TokenHash(token), ct);
        if (signer?.SignatureRequest is null)
        {
            return (null, null, SignTokenError.NotFound());
        }

        var request = signer.SignatureRequest;

        if (signer.TokenExpiresAtUtc <= DateTime.UtcNow
            && signer.Status is not (SignatureSignerStatus.Signed or SignatureSignerStatus.Declined)
            && request.Status is not (SignatureRequestStatus.Completed or SignatureRequestStatus.Declined or SignatureRequestStatus.Voided))
        {
            return (signer, request, SignTokenError.Expired("This signing link has expired. Please ask the sender for a new one."));
        }

        if (requireActive)
        {
            if (signer.Status is SignatureSignerStatus.Signed)
            {
                return (signer, request, SignTokenError.Expired("You have already signed this document."));
            }
            if (signer.Status is SignatureSignerStatus.Declined)
            {
                return (signer, request, SignTokenError.Expired("This signing request was declined."));
            }
            if (request.Status is SignatureRequestStatus.Completed or SignatureRequestStatus.Declined or SignatureRequestStatus.Voided)
            {
                return (signer, request, SignTokenError.Expired("This signing request is no longer active."));
            }
        }

        return (signer, request, null);
    }

    /// <summary>Decodes a PNG data URL ("data:image/png;base64,...") or a bare base64 string to bytes.</summary>
    private static byte[]? DecodeDataUrl(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var payload = dataUrl.Trim();
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            payload = payload[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string OperationIdentity(string token, string operationKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}:{operationKey}"))).ToLowerInvariant();

    private static string TokenIdentity(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string TokenHash(string token) => TokenIdentity(token);

    private static SignTokenResult<SignActionResponse> MapSignerActionError(NativeSignerActionResult result) =>
        result.Outcome switch
        {
            NativeSignerActionOutcome.NotFound => SignTokenResult<SignActionResponse>.NotFound(),
            _ => SignTokenResult<SignActionResponse>.Expired(
                result.Error ?? "This signing request is no longer active."),
        };

    /// <summary>Internal error carrier so a single resolve can serve multiple result types.</summary>
    private sealed class SignTokenError
    {
        public SignTokenOutcome Outcome { get; private init; }
        public string Message { get; private init; } = string.Empty;

        public static SignTokenError NotFound() => new() { Outcome = SignTokenOutcome.NotFound, Message = "This signing link is invalid." };
        public static SignTokenError Expired(string msg) => new() { Outcome = SignTokenOutcome.Expired, Message = msg };

        public SignTokenResult<T> Cast<T>() => Outcome switch
        {
            SignTokenOutcome.Expired => SignTokenResult<T>.Expired(Message),
            _ => SignTokenResult<T>.NotFound(),
        };
    }
}
