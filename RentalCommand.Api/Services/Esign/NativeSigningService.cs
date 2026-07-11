using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Esign;

/// <inheritdoc cref="INativeSigningService"/>
public sealed class NativeSigningService : INativeSigningService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IFileStorage _storage;
    private readonly INativeEsignExecutionService _execution;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NativeSigningService> _logger;

    public NativeSigningService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        INativeEsignExecutionService execution,
        TimeProvider timeProvider,
        ILogger<NativeSigningService> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _execution = execution;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SignTokenResult<SignPackageResponse>> GetPackageAsync(
        string token, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return SignTokenResult<SignPackageResponse>.NotFound();
        }

        var now = _timeProvider.UtcNow();
        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.view", TokenIdentity(token)),
            new RecordNativeEsignViewCommand(token, ipAddress, userAgent, now),
            new AtomicJsonResultCodec<RecordNativeEsignViewResult>("native-esign.view.v1"),
            ct);
        // The durable receipt proves only that the first-view command ran. Always project current state
        // after commit so later opens never replay stale status or stale expiry decisions.
        var current = await _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.Token == token)
            .Select(signer => new
            {
                signer.Name,
                signer.Email,
                signer.ExpiresAtUtc,
                SignerStatus = signer.Status,
                RequestStatus = signer.SignatureRequest!.Status,
                signer.SignatureRequest.Subject,
                signer.SignatureRequest.DocumentName,
                SenderName = signer.SignatureRequest.Portfolio!.ManagementCompanyName
                    ?? signer.SignatureRequest.Portfolio.Name,
            })
            .SingleOrDefaultAsync(ct);
        if (current is null)
        {
            return SignTokenResult<SignPackageResponse>.NotFound();
        }
        if (current.ExpiresAtUtc <= now
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
            SignerName = current.Name,
            SignerEmail = current.Email,
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
        var fileId = request!.SignedStoredFileId ?? request.OriginalStoredFileId;
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

        var now = _timeProvider.UtcNow();
        var operationKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey;
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.sign", OperationIdentity(token, operationKey)),
            new RecordNativeSignatureCommand(
                token,
                typed ? SignatureSignatureType.Typed : SignatureSignatureType.Drawn,
                typed ? request.TypedName!.Trim() : null,
                drawnBytes,
                ipAddress,
                userAgent,
                now),
            new AtomicJsonResultCodec<NativeSignerActionResult>("native-esign.sign.v1"),
            ct);
        if (outcome.Value.Outcome != NativeSignerActionOutcome.Applied)
        {
            return MapSignerActionError(outcome.Value);
        }

        var completed = outcome.Value.RequestStatus == SignatureRequestStatus.Completed;
        var responseStatus = outcome.Value.RequestStatus;
        if (outcome.Value.ExecutionRequired)
        {
            completed = await _execution.FinalizePendingAsync(outcome.Value.SignatureRequestId, ct);
            responseStatus = completed ? SignatureRequestStatus.Completed : SignatureRequestStatus.ExecutionPending;
        }

        return SignTokenResult<SignActionResponse>.Ok(new SignActionResponse
        {
            SignerStatus = outcome.Value.SignerStatus.ToString(),
            RequestStatus = responseStatus.ToString(),
            RequestCompleted = completed,
        });
    }

    public async Task<SignTokenResult<SignActionResponse>> DeclineAsync(
        string token, DeclineSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();
        var operationKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey;
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("native-esign.decline", OperationIdentity(token, operationKey)),
            new RecordNativeDeclineCommand(token, request.Reason?.Trim(), ipAddress, userAgent, now),
            new AtomicJsonResultCodec<NativeSignerActionResult>("native-esign.decline.v1"),
            ct);
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
            .FirstOrDefaultAsync(s => s.Token == token, ct);
        if (signer?.SignatureRequest is null)
        {
            return (null, null, SignTokenError.NotFound());
        }

        var request = signer.SignatureRequest;

        if (signer.ExpiresAtUtc <= DateTime.UtcNow
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
