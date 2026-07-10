using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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
    private readonly ILeaseEsignService _leaseEsign;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NativeSigningService> _logger;

    public NativeSigningService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        INativeEsignExecutionService execution,
        ILeaseEsignService leaseEsign,
        TimeProvider timeProvider,
        ILogger<NativeSigningService> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _execution = execution;
        _leaseEsign = leaseEsign;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SignTokenResult<SignPackageResponse>> GetPackageAsync(
        string token, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var (signer, request, error) = await ResolveAsync(token, requireActive: false, ct);
        if (error is not null)
        {
            return error.Cast<SignPackageResponse>();
        }

        var now = _timeProvider.UtcNow();

        // Mark Viewed + audit on the first open. Only meaningful while the signer is still pending.
        if (signer!.Status == SignatureSignerStatus.Pending)
        {
            signer.Status = SignatureSignerStatus.Viewed;
            signer.ViewedAtUtc ??= now;
            signer.IpAddress ??= ipAddress;
            signer.UserAgent ??= userAgent;

            if (request!.Status == SignatureRequestStatus.Sent)
            {
                request.Status = SignatureRequestStatus.Viewed;
            }

            _db.SignatureAuditEvents.Add(new SignatureAuditEvent
            {
                SignatureRequestId = request!.Id,
                SignerId = signer.Id,
                Type = SignatureAuditEventType.Viewed,
                AtUtc = now,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                Detail = $"{signer.Name} opened the signing page.",
            });

            await _db.SaveChangesAsync(ct);

            // Best-effort: surface the "viewed" status onto the lease workflow if it tracks it.
            await SafeAsync("lease viewed sync", () => _leaseEsign.GetSignatureStatusAsync(request.PortfolioId, request.LeaseId, ct));
        }

        var portfolio = await _db.Portfolios.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request!.PortfolioId, ct);

        var package = new SignPackageResponse
        {
            SignerName = signer.Name,
            SignerEmail = signer.Email,
            Subject = request!.Subject,
            DocumentName = request.DocumentName,
            SenderName = portfolio?.ManagementCompanyName ?? portfolio?.Name ?? "Rental Command",
            SignerStatus = signer.Status.ToString(),
            RequestStatus = request.Status.ToString(),
            AlreadySigned = signer.Status == SignatureSignerStatus.Signed,
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

        // Tracked load (we mutate signer/request). Include sibling signers so completion can check "all signed".
        var signer = await _db.SignatureSigners
            .Include(s => s.SignatureRequest!).ThenInclude(r => r.Signers)
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

    private static SignTokenResult<SignActionResponse> MapSignerActionError(NativeSignerActionResult result) =>
        result.Outcome switch
        {
            NativeSignerActionOutcome.NotFound => SignTokenResult<SignActionResponse>.NotFound(),
            _ => SignTokenResult<SignActionResponse>.Expired(
                result.Error ?? "This signing request is no longer active."),
        };

    private async Task SafeAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native e-sign side effect '{Label}' failed (continuing).", label);
        }
    }

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
