using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// Native, ESIGN/UETA-compliant <see cref="IEsignProvider"/>. Unlike the gated Dropbox Sign provider,
/// this one is ALWAYS configured (<see cref="IsConfigured"/> is true) — out of the box, e-sign works.
/// <para>
/// <see cref="SendForSignatureAsync"/> creates a <see cref="SignatureRequest"/> (the envelope) plus one
/// <see cref="SignatureSigner"/> per recipient, each with a fresh opaque single-use token + expiry,
/// stores the original document as a <see cref="StoredFile"/>, writes a "Sent" audit event, enqueues a
/// signing-link email per signer via the DB outbox, and returns the envelope id (the request's PublicId).
/// </para>
/// <para>
/// The actual signing/decline happens on the public <c>/api/v1/sign/{token}</c> endpoints; this provider
/// only maps status (<see cref="GetStatusAsync"/>) and streams the final executed PDF
/// (<see cref="DownloadSignedDocumentAsync"/>) so the rest of the lease e-sign machinery is unchanged.
/// </para>
/// </summary>
public sealed class NativeEsignProvider : IEsignProvider
{
    /// <summary>How long a signing link stays valid before it expires.</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(14);

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IFileStorage _storage;
    private readonly string _webBaseUrl;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NativeEsignProvider> _logger;
    private readonly IPendingFileUploadStore _pendingUploads;

    public NativeEsignProvider(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IPendingFileUploadStore pendingUploads,
        ILogger<NativeEsignProvider> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _webBaseUrl = NormalizeWebBaseUrl(configuration["App:WebBaseUrl"]);
        _timeProvider = timeProvider;
        _pendingUploads = pendingUploads;
        _logger = logger;
    }

    // Native signing is always available — no third-party key required.
    public bool IsConfigured => true;

    public async Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
    {
        if (request.Signers.Count == 0)
        {
            return new EsignResult { Status = "Error", Error = "At least one signer is required." };
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return new EsignResult { Status = "Error", Error = "An idempotency key is required." };
        }

        // Resolve the lease (and thus the portfolio scope) from the document name embedded by the lease
        // e-sign service: "lease-{id}-agreement.pdf". The envelope is portfolio-scoped via the lease.
        var leaseId = TryParseLeaseId(request.DocumentName);
        if (leaseId is null)
        {
            return new EsignResult { Status = "Error", Error = "Could not resolve the lease for this document." };
        }

        var lease = await _db.Leases.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leaseId.Value, ct);
        if (lease is null)
        {
            return new EsignResult { Status = "Error", Error = "Lease not found for this document." };
        }

        var now = _timeProvider.UtcNow();
        // The signing LINK's expiry stays on the REAL clock (never the simulation clock) so a
        // time-travelling dev session can't wrongly expire — or revive — a real tenant's signing link.
        var linkExpiresAtUtc = DateTime.UtcNow.Add(TokenLifetime);

        var requestFingerprint = RequestFingerprint(lease.PortfolioId, lease.Id, request);
        PendingFileUploadAdmission admission;
        try
        {
            admission = await _pendingUploads.PrepareAsync(
                lease.PortfolioId,
                actorScopeId: 0,
                purpose: "native-esign-source",
                clientOperationId: $"{lease.Id}:{request.IdempotencyKey}",
                requestFingerprint,
                request.DocumentName,
                "application/pdf",
                request.DocumentBytes.LongLength,
                now,
                ct);
        }
        catch (UploadOperationConflictException ex)
        {
            return new EsignResult { Status = "Error", Error = ex.Message };
        }

        if (admission.State == PendingFileUploadState.Finalized && admission.StoredFileId.HasValue)
        {
            var replayPublicId = await _db.SignatureRequests.AsNoTracking()
                .Where(candidate => candidate.OriginalStoredFileId == admission.StoredFileId.Value)
                .Select(candidate => candidate.PublicId)
                .SingleOrDefaultAsync(ct);
            if (replayPublicId is not null)
            {
                return EsignResult.Sent(replayPublicId, "Sent", leaseStateCommitted: true);
            }
        }

        var publicId = Guid.NewGuid().ToString("N");
        var signers = request.Signers.Select(signer => new NativeEsignSignerCommand(
            signer.Name,
            signer.Email,
            GenerateToken())).ToArray();
        await using (var stream = new MemoryStream(request.DocumentBytes))
        {
            await _storage.UploadAtAsync(
                stream,
                admission.StoragePath,
                request.DocumentName,
                "application/pdf",
                ct);
        }

        AtomicCommandOutcome<CreateNativeEsignRequestResult> outcome;
        try
        {
            outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "native-esign.send",
                    ScopedIdempotencyKey(lease.PortfolioId, lease.Id, request.IdempotencyKey)),
                new CreateNativeEsignRequestCommand(
                    admission.Id,
                    requestFingerprint,
                    lease.PortfolioId,
                    lease.Id,
                    publicId,
                    request.DocumentName,
                    request.Subject,
                    admission.StoragePath,
                    request.DocumentBytes.LongLength,
                    request.DocumentTemplateId,
                    request.DocumentTemplateVersion,
                    request.TemplateFieldSnapshotJson,
                    _webBaseUrl,
                    now,
                    linkExpiresAtUtc,
                    signers),
                new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"),
                ct);
        }
        catch
        {
            // The admission owns the deterministic blob key. A later retry can finish the same
            // operation and the stale-upload scavenger can remove it if the caller never returns.
            throw;
        }

        _logger.LogInformation(
            "Native e-sign request {PublicId} created for lease {LeaseId} with {Count} signer(s).",
            outcome.Value.PublicId, lease.Id, signers.Length);

        return EsignResult.Sent(outcome.Value.PublicId, "Sent", leaseStateCommitted: true);
    }

    public async Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
    {
        var req = await _db.SignatureRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PublicId == envelopeId, ct);
        if (req is null)
        {
            return new EsignResult { EnvelopeId = envelopeId, Status = "Error", Error = "Unknown envelope." };
        }

        return new EsignResult { EnvelopeId = envelopeId, Status = MapStatus(req.Status) };
    }

    public async Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
    {
        var req = await _db.SignatureRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PublicId == envelopeId, ct);
        if (req?.SignedStoredFileId is null)
        {
            return null;
        }

        var file = await _db.StoredFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SignedStoredFileId.Value && f.DeletedAt == null, ct);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native e-sign: signed file blob missing for envelope {EnvelopeId}.", envelopeId);
            return null;
        }
    }

    /// <summary>Maps a request status onto the coarse vocabulary the lease e-sign service understands.</summary>
    public static string MapStatus(SignatureRequestStatus status) => status switch
    {
        SignatureRequestStatus.Completed => "Completed",
        SignatureRequestStatus.Declined => "Declined",
        SignatureRequestStatus.Voided => "Declined",
        _ => "Sent",
    };

    private static int? TryParseLeaseId(string documentName)
    {
        // "lease-{id}-agreement.pdf" or "lease-{id}-signed.pdf"
        var parts = documentName.Split('-');
        if (parts.Length >= 2 && parts[0].Equals("lease", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[1], out var id) && id > 0)
        {
            return id;
        }
        return null;
    }

    private static string GenerateToken()
    {
        // 32 random bytes → ~43-char URL-safe string, comfortably within the 64-char column.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string NormalizeWebBaseUrl(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "https://localhost:5667" : value.Trim();
        return normalized.TrimEnd('/');
    }

    private static string ScopedIdempotencyKey(int portfolioId, int leaseId, string callerKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{portfolioId}:{leaseId}:{callerKey}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string RequestFingerprint(int portfolioId, int leaseId, EsignRequest request)
    {
        var signerFingerprint = string.Join("\n", request.Signers.Select(signer =>
            $"{signer.Name.Trim()}\u001f{signer.Email.Trim().ToLowerInvariant()}"));
        var contentHash = Convert.ToHexString(SHA256.HashData(request.DocumentBytes)).ToLowerInvariant();
        var canonical = string.Join("\u001e", new[]
        {
            portfolioId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            leaseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.DocumentName,
            request.Subject ?? string.Empty,
            contentHash,
            request.DocumentBytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.DocumentTemplateId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            request.DocumentTemplateVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            request.TemplateFieldSnapshotJson ?? string.Empty,
            signerFingerprint,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
