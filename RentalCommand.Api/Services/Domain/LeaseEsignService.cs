using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ILeaseEsignService"/>
public sealed class LeaseEsignService : ILeaseEsignService
{
    private const string EntityType = "Lease";
    private const int SignatureQueueLimit = 5;

    private readonly RentalCommandDbContext _db;
    private readonly ILeaseService _leaseService;
    private readonly IEsignProvider _provider;
    private readonly IFileStorage _storage;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly string _webBaseUrl;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LeaseEsignService> _logger;

    public LeaseEsignService(
        RentalCommandDbContext db,
        ILeaseService leaseService,
        IEsignProvider provider,
        IFileStorage storage,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<LeaseEsignService> logger)
    {
        _db = db;
        _leaseService = leaseService;
        _provider = provider;
        _storage = storage;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _webBaseUrl = NormalizeWebBaseUrl(configuration["App:WebBaseUrl"]);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SendForSignatureResult> SendForSignatureAsync(
        int portfolioId, int leaseId, SendForSignatureRequest request, int? changedByUserId, string? ipAddress, CancellationToken ct = default)
    {
        var lease = await _db.Leases
            .FirstOrDefaultAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
        if (lease is null)
        {
            return SendForSignatureResult.NotFound();
        }

        var isSignableLifecycleState =
            lease.Status == LeaseStatus.Draft
            || lease.Status == LeaseStatus.PendingSignature
            || lease.Status == LeaseStatus.Active;

        // Active unsigned leases can still be sent for tenant signature, but closed or notice-given
        // leases must not be reopened or have inconsistent move-out state left behind.
        if (lease.EsignStatus == EsignStatus.Signed
            || lease.SignedDocumentStoredFileId.HasValue
            || !isSignableLifecycleState)
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

        // Resolve tenant signers from the lease itself. Landlords recognize the property/unit/tenant,
        // not an internal envelope recipient override, so send/resend always follows the lease tenants.
        var tenantSigners = await ResolveTenantSignersAsync(portfolioId, lease.Id, ct);
        if (tenantSigners.Count == 0)
        {
            return SendForSignatureResult.MissingSigner(
                "Each tenant on the lease needs a name and email before sending a lease for signature.");
        }

        // Render the agreement at send time so send/resend uses the current lease-template selection,
        // not an older generated agreement file that may predate the landlord's template.
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
            IdempotencyKey = request.IdempotencyKey,
            DocumentName = $"lease-{lease.Id}-agreement.pdf",
            Subject = string.IsNullOrWhiteSpace(lease.LeaseNumber) ? $"Lease #{lease.Id}" : $"Lease {lease.LeaseNumber}",
            DocumentBytes = pdf,
            Signers = tenantSigners,
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

        if (providerResult.LeaseStateCommitted)
        {
            await _db.Entry(lease).ReloadAsync(ct);
        }
        else
        {
            lease.EsignEnvelopeId = providerResult.EnvelopeId;
            lease.EsignStatus = EsignStatus.Sent;
            if (lease.Status != LeaseStatus.Active)
            {
                lease.Status = LeaseStatus.PendingSignature;
            }
            lease.UpdatedAt = _timeProvider.UtcNow();
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
                changeReason: $"Lease #{lease.Id} sent to {FormatSignerEmails(tenantSigners)} for electronic signature.",
                ipAddress: ipAddress,
                ct: ct));
        }

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

    public async Task<LeaseSignatureQueueResponse?> GetSignatureQueueAsync(int portfolioId, int leaseId, CancellationToken ct = default)
    {
        var currentEnvelopeId = await _db.Leases
            .AsNoTracking()
            .Where(l => l.Id == leaseId && l.PortfolioId == portfolioId)
            .Select(l => l.EsignEnvelopeId)
            .FirstOrDefaultAsync(ct);
        if (currentEnvelopeId is null)
        {
            var exists = await _db.Leases
                .AsNoTracking()
                .AnyAsync(l => l.Id == leaseId && l.PortfolioId == portfolioId, ct);
            if (!exists)
            {
                return null;
            }
        }

        var currentSignatureRequestId = string.IsNullOrWhiteSpace(currentEnvelopeId)
            ? null
            : await _db.SignatureRequests
                .AsNoTracking()
                .Where(r => r.PortfolioId == portfolioId
                    && r.LeaseId == leaseId
                    && r.PublicId == currentEnvelopeId)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync(ct);

        var messages = await QueryLeaseSignatureQueueMessagesAsync(portfolioId, leaseId, currentSignatureRequestId, ct);
        var signingUrls = await QueryActiveSigningUrlsAsync(portfolioId, messages, ct);
        var tenantLinks = await QueryLeaseTenantLinksAsync(portfolioId, leaseId, messages, ct);
        return new LeaseSignatureQueueResponse
        {
            LeaseId = leaseId,
            Items = messages.Select(message => ToQueueItem(message, signingUrls, tenantLinks)).ToList(),
        };
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
        lease.UpdatedAt = _timeProvider.UtcNow();
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
        lease.UpdatedAt = _timeProvider.UtcNow();
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
    /// Renders and returns the latest agreement PDF bytes for the lease.
    /// Null only when the lease is not in scope.
    /// </summary>
    private async Task<byte[]?> GetOrGenerateAgreementBytesAsync(int portfolioId, int leaseId, CancellationToken ct)
    {
        var generated = await _leaseService.GenerateDocumentAsync(portfolioId, leaseId, ct);
        if (generated is null)
        {
            return null;
        }

        var existing = await _leaseService.GetDocumentAsync(portfolioId, leaseId, ct);
        if (existing is null)
        {
            return null;
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
            UploadedAt = _timeProvider.UtcNow(),
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

    private async Task<IReadOnlyList<EsignSigner>> ResolveTenantSignersAsync(
        int portfolioId,
        int leaseId,
        CancellationToken ct)
    {
        var membershipSigners = await _db.LeaseTenants
            .AsNoTracking()
            .Where(lt => lt.PortfolioId == portfolioId && lt.LeaseId == leaseId)
            .OrderByDescending(lt => lt.IsPrimary)
            .ThenBy(lt => lt.Id)
            .Select(lt => new
            {
                lt.Tenant!.FirstName,
                lt.Tenant.LastName,
                lt.Tenant.Email,
            })
            .ToListAsync(ct);

        if (membershipSigners.Count > 0)
        {
            return ToTenantSigners(membershipSigners.Select(s => (s.FirstName, s.LastName, s.Email)));
        }

        var legacySigner = await _db.Leases
            .AsNoTracking()
            .Where(l => l.Id == leaseId && l.PortfolioId == portfolioId)
            .Select(l => new
            {
                l.Tenant!.FirstName,
                l.Tenant.LastName,
                l.Tenant.Email,
            })
            .FirstOrDefaultAsync(ct);

        return legacySigner is null
            ? []
            : ToTenantSigners([(legacySigner.FirstName, legacySigner.LastName, legacySigner.Email)]);
    }

    private static IReadOnlyList<EsignSigner> ToTenantSigners(IEnumerable<(string FirstName, string LastName, string? Email)> candidates)
    {
        var signers = new List<EsignSigner>();
        foreach (var candidate in candidates)
        {
            var signer = ToTenantSigner(candidate.FirstName, candidate.LastName, candidate.Email);
            if (signer is null)
            {
                return [];
            }

            signers.Add(signer);
        }

        return signers;
    }

    private static EsignSigner? ToTenantSigner(string? firstName, string? lastName, string? email)
    {
        var trimmedName = $"{firstName} {lastName}".Trim();
        var trimmedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || string.IsNullOrWhiteSpace(trimmedEmail))
        {
            return null;
        }

        return new EsignSigner { Name = trimmedName, Email = trimmedEmail };
    }

    private static string FormatSignerEmails(IReadOnlyList<EsignSigner> signers)
        => string.Join(", ", signers.Select(s => s.Email));

    private async Task<IReadOnlyList<OutboxMessage>> QueryLeaseSignatureQueueMessagesAsync(
        int portfolioId,
        int leaseId,
        int? signatureRequestId,
        CancellationToken ct)
    {
        var source = OutboxPayloadSources.LeaseEsignSigningLink;
        var leaseIdText = leaseId.ToString(CultureInfo.InvariantCulture);
        var signatureRequestIdText = signatureRequestId?.ToString(CultureInfo.InvariantCulture);

        if (_db.Database.IsNpgsql())
        {
            // When there is no current signature request (lease never sent for e-signature),
            // omit the signatureRequestId clause entirely. Interpolating a C# null here would bind
            // a typeless NULL parameter and Postgres throws "could not determine data type of
            // parameter" — a 500 on every un-sent lease. Omitting it returns all of the lease's
            // signing emails, exactly the original `({param} IS NULL OR …)` intent when null.
            var npgsqlQuery = signatureRequestIdText is null
                ? _db.OutboxMessages.FromSqlInterpolated($"""
                    SELECT *
                    FROM "OutboxMessages"
                    WHERE "PortfolioId" = {portfolioId}
                      AND "MessageType" = 'email'
                      AND "Payload" ->> 'source' = {source}
                      AND "Payload" ->> 'leaseId' = {leaseIdText}
                    ORDER BY "CreatedAt" DESC, "Id" DESC
                    LIMIT {SignatureQueueLimit}
                    """)
                : _db.OutboxMessages.FromSqlInterpolated($"""
                    SELECT *
                    FROM "OutboxMessages"
                    WHERE "PortfolioId" = {portfolioId}
                      AND "MessageType" = 'email'
                      AND "Payload" ->> 'source' = {source}
                      AND "Payload" ->> 'leaseId' = {leaseIdText}
                      AND "Payload" ->> 'signatureRequestId' = {signatureRequestIdText}
                    ORDER BY "CreatedAt" DESC, "Id" DESC
                    LIMIT {SignatureQueueLimit}
                    """);

            return await npgsqlQuery
                .AsNoTracking()
                .ToListAsync(ct);
        }

        var sourceNeedle = $"\"source\":\"{source}\"";
        var leaseNeedleWithComma = $"\"leaseId\":{leaseIdText},";
        var leaseNeedleAtEnd = $"\"leaseId\":{leaseIdText}}}";
        var query = _db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.PortfolioId == portfolioId
                && m.MessageType == "email"
                && m.Payload.Contains(sourceNeedle)
                && (m.Payload.Contains(leaseNeedleWithComma) || m.Payload.Contains(leaseNeedleAtEnd)));

        if (signatureRequestIdText is not null)
        {
            var signatureNeedleWithComma = $"\"signatureRequestId\":{signatureRequestIdText},";
            var signatureNeedleAtEnd = $"\"signatureRequestId\":{signatureRequestIdText}}}";
            query = query.Where(m => m.Payload.Contains(signatureNeedleWithComma) || m.Payload.Contains(signatureNeedleAtEnd));
        }

        return await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(SignatureQueueLimit)
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, string>> QueryActiveSigningUrlsAsync(
        int portfolioId,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken ct)
    {
        var signatureRequestIds = messages
            .Select(TryReadSignatureRequestId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (signatureRequestIds.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var now = DateTime.UtcNow;
        var signers = await _db.SignatureSigners
            .AsNoTracking()
            .Where(s => signatureRequestIds.Contains(s.SignatureRequestId)
                && s.SignatureRequest!.PortfolioId == portfolioId
                && (s.Status == SignatureSignerStatus.Pending || s.Status == SignatureSignerStatus.Viewed)
                && s.ExpiresAtUtc > now)
            .Select(s => new
            {
                s.SignatureRequestId,
                s.Email,
                s.Token,
            })
            .ToListAsync(ct);

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var signer in signers)
        {
            if (string.IsNullOrWhiteSpace(signer.Email) || string.IsNullOrWhiteSpace(signer.Token))
            {
                continue;
            }

            urls.TryAdd(SigningLookupKey(signer.SignatureRequestId, signer.Email), $"{_webBaseUrl}/sign/{Uri.EscapeDataString(signer.Token)}");
        }

        return urls;
    }

    private static LeaseSignatureQueueItemResponse ToQueueItem(
        OutboxMessage message,
        IReadOnlyDictionary<string, string> signingUrls,
        IReadOnlyDictionary<string, int> tenantLinks)
    {
        var sentAt = message.AcceptedAtUtc;
        var failedAt = message.DeadLetteredAtUtc ?? message.LastAttemptAtUtc;
        var status = ResolveQueueStatus(message);
        var recipientEmail = ReadPayloadValue(message.Payload, "to");
        var signatureRequestId = ReadPayloadValue(message.Payload, "signatureRequestId");
        var signingUrl = status == "DeliveryDisabled"
            && int.TryParse(signatureRequestId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestId)
            && signingUrls.TryGetValue(SigningLookupKey(requestId, recipientEmail), out var url)
                ? url
                : null;

        return new LeaseSignatureQueueItemResponse
        {
            Id = message.Id,
            RecipientEmail = recipientEmail,
            TenantId = tenantLinks.TryGetValue(NormalizeEmailKey(recipientEmail) ?? string.Empty, out var tenantId)
                ? tenantId
                : null,
            Subject = ReadPayloadValue(message.Payload, "subject"),
            Status = status,
            QueuedAt = message.CreatedAtUtc,
            StatusAt = sentAt ?? failedAt ?? message.CreatedAtUtc,
            SentAt = sentAt,
            FailedAt = failedAt,
            RetryCount = message.AttemptCount,
            Error = message.LastError,
            SignatureRequestId = signatureRequestId,
            SigningUrl = signingUrl,
        };
    }

    private static int? TryReadSignatureRequestId(OutboxMessage message)
        => int.TryParse(
            ReadPayloadValue(message.Payload, "signatureRequestId"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var requestId)
            ? requestId
            : null;

    private static string SigningLookupKey(int signatureRequestId, string email)
        => $"{signatureRequestId}:{email.Trim().ToUpperInvariant()}";

    private async Task<IReadOnlyDictionary<string, int>> QueryLeaseTenantLinksAsync(
        int portfolioId,
        int leaseId,
        IReadOnlyList<OutboxMessage> messages,
        CancellationToken ct)
    {
        var recipientEmails = messages
            .Select(message => NormalizeEmailKey(ReadPayloadValue(message.Payload, "to")))
            .Where(email => email is not null)
            .Select(email => email!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (recipientEmails.Length == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var tenantMatches = await _db.LeaseTenants
            .AsNoTracking()
            .Where(lt => lt.PortfolioId == portfolioId && lt.LeaseId == leaseId)
            .Select(lt => new
            {
                TenantId = (int?)lt.TenantId,
                Email = lt.Tenant!.Email,
                Rank = lt.IsPrimary ? 0 : 1,
                SortId = lt.Id,
            })
            .Concat(_db.Leases
                .AsNoTracking()
                .Where(l => l.PortfolioId == portfolioId && l.Id == leaseId)
                .Select(l => new
                {
                    TenantId = (int?)l.TenantId,
                    Email = l.Tenant!.Email,
                    Rank = 2,
                    SortId = l.Id,
                }))
            .Where(match => match.Email != null && recipientEmails.Contains(match.Email.Trim().ToUpper()))
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.SortId)
            .ToListAsync(ct);

        var links = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var match in tenantMatches)
        {
            var emailKey = NormalizeEmailKey(match.Email);
            if (emailKey is null || match.TenantId is null)
            {
                continue;
            }

            links.TryAdd(emailKey, match.TenantId.Value);
        }

        return links;
    }

    private static string? NormalizeEmailKey(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static string ResolveQueueStatus(OutboxMessage message)
    {
        if (message.AcceptedAtUtc.HasValue)
        {
            return "Sent";
        }

        if (message.DeadLetteredAtUtc.HasValue)
        {
            return message.FailureKind == RentalCommand.Core.Enums.OutboxFailureKind.ConfigurationBlocked
                ? "DeliveryDisabled"
                : "Failed";
        }

        if (message.FailureKind == RentalCommand.Core.Enums.OutboxFailureKind.Retryable)
        {
            return "Retrying";
        }

        return "Queued";
    }

    private static string ReadPayloadValue(string payload, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty(propertyName, out var value))
            {
                return string.Empty;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string NormalizeWebBaseUrl(string? webBaseUrl)
    {
        var trimmed = webBaseUrl?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? "https://localhost:5667"
            : trimmed.TrimEnd('/');
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
