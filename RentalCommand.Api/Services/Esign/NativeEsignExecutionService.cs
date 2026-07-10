using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Esign;

/// <inheritdoc cref="INativeEsignExecutionService"/>
public sealed class NativeEsignExecutionService : INativeEsignExecutionService
{
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IFileStorage _storage;
    private readonly IExecutedLeasePdfGenerator _executedPdf;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NativeEsignExecutionService> _logger;

    public NativeEsignExecutionService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IFileStorage storage,
        IExecutedLeasePdfGenerator executedPdf,
        TimeProvider timeProvider,
        ILogger<NativeEsignExecutionService> logger)
    {
        _db = db;
        _atomic = atomic;
        _storage = storage;
        _executedPdf = executedPdf;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> FinalizePendingAsync(int signatureRequestId, CancellationToken ct = default)
    {
        var sigRequest = await _db.SignatureRequests.AsNoTracking()
            .SingleOrDefaultAsync(request => request.Id == signatureRequestId, ct);
        if (sigRequest is null)
        {
            return false;
        }

        if (sigRequest.Status == SignatureRequestStatus.Completed && sigRequest.SignedStoredFileId.HasValue)
        {
            return true;
        }

        if (sigRequest.Status != SignatureRequestStatus.ExecutionPending)
        {
            return false;
        }

        var now = _timeProvider.UtcNow();
        var data = await BuildExecutedDataAsync(sigRequest, now, ct);
        if (data is null)
        {
            _logger.LogWarning(
                "Native e-sign: could not build executed document for request {PublicId} (lease graph missing).",
                sigRequest.PublicId);
            return false;
        }

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

    private async Task<ExecutedLeaseData?> BuildExecutedDataAsync(
        SignatureRequest sigRequest,
        DateTime now,
        CancellationToken ct)
    {
        var lease = await _db.Leases.AsNoTracking()
            .Include(candidate => candidate.Tenant)
            .Include(candidate => candidate.Unit)
            .Include(candidate => candidate.Property)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == sigRequest.LeaseId
                    && candidate.PortfolioId == sigRequest.PortfolioId,
                ct);
        if (lease is null)
        {
            return null;
        }

        var portfolio = await _db.Portfolios.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == sigRequest.PortfolioId, ct);
        var landlordName = !string.IsNullOrWhiteSpace(portfolio?.ManagementCompanyName)
            ? portfolio.ManagementCompanyName
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
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var originalDocumentBytes = sigRequest.DocumentTemplateId.HasValue
            ? await TryLoadOriginalDocumentBytesAsync(sigRequest, ct)
            : null;
        var signerRows = await _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.SignatureRequestId == sigRequest.Id)
            .OrderBy(signer => signer.Id)
            .ToListAsync(ct);

        return new ExecutedLeaseData
        {
            Agreement = new LeaseAgreementData
            {
                Lease = lease,
                LandlordName = landlordName,
                TenantName = tenantName,
                PropertyName = property?.Name ?? string.Empty,
                PropertyAddress = propertyAddress,
                UnitNumber = lease.Unit?.UnitNumber,
                State = property?.State ?? string.Empty,
                YearBuilt = property?.YearBuilt,
            },
            Signers = BuildExecutedSigners(signerRows, tenantName, lease.Tenant?.Email),
            LandlordName = landlordName,
            EnvelopeId = sigRequest.PublicId,
            DocumentName = sigRequest.DocumentName,
            OriginalDocumentBytes = originalDocumentBytes,
            TemplateFieldSnapshotJson = sigRequest.TemplateFieldSnapshotJson,
            CompletedAtUtc = now,
        };
    }

    private async Task<byte[]?> TryLoadOriginalDocumentBytesAsync(
        SignatureRequest sigRequest,
        CancellationToken ct)
    {
        var file = await _db.StoredFiles.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == sigRequest.OriginalStoredFileId
                && candidate.PortfolioId == sigRequest.PortfolioId
                && candidate.DeletedAt == null, ct);
        if (file is null)
        {
            return null;
        }

        try
        {
            await using var stream = await _storage.DownloadAsync(file.FilePath, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
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

        for (var index = 0; index < source.Count; index++)
        {
            var signer = source[index];
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

            roles[index] = role;
        }

        if (!tenantAssigned && source.Count > 0)
        {
            roles[0] = DocumentTemplateSignerRole.Tenant;
            if (!roles.Contains(DocumentTemplateSignerRole.Landlord) && source.Count > 1)
            {
                roles[1] = DocumentTemplateSignerRole.Landlord;
            }
        }

        for (var index = 0; index < source.Count; index++)
        {
            var signer = source[index];
            signers.Add(new ExecutedSigner
            {
                Name = signer.Name,
                Email = signer.Email,
                SignerRole = roles[index],
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

    private static string TokenIdentity(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

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
}
