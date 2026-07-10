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

    public NativeEsignProvider(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<NativeEsignProvider> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _webBaseUrl = NormalizeWebBaseUrl(configuration["App:WebBaseUrl"]);
        _timeProvider = timeProvider;
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

        var publicId = Guid.NewGuid().ToString("N");
        var signers = request.Signers.Select(signer => new NativeEsignSignerCommand(
            signer.Name,
            signer.Email,
            GenerateToken())).ToArray();
        string storageKey;
        await using (var stream = new MemoryStream(request.DocumentBytes))
        {
            storageKey = await _storage.UploadAsync(
                stream,
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
                    lease.PortfolioId,
                    lease.Id,
                    publicId,
                    request.DocumentName,
                    request.Subject,
                    storageKey,
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
            await DeleteFailedUploadIfUnreferencedAsync(publicId, storageKey);
            throw;
        }

        if (outcome.Disposition == AtomicCommandDisposition.Replayed)
        {
            // A later HTTP retry uploads a duplicate blob and must remove it. An execution-strategy
            // retry after an unknown commit reuses this invocation's original key, which is already
            // referenced by the committed StoredFile and must be preserved.
            await DeleteReplayUploadIfUnreferencedAsync(outcome.Value.OriginalStoredFileId, storageKey);
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

    private async Task DeleteUploadedBlobAsync(string storageKey)
    {
        try
        {
            await _storage.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove uncommitted native e-sign blob {StorageKey}.", storageKey);
        }
    }

    private async Task DeleteReplayUploadIfUnreferencedAsync(int storedFileId, string currentStorageKey)
    {
        try
        {
            var committedStorageKey = await _db.StoredFiles
                .AsNoTracking()
                .Where(file => file.Id == storedFileId)
                .Select(file => file.FilePath)
                .SingleOrDefaultAsync(CancellationToken.None);
            if (committedStorageKey is null)
            {
                _logger.LogWarning(
                    "Could not verify the committed stored file {StoredFileId}; preserving replay upload {StorageKey}.",
                    storedFileId,
                    currentStorageKey);
                return;
            }

            if (!string.Equals(committedStorageKey, currentStorageKey, StringComparison.Ordinal))
            {
                await DeleteUploadedBlobAsync(currentStorageKey);
            }
        }
        catch (Exception ex)
        {
            // Data preservation wins if the receipt's file reference cannot be verified.
            _logger.LogWarning(
                ex,
                "Could not verify whether replay upload {StorageKey} is referenced; preserving it.",
                currentStorageKey);
        }
    }

    private async Task DeleteFailedUploadIfUnreferencedAsync(string publicId, string currentStorageKey)
    {
        try
        {
            var committedStorageKey = await _db.SignatureRequests
                .AsNoTracking()
                .Where(request => request.PublicId == publicId)
                .Select(request => request.OriginalStoredFile!.FilePath)
                .SingleOrDefaultAsync(CancellationToken.None);
            if (committedStorageKey is null)
            {
                await DeleteUploadedBlobAsync(currentStorageKey);
            }
            else if (!string.Equals(committedStorageKey, currentStorageKey, StringComparison.Ordinal))
            {
                await DeleteUploadedBlobAsync(currentStorageKey);
            }
        }
        catch (Exception ex)
        {
            // A database outage makes commit state unknowable. Preserve the blob rather than risk
            // deleting the immutable document referenced by a transaction that actually committed.
            _logger.LogWarning(
                ex,
                "Could not verify failed native e-sign upload {StorageKey}; preserving it for reconciliation.",
                currentStorageKey);
        }
    }

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
}
