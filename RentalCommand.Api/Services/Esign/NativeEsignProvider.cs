using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
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
    private readonly IFileStorage _storage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NativeEsignProvider> _logger;

    public NativeEsignProvider(
        RentalCommandDbContext db,
        IFileStorage storage,
        IConfiguration configuration,
        ILogger<NativeEsignProvider> logger)
    {
        _db = db;
        _storage = storage;
        _configuration = configuration;
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

        var now = DateTime.UtcNow;

        // Persist the original (unsigned) document so the signer can review the exact bytes that were sent.
        var originalFileId = await StoreDocumentAsync(
            lease.PortfolioId, lease.Id, request.DocumentBytes, request.DocumentName, "esign-original", ct);

        var publicId = Guid.NewGuid().ToString("N");
        var signatureRequest = new SignatureRequest
        {
            PortfolioId = lease.PortfolioId,
            PublicId = publicId,
            LeaseId = lease.Id,
            DocumentName = request.DocumentName,
            Subject = request.Subject,
            OriginalStoredFileId = originalFileId,
            Status = SignatureRequestStatus.Sent,
            CreatedAtUtc = now,
        };

        foreach (var s in request.Signers)
        {
            signatureRequest.Signers.Add(new SignatureSigner
            {
                Name = s.Name,
                Email = s.Email,
                Token = GenerateToken(),
                ExpiresAtUtc = now.Add(TokenLifetime),
                Status = SignatureSignerStatus.Pending,
            });
        }

        signatureRequest.AuditEvents.Add(new SignatureAuditEvent
        {
            Type = SignatureAuditEventType.Sent,
            AtUtc = now,
            Detail = $"Request sent to {request.Signers.Count} signer(s): {string.Join(", ", request.Signers.Select(s => s.Email))}.",
        });

        _db.SignatureRequests.Add(signatureRequest);
        await _db.SaveChangesAsync(ct);

        // Enqueue the signing-link email per signer. In sandbox the outbox dispatcher redirects the
        // email to the portfolio owner (tagged [Sandbox]) so nothing reaches a real tenant while the
        // owner can still exercise the full flow. Best-effort: a mail-queue hiccup must not roll back
        // the created request.
        foreach (var signer in signatureRequest.Signers)
        {
            await SafeAsync("enqueue signing email", () => EnqueueSigningEmailAsync(signatureRequest, signer, ct));
        }

        _logger.LogInformation(
            "Native e-sign request {PublicId} created for lease {LeaseId} with {Count} signer(s).",
            publicId, lease.Id, signatureRequest.Signers.Count);

        return EsignResult.Sent(publicId, "Sent");
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

    private async Task EnqueueSigningEmailAsync(SignatureRequest request, SignatureSigner signer, CancellationToken ct)
    {
        var webBase = _configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
        var link = $"{webBase}/sign/{signer.Token}";
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? "Please sign your lease agreement"
            : $"Please sign: {request.Subject}";

        var body = $"""
Hi {signer.Name},

You have a document ready to sign electronically: {request.Subject ?? request.DocumentName}.

Review and sign it here:
{link}

This secure link is unique to you and expires on {signer.ExpiresAtUtc:MMMM d, yyyy}. By signing you agree to
use electronic records and signatures (E-SIGN / UETA).

– Sent via Rental Command
""";

        var payload = JsonSerializer.Serialize(new { to = signer.Email, subject, body });
        _db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = request.PortfolioId,
            MessageType = "email",
            Payload = payload,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task<int> StoreDocumentAsync(
        int portfolioId, int leaseId, byte[] bytes, string fileName, string entityType, CancellationToken ct)
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
            EntityType = entityType,
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
}
