using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Esign;

/// <inheritdoc cref="INativeSigningService"/>
public sealed class NativeSigningService : INativeSigningService
{
    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly IExecutedLeasePdfGenerator _executedPdf;
    private readonly ILeaseEsignService _leaseEsign;
    private readonly ILogger<NativeSigningService> _logger;

    public NativeSigningService(
        RentalCommandDbContext db,
        IFileStorage storage,
        IExecutedLeasePdfGenerator executedPdf,
        ILeaseEsignService leaseEsign,
        ILogger<NativeSigningService> logger)
    {
        _db = db;
        _storage = storage;
        _executedPdf = executedPdf;
        _leaseEsign = leaseEsign;
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

        var now = DateTime.UtcNow;

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

        var (signer, sigRequest, error) = await ResolveAsync(token, requireActive: true, ct);
        if (error is not null)
        {
            return error.Cast<SignActionResponse>();
        }

        var now = DateTime.UtcNow;

        signer!.Status = SignatureSignerStatus.Signed;
        signer.SignatureType = typed ? SignatureSignatureType.Typed : SignatureSignatureType.Drawn;
        signer.TypedName = typed ? request.TypedName!.Trim() : null;
        signer.DrawnSignatureImage = drawnBytes;
        signer.ConsentGiven = true;
        signer.SignedAtUtc = now;
        // Signing IP/UA take precedence over the earlier view capture.
        signer.IpAddress = ipAddress ?? signer.IpAddress;
        signer.UserAgent = userAgent ?? signer.UserAgent;
        signer.ViewedAtUtc ??= now;

        _db.SignatureAuditEvents.Add(new SignatureAuditEvent
        {
            SignatureRequestId = sigRequest!.Id,
            SignerId = signer.Id,
            Type = SignatureAuditEventType.Signed,
            AtUtc = now,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Detail = $"{signer.Name} signed ({signer.SignatureType}).",
        });

        // All signers signed? -> complete the request + execute the document + update the lease.
        var allSigned = sigRequest.Signers.All(s => s.Status == SignatureSignerStatus.Signed);
        sigRequest.Status = allSigned ? SignatureRequestStatus.Completed : SignatureRequestStatus.PartiallySigned;

        await _db.SaveChangesAsync(ct);

        var completed = false;
        if (allSigned)
        {
            await CompleteRequestAsync(sigRequest, now, ct);
            completed = true;
        }

        return SignTokenResult<SignActionResponse>.Ok(new SignActionResponse
        {
            SignerStatus = signer.Status.ToString(),
            RequestStatus = sigRequest.Status.ToString(),
            RequestCompleted = completed,
        });
    }

    public async Task<SignTokenResult<SignActionResponse>> DeclineAsync(
        string token, DeclineSignatureRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var (signer, sigRequest, error) = await ResolveAsync(token, requireActive: true, ct);
        if (error is not null)
        {
            return error.Cast<SignActionResponse>();
        }

        var now = DateTime.UtcNow;

        signer!.Status = SignatureSignerStatus.Declined;
        signer.IpAddress = ipAddress ?? signer.IpAddress;
        signer.UserAgent = userAgent ?? signer.UserAgent;

        sigRequest!.Status = SignatureRequestStatus.Declined;

        _db.SignatureAuditEvents.Add(new SignatureAuditEvent
        {
            SignatureRequestId = sigRequest.Id,
            SignerId = signer.Id,
            Type = SignatureAuditEventType.Declined,
            AtUtc = now,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Detail = string.IsNullOrWhiteSpace(request.Reason)
                ? $"{signer.Name} declined to sign."
                : $"{signer.Name} declined to sign: {request.Reason!.Trim()}",
        });

        // Ensure the lease ↔ envelope link exists so the decline reliably resolves the lease.
        var declinedLease = await _db.Leases.FirstOrDefaultAsync(
            l => l.Id == sigRequest.LeaseId && l.PortfolioId == sigRequest.PortfolioId, ct);
        if (declinedLease is not null && declinedLease.EsignEnvelopeId != sigRequest.PublicId)
        {
            declinedLease.EsignEnvelopeId = sigRequest.PublicId;
        }

        await _db.SaveChangesAsync(ct);

        // Reflect the decline on the lease (EsignStatus=Declined) via the existing webhook-equivalent path.
        await SafeAsync("lease decline sync", () => _leaseEsign.HandleDeclinedEventAsync(sigRequest.PublicId, ct));

        return SignTokenResult<SignActionResponse>.Ok(new SignActionResponse
        {
            SignerStatus = signer.Status.ToString(),
            RequestStatus = sigRequest.Status.ToString(),
            RequestCompleted = false,
        });
    }

    // -------------------------------------------------------------------------
    // Completion: render executed PDF + certificate, hash, store, update lease.
    // -------------------------------------------------------------------------

    private async Task CompleteRequestAsync(SignatureRequest sigRequest, DateTime now, CancellationToken ct)
    {
        var data = await BuildExecutedDataAsync(sigRequest, now, ct);
        if (data is null)
        {
            _logger.LogWarning("Native e-sign: could not build executed document for request {PublicId} (lease graph missing).", sigRequest.PublicId);
            return;
        }

        // Render once; hash the exact stored bytes (the certificate references the hash as computed on
        // storage so there is no chicken-and-egg between the bytes and their own hash).
        var executedBytes = _executedPdf.Generate(data, contentSha256: string.Empty);
        var sha256 = Convert.ToHexString(SHA256.HashData(executedBytes)).ToLowerInvariant();

        var fileName = $"lease-{sigRequest.LeaseId}-executed.pdf";
        var storedFileId = await StoreDocumentAsync(sigRequest.PortfolioId, sigRequest.LeaseId, executedBytes, fileName, ct);

        sigRequest.SignedStoredFileId = storedFileId;
        sigRequest.ContentSha256 = sha256;
        sigRequest.CompletedAtUtc = now;
        sigRequest.Status = SignatureRequestStatus.Completed;

        _db.SignatureAuditEvents.Add(new SignatureAuditEvent
        {
            SignatureRequestId = sigRequest.Id,
            Type = SignatureAuditEventType.Completed,
            AtUtc = now,
            Detail = $"All signers signed. Executed document SHA-256 {sha256}.",
        });

        await _db.SaveChangesAsync(ct);

        // The lease e-sign service resolves the lease by its EsignEnvelopeId. Ensure that link exists
        // (it is normally set by LeaseEsignService.SendForSignatureAsync, but native requests created via
        // other paths may not have stamped it) so completion reliably advances the lease.
        var lease = await _db.Leases.FirstOrDefaultAsync(
            l => l.Id == sigRequest.LeaseId && l.PortfolioId == sigRequest.PortfolioId, ct);
        if (lease is not null && lease.EsignEnvelopeId != sigRequest.PublicId)
        {
            lease.EsignEnvelopeId = sigRequest.PublicId;
            await _db.SaveChangesAsync(ct);
        }

        // Hand off to the lease e-sign service: it downloads the executed PDF via the native provider's
        // DownloadSignedDocumentAsync (now that SignedStoredFileId is set), stores it on the lease, sets
        // EsignStatus=Signed, and flips a PendingSignature lease to Active. Resolved by envelope id only.
        await SafeAsync("lease signed sync", () => _leaseEsign.HandleSignedEventAsync(sigRequest.PublicId, ct));
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

        var signers = sigRequest.Signers
            .OrderBy(s => s.Id)
            .Select(s => new ExecutedSigner
            {
                Name = s.Name,
                Email = s.Email,
                SignatureType = s.SignatureType,
                TypedName = s.TypedName,
                DrawnSignatureImage = s.DrawnSignatureImage,
                SignedAtUtc = s.SignedAtUtc,
                IpAddress = s.IpAddress,
                UserAgent = s.UserAgent,
                ViewedAtUtc = s.ViewedAtUtc,
                ConsentGiven = s.ConsentGiven,
            })
            .ToList();

        return new ExecutedLeaseData
        {
            Agreement = agreement,
            Signers = signers,
            LandlordName = landlordName,
            EnvelopeId = sigRequest.PublicId,
            CompletedAtUtc = now,
        };
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

    private async Task<int> StoreDocumentAsync(int portfolioId, int leaseId, byte[] bytes, string fileName, CancellationToken ct)
    {
        string storageKey;
        await using (var ms = new MemoryStream(bytes))
        {
            storageKey = await _storage.UploadAsync(ms, fileName, "application/pdf", ct);
        }

        var stored = new StoredFile
        {
            PortfolioId = portfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = bytes.Length,
            EntityType = "Lease",
            EntityId = leaseId,
            UploadedAt = DateTime.UtcNow,
        };

        try
        {
            _db.StoredFiles.Add(stored);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await _storage.DeleteAsync(storageKey, ct); } catch { /* best-effort */ }
            throw;
        }

        return stored.Id;
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
