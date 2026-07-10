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
    private readonly IExecutedLeasePdfGenerator _executedPdf;
    private readonly ILeaseEsignService _leaseEsign;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NativeSigningService> _logger;

    public NativeSigningService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        IExecutedLeasePdfGenerator executedPdf,
        ILeaseEsignService leaseEsign,
        TimeProvider timeProvider,
        ILogger<NativeSigningService> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _executedPdf = executedPdf;
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
            var sigRequest = await _db.SignatureRequests
                .SingleAsync(candidate => candidate.Id == outcome.Value.SignatureRequestId, ct);
            completed = await CompleteRequestAsync(sigRequest, now, ct);
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
    // Completion: render executed PDF + certificate, hash, store, update lease.
    // -------------------------------------------------------------------------

    private async Task<bool> CompleteRequestAsync(SignatureRequest sigRequest, DateTime now, CancellationToken ct)
    {
        var data = await BuildExecutedDataAsync(sigRequest, now, ct);
        if (data is null)
        {
            _logger.LogWarning("Native e-sign: could not build executed document for request {PublicId} (lease graph missing).", sigRequest.PublicId);
            return false;
        }

        // Render once; hash the exact stored bytes (the certificate references the hash as computed on
        // storage so there is no chicken-and-egg between the bytes and their own hash).
        var executedBytes = _executedPdf.Generate(data, contentSha256: string.Empty);
        var sha256 = Convert.ToHexString(SHA256.HashData(executedBytes)).ToLowerInvariant();

        var fileName = $"lease-{sigRequest.LeaseId}-executed.pdf";
        string storageKey;
        await using (var stream = new MemoryStream(executedBytes))
        {
            storageKey = await _storage.UploadAsync(stream, fileName, "application/pdf", ct);
        }

        AtomicCommandOutcome<FinalizeNativeEsignRequestResult> outcome;
        try
        {
            outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("native-esign.finalize", TokenIdentity(sigRequest.PublicId)),
                new FinalizeNativeEsignRequestCommand(
                    sigRequest.PublicId,
                    storageKey,
                    fileName,
                    executedBytes.LongLength,
                    sha256,
                    now),
                new AtomicJsonResultCodec<FinalizeNativeEsignRequestResult>("native-esign.finalize.v1"),
                ct);
        }
        catch
        {
            await DeleteFailedExecutionUploadIfUnreferencedAsync(sigRequest.PublicId, storageKey);
            throw;
        }

        if (outcome.Disposition == AtomicCommandDisposition.Replayed)
        {
            await DeleteReplayExecutionUploadIfUnreferencedAsync(outcome.Value.StoredFileId, storageKey);
        }

        return true;
    }

    private async Task<ExecutedLeaseData?> BuildExecutedDataAsync(SignatureRequest sigRequest, DateTime now, CancellationToken ct)
    {
        var lease = await _db.Leases.AsNoTracking()
            .Include(l => l.Tenant)
            .Include(l => l.Unit)
            .Include(l => l.Property)
            .FirstOrDefaultAsync(l => l.Id == sigRequest.LeaseId && l.PortfolioId == sigRequest.PortfolioId, ct);
        if (lease is null)
        {
            return null;
        }

        var portfolio = await _db.Portfolios.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == sigRequest.PortfolioId, ct);

        var landlordName = !string.IsNullOrWhiteSpace(portfolio?.ManagementCompanyName)
            ? portfolio!.ManagementCompanyName
            : portfolio?.Name ?? "Landlord";

        var tenantName = lease.Tenant is null
            ? string.Empty
            : $"{lease.Tenant.FirstName} {lease.Tenant.LastName}".Trim();

        var property = lease.Property;
        var propertyAddress = property is null
            ? string.Empty
            : string.Join(", ", new[]
            {
                property.AddressLine1,
                property.AddressLine2,
                $"{property.City}, {property.State} {property.PostalCode}".Trim(),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var agreement = new LeaseAgreementData
        {
            Lease = lease,
            LandlordName = landlordName,
            TenantName = tenantName,
            PropertyName = property?.Name ?? string.Empty,
            PropertyAddress = propertyAddress,
            UnitNumber = lease.Unit?.UnitNumber,
            State = property?.State ?? string.Empty,
            YearBuilt = property?.YearBuilt,
        };

        var originalDocumentBytes = sigRequest.DocumentTemplateId.HasValue
            ? await TryLoadOriginalDocumentBytesAsync(sigRequest, ct)
            : null;

        var signerRows = await _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.SignatureRequestId == sigRequest.Id)
            .OrderBy(signer => signer.Id)
            .ToListAsync(ct);
        var signers = BuildExecutedSigners(signerRows, tenantName, lease.Tenant?.Email);

        return new ExecutedLeaseData
        {
            Agreement = agreement,
            Signers = signers,
            LandlordName = landlordName,
            EnvelopeId = sigRequest.PublicId,
            DocumentName = sigRequest.DocumentName,
            OriginalDocumentBytes = originalDocumentBytes,
            TemplateFieldSnapshotJson = sigRequest.TemplateFieldSnapshotJson,
            CompletedAtUtc = now,
        };
    }

    private async Task<byte[]?> TryLoadOriginalDocumentBytesAsync(SignatureRequest sigRequest, CancellationToken ct)
    {
        var file = await _db.StoredFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == sigRequest.OriginalStoredFileId
                && f.PortfolioId == sigRequest.PortfolioId
                && f.DeletedAt == null, ct);
        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = await _storage.DownloadAsync(file.FilePath, ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Native e-sign: original template document missing for request {PublicId}.",
                sigRequest.PublicId);
            return null;
        }
    }

    private static List<ExecutedSigner> BuildExecutedSigners(
        IReadOnlyList<SignatureSigner> source,
        string tenantName,
        string? tenantEmail)
    {
        var roles = new DocumentTemplateSignerRole[source.Count];
        var signers = new List<ExecutedSigner>(source.Count);
        var tenantAssigned = false;
        var landlordAssigned = false;

        for (var i = 0; i < source.Count; i++)
        {
            var signer = source[i];
            var role = DocumentTemplateSignerRole.None;
            if (!tenantAssigned && IsTenantSigner(signer, tenantName, tenantEmail))
            {
                role = DocumentTemplateSignerRole.Tenant;
                tenantAssigned = true;
            }
            else if (!landlordAssigned)
            {
                role = DocumentTemplateSignerRole.Landlord;
                landlordAssigned = true;
            }

            roles[i] = role;
        }

        if (!tenantAssigned && source.Count > 0)
        {
            roles[0] = DocumentTemplateSignerRole.Tenant;
            if (!roles.Contains(DocumentTemplateSignerRole.Landlord) && source.Count > 1)
            {
                roles[1] = DocumentTemplateSignerRole.Landlord;
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var signer = source[i];
            signers.Add(new ExecutedSigner
            {
                Name = signer.Name,
                Email = signer.Email,
                SignerRole = roles[i],
                SignatureType = signer.SignatureType,
                TypedName = signer.TypedName,
                DrawnSignatureImage = signer.DrawnSignatureImage,
                SignedAtUtc = signer.SignedAtUtc,
                IpAddress = signer.IpAddress,
                UserAgent = signer.UserAgent,
                ViewedAtUtc = signer.ViewedAtUtc,
                ConsentGiven = signer.ConsentGiven,
            });
        }

        return signers;
    }

    private static bool IsTenantSigner(SignatureSigner signer, string tenantName, string? tenantEmail)
    {
        if (!string.IsNullOrWhiteSpace(tenantEmail)
            && string.Equals(signer.Email.Trim(), tenantEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(tenantName)
            && string.Equals(signer.Name.Trim(), tenantName.Trim(), StringComparison.OrdinalIgnoreCase);
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

    private static string TokenIdentity(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task DeleteReplayExecutionUploadIfUnreferencedAsync(int storedFileId, string storageKey)
    {
        try
        {
            var committedKey = await _db.StoredFiles.AsNoTracking()
                .Where(file => file.Id == storedFileId)
                .Select(file => file.FilePath)
                .SingleOrDefaultAsync(CancellationToken.None);
            if (committedKey is not null && !string.Equals(committedKey, storageKey, StringComparison.Ordinal))
            {
                await DeleteExecutionUploadAsync(storageKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify replayed executed-document upload {StorageKey}; preserving it.", storageKey);
        }
    }

    private async Task DeleteFailedExecutionUploadIfUnreferencedAsync(string publicId, string storageKey)
    {
        try
        {
            var committedKey = await _db.SignatureRequests.AsNoTracking()
                .Where(request => request.PublicId == publicId && request.SignedStoredFileId != null)
                .Select(request => request.SignedStoredFile!.FilePath)
                .SingleOrDefaultAsync(CancellationToken.None);
            if (committedKey is null || !string.Equals(committedKey, storageKey, StringComparison.Ordinal))
            {
                await DeleteExecutionUploadAsync(storageKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify failed executed-document upload {StorageKey}; preserving it.", storageKey);
        }
    }

    private async Task DeleteExecutionUploadAsync(string storageKey)
    {
        try
        {
            await _storage.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove unreferenced executed-document upload {StorageKey}.", storageKey);
        }
    }

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
