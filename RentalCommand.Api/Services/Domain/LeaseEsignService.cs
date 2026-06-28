using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseEsignService"/>
public sealed class LeaseEsignService : ILeaseEsignService
{
    private const string EntityType = "Lease";

    private readonly RentalCommandDbContext _db;
    private readonly ILeaseService _leaseService;
    private readonly IEsignProvider _provider;
    private readonly IFileStorage _storage;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<LeaseEsignService> _logger;

    public LeaseEsignService(
        RentalCommandDbContext db,
        ILeaseService leaseService,
        IEsignProvider provider,
        IFileStorage storage,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        ILogger<LeaseEsignService> logger)
    {
        _db = db;
        _leaseService = leaseService;
        _provider = provider;
        _storage = storage;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _logger = logger;
    }

    public async Task<SendForSignatureResult> SendForSignatureAsync(
        int portfolioId, int leaseId, SendForSignatureRequest request, int? changedByUserId, string? ipAddress, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .Include(l => l.Tenant)
            .FirstOrDefaultAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (lease is null)
        {
            return SendForSignatureResult.NotFound();
        }

        // Precondition: only pre-execution leases may enter the e-sign send flow. Lifecycle states like
        // Active or NoticeGiven must not be silently reverted to PendingSignature — that can reopen a
        // closed/active contract or leave inconsistent move-out state behind.
        if (lease.EsignStatus == EsignStatus.Signed
            || lease.SignedDocumentStoredFileId.HasValue
            || (lease.Status != LeaseStatus.Draft && lease.Status != LeaseStatus.PendingSignature))
        {
            _logger.LogInformation(
                "Send-for-signature refused for lease {LeaseId}: not in a signable state (status {Status}, esign {Esign}, hasSignedDoc {HasDoc}).",
                leaseId, lease.Status, lease.EsignStatus, lease.SignedDocumentStoredFileId.HasValue);
            return SendForSignatureResult.AlreadyFinalized();
        }

        // Sandbox is intentionally NOT short-circuited here. A demo account must still be able to
        // exercise the full send -> sign -> executed-PDF flow. The native e-sign provider marks the
        // signing-link outbox payload as lease e-sign so the dispatcher sends it to the lease tenant
        // instead of applying the generic sandbox owner/admin redirect.

        // Gate up front: never touch lease state when the provider is not configured.
        if (!_provider.IsConfigured)
        {
            _logger.LogInformation(
                "Send-for-signature requested for lease {LeaseId} but the e-sign provider is not configured — no change made.",
                leaseId);
            return SendForSignatureResult.NotConfigured();
        }

        // Resolve the signer from the lease itself. Landlords recognize the property/unit/tenant, not
        // an internal envelope recipient override, so send/resend always follows the lease tenant.
        var signerName = $"{lease.Tenant?.FirstName} {lease.Tenant?.LastName}".Trim();
        var signerEmail = lease.Tenant?.Email?.Trim();

        if (string.IsNullOrWhiteSpace(signerName) || string.IsNullOrWhiteSpace(signerEmail))
        {
            return SendForSignatureResult.MissingSigner();
        }

        // Ensure a generated agreement PDF exists; generate one on demand if none has been created yet.
        var pdf = await GetOrGenerateAgreementBytesAsync(portfolioId, leaseId, ct);
        if (pdf is null)
        {
            // Lease vanished between checks (or generation failed for a non-existent lease).
            return SendForSignatureResult.NotFound();
        }

        await _db.Entry(lease).ReloadAsync(ct);
        var templateFieldSnapshot = lease.DocumentTemplateId.HasValue
            ? await BuildTemplateFieldSnapshotAsync(portfolioId, lease.DocumentTemplateId.Value, ct)
            : null;

        var providerResult = await _provider.SendForSignatureAsync(new EsignRequest
        {
            DocumentName = $"lease-{lease.Id}-agreement.pdf",
            Subject = string.IsNullOrWhiteSpace(lease.LeaseNumber) ? $"Lease #{lease.Id}" : $"Lease {lease.LeaseNumber}",
            DocumentBytes = pdf,
            Signers = new[] { new EsignSigner { Name = signerName, Email = signerEmail } },
            DocumentTemplateId = lease.DocumentTemplateId,
            DocumentTemplateVersion = lease.DocumentTemplateVersion,
            TemplateFieldSnapshotJson = templateFieldSnapshot,
        }, ct);

        if (!providerResult.IsConfigured)
        {
            // Defensive: provider reported not-configured even though the gate passed. Leave the lease alone.
            return SendForSignatureResult.NotConfigured();
        }

        if (string.IsNullOrWhiteSpace(providerResult.EnvelopeId))
        {
            return SendForSignatureResult.ProviderError(providerResult.Error ?? "The e-sign provider did not return an envelope id.");
        }

        lease.EsignEnvelopeId = providerResult.EnvelopeId;
        lease.EsignStatus = EsignStatus.Sent;
        lease.Status = LeaseStatus.PendingSignature;
        lease.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await SafeAsync("send audit", () => _audit.LogAsync(
            portfolioId,
            EntityType,
            lease.Id,
            AuditLogOperation.Updated,
            userId: changedByUserId,
            actorLabel: changedByUserId.HasValue ? null : "staff",
            newValues: JsonSerializer.Serialize(new
            {
                esignStatus = lease.EsignStatus.ToString(),
                leaseStatus = lease.Status.ToString(),
                envelopeId = lease.EsignEnvelopeId,
            }),
            changeReason: $"Lease #{lease.Id} sent to {signerEmail} for electronic signature.",
            ipAddress: ipAddress,
            ct: ct));

        await BroadcastLeaseAsync(portfolioId, lease.Id, ct);

        return SendForSignatureResult.Sent(ToStatus(lease));
    }

    public async Task<LeaseSignatureStatusResponse?> GetSignatureStatusAsync(int portfolioId, int leaseId, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (lease is null)
        {
            return null;
        }

        // When configured and a request is in flight, refresh the coarse status from the provider so the
        // UI reflects a signature/decline even if the webhook has not landed yet. Best-effort: a provider
        // hiccup never fails the read.
        if (_provider.IsConfigured
            && !string.IsNullOrWhiteSpace(lease.EsignEnvelopeId)
            && lease.EsignStatus == EsignStatus.Sent)
        {
            try
            {
                var remote = await _provider.GetStatusAsync(lease.EsignEnvelopeId!, ct);
                if (string.Equals(remote.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    // Reuse the same advance path the webhook uses so signing is captured even without it.
                    await HandleSignedEventAsync(lease.EsignEnvelopeId!, ct);
                    await _db.Entry(lease).ReloadAsync(ct);
                }
                else if (string.Equals(remote.Status, "Declined", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleDeclinedEventAsync(lease.EsignEnvelopeId!, ct);
                    await _db.Entry(lease).ReloadAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E-sign status refresh failed for lease {LeaseId} (returning stored state).", leaseId);
            }
        }

        return ToStatus(lease);
    }

    public async Task<(Stream Stream, string FileName, string ContentType)?> GetSignedDocumentAsync(int portfolioId, int leaseId, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (lease?.SignedDocumentStoredFileId is null)
        {
            return null;
        }

        var file = await _db.StoredFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == lease.SignedDocumentStoredFileId.Value
                && f.PortfolioId == portfolioId
                && f.DeletedAt == null, ct);
        if (file is null)
        {
            return null;
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(file.FilePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signed lease blob missing for lease {LeaseId} (file {FileId}).", leaseId, file.Id);
            return null;
        }

        return (stream, file.FileName, file.ContentType);
    }

    public async Task<bool> HandleSignedEventAsync(string envelopeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelopeId))
        {
            return false;
        }

        // The webhook is anonymous: resolve scope FROM the envelope id, never a client parameter.
        var lease = await _db.Leases
            .FirstOrDefaultAsync(l => l.EsignEnvelopeId == envelopeId, ct);
        if (lease is null)
        {
            _logger.LogWarning("E-sign signed event for unknown envelope {EnvelopeId} — ignoring.", envelopeId);
            return false;
        }

        // Idempotent: a duplicate "signed" delivery is a no-op.
        if (lease.EsignStatus == EsignStatus.Signed && lease.SignedDocumentStoredFileId.HasValue)
        {
            return true;
        }

        // Download + store the signed PDF when the provider is configured. When it is not (e.g. a test or a
        // replayed event without keys), we still advance the workflow state but without a stored document.
        if (lease.SignedDocumentStoredFileId is null && _provider.IsConfigured)
        {
            var signedBytes = await _provider.DownloadSignedDocumentAsync(envelopeId, ct);
            if (signedBytes is { Length: > 0 })
            {
                var storedFileId = await StoreSignedDocumentAsync(lease, signedBytes, ct);
                lease.SignedDocumentStoredFileId = storedFileId;
            }
            else
            {
                _logger.LogWarning("E-sign signed event for envelope {EnvelopeId}: no signed file returned by provider.", envelopeId);
            }
        }

        var wasPending = lease.Status == LeaseStatus.PendingSignature;
        lease.EsignStatus = EsignStatus.Signed;
        if (wasPending)
        {
            lease.Status = LeaseStatus.Active;
        }
        lease.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await SafeAsync("signed audit", () => _audit.LogAsync(
            lease.PortfolioId,
            EntityType,
            lease.Id,
            AuditLogOperation.Updated,
            actorLabel: "esign-webhook",
            newValues: JsonSerializer.Serialize(new
            {
                esignStatus = lease.EsignStatus.ToString(),
                leaseStatus = lease.Status.ToString(),
                envelopeId,
                signedDocumentStoredFileId = lease.SignedDocumentStoredFileId,
            }),
            changeReason: $"Lease #{lease.Id} signed electronically (envelope {envelopeId})"
                + (wasPending ? " — lease activated." : "."),
            ct: ct));

        await BroadcastLeaseAsync(lease.PortfolioId, lease.Id, ct);
        return true;
    }

    public async Task<bool> HandleDeclinedEventAsync(string envelopeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(envelopeId))
        {
            return false;
        }

        var lease = await _db.Leases
            .FirstOrDefaultAsync(l => l.EsignEnvelopeId == envelopeId, ct);
        if (lease is null)
        {
            _logger.LogWarning("E-sign declined event for unknown envelope {EnvelopeId} — ignoring.", envelopeId);
            return false;
        }

        if (lease.EsignStatus == EsignStatus.Declined)
        {
            return true;
        }

        lease.EsignStatus = EsignStatus.Declined;
        lease.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await SafeAsync("declined audit", () => _audit.LogAsync(
            lease.PortfolioId,
            EntityType,
            lease.Id,
            AuditLogOperation.Updated,
            actorLabel: "esign-webhook",
            newValues: JsonSerializer.Serialize(new { esignStatus = lease.EsignStatus.ToString(), envelopeId }),
            changeReason: $"Lease #{lease.Id} signature declined (envelope {envelopeId}).",
            ct: ct));

        await BroadcastLeaseAsync(lease.PortfolioId, lease.Id, ct);
        return true;
    }

    /// <summary>
    /// Returns the latest generated agreement PDF bytes for the lease, generating one first if none exists.
    /// Null only when the lease is not in scope.
    /// </summary>
    private async Task<byte[]?> GetOrGenerateAgreementBytesAsync(int portfolioId, int leaseId, CancellationToken ct)
    {
        var existing = await _leaseService.GetDocumentAsync(portfolioId, leaseId, ct);
        if (existing is null)
        {
            var generated = await _leaseService.GenerateDocumentAsync(portfolioId, leaseId, ct);
            if (generated is null)
            {
                return null;
            }
            existing = await _leaseService.GetDocumentAsync(portfolioId, leaseId, ct);
            if (existing is null)
            {
                return null;
            }
        }

        await using var stream = existing.Value.Stream;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Persist signed PDF bytes as a StoredFile attached to the lease (mirrors the generate-document path).</summary>
    private async Task<int> StoreSignedDocumentAsync(Lease lease, byte[] signedBytes, CancellationToken ct)
    {
        var fileName = $"lease-{lease.Id}-signed.pdf";
        string storageKey;
        await using (var ms = new MemoryStream(signedBytes))
        {
            storageKey = await _storage.UploadAsync(ms, fileName, "application/pdf", ct);
        }

        var stored = new StoredFile
        {
            PortfolioId = lease.PortfolioId,
            FileName = fileName,
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = signedBytes.Length,
            EntityType = EntityType,
            EntityId = lease.Id,
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

    private async Task<string?> BuildTemplateFieldSnapshotAsync(
        int portfolioId,
        int documentTemplateId,
        CancellationToken ct)
    {
        var fields = await _db.DocumentTemplateFields
            .AsNoTracking()
            .Where(f => f.DocumentTemplateId == documentTemplateId
                && f.DocumentTemplate!.PortfolioId == portfolioId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .Select(f => new
            {
                f.Id,
                f.FieldKey,
                f.Label,
                Kind = f.Kind.ToString(),
                SignerRole = f.SignerRole.ToString(),
                f.PageNumber,
                f.XPct,
                f.YPct,
                f.WidthPct,
                f.HeightPct,
                f.Required,
                f.Locked,
                f.SortOrder,
            })
            .ToListAsync(ct);

        return fields.Count == 0 ? null : JsonSerializer.Serialize(fields);
    }

    private async Task BroadcastLeaseAsync(int portfolioId, int leaseId, CancellationToken ct)
    {
        var response = await _leaseService.GetAsync(portfolioId, leaseId, ct);
        if (response is not null)
        {
            await SafeAsync("lease broadcast", () => _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, leaseId, response, ct));
        }
    }

    private static LeaseSignatureStatusResponse ToStatus(Lease lease) => new()
    {
        LeaseId = lease.Id,
        EsignStatus = lease.EsignStatus,
        LeaseStatus = lease.Status,
        EnvelopeId = lease.EsignEnvelopeId,
        HasSignedDocument = lease.SignedDocumentStoredFileId.HasValue,
    };

    private async Task SafeAsync(string label, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lease e-sign side effect '{Label}' failed (continuing).", label);
        }
    }
}
