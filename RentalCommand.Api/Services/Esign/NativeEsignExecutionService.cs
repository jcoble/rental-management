using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Esign;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Esign;

/// <inheritdoc cref="INativeEsignExecutionService"/>
public sealed class NativeEsignExecutionService : INativeEsignExecutionService
{
    private static readonly TimeSpan ExecutionLease = TimeSpan.FromMinutes(10);
    private readonly string _claimOwner =
        $"{Environment.MachineName}:{Environment.ProcessId}:native-esign-api:{Guid.NewGuid():N}";

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly INativeEsignExecutionClaimStore _claims;
    private readonly IFileStorage _storage;
    private readonly IExecutedLeasePdfGenerator _executedPdf;
    private readonly ILogger<NativeEsignExecutionService> _logger;
    private readonly IPendingFileUploadStore _pendingUploads;

    public NativeEsignExecutionService(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        INativeEsignExecutionClaimStore claims,
        IFileStorage storage,
        IExecutedLeasePdfGenerator executedPdf,
        IPendingFileUploadStore pendingUploads,
        ILogger<NativeEsignExecutionService> logger)
    {
        _db = db;
        _atomic = atomic;
        _claims = claims;
        _storage = storage;
        _executedPdf = executedPdf;
        _pendingUploads = pendingUploads;
        _logger = logger;
    }

    public async Task<bool> FinalizePendingAsync(int signatureRequestId, CancellationToken ct = default)
    {
        var completed = await _db.SignatureRequests.AsNoTracking()
            .AnyAsync(request => request.Id == signatureRequestId
                && request.Status == SignatureRequestStatus.Completed
                && request.ExecutedArtifactId != null, ct);
        if (completed)
        {
            return true;
        }

        var claim = await _claims.TryClaimAsync(
            signatureRequestId, _claimOwner, ExecutionLease, ct);
        return claim is not null
            && await FinalizeClaimedAsync(claim.Id, claim.ClaimToken, ct);
    }

    public async Task<bool> FinalizeClaimedAsync(
        int signatureRequestId, Guid claimToken, CancellationToken ct = default)
    {
        var sigRequest = await _db.SignatureRequests.AsNoTracking()
            .SingleOrDefaultAsync(request => request.Id == signatureRequestId
                && request.ExecutionClaimToken == claimToken
                && request.ExecutionClaimExpiresAtUtc > DateTime.UtcNow, ct);
        if (sigRequest is null)
        {
            return false;
        }

        if (sigRequest.Status == SignatureRequestStatus.Completed && sigRequest.ExecutedArtifactId.HasValue)
        {
            return true;
        }

        if (sigRequest.Status != SignatureRequestStatus.ExecutionPending)
        {
            return false;
        }

        string? storageKey = null;
        try
        {
            var now = await _db.SignatureSigners.AsNoTracking()
                .Where(signer => signer.SignatureRequestId == sigRequest.Id)
                .MaxAsync(signer => signer.SignedAtUtc, ct)
                ?? throw new InvalidOperationException("The executed document has no signed timestamp.");
            var data = await BuildExecutedDataAsync(sigRequest, now, ct);
            if (data is null)
            {
                _logger.LogWarning(
                    "Native e-sign: could not build executed document for request {PublicId} (lease graph missing).",
                    sigRequest.PublicId);
                await _claims.ReleaseForRetryAsync(
                    signatureRequestId, claimToken, "Lease graph missing while building executed document.", ct);
                return false;
            }

            var executedBytes = _executedPdf.Generate(data, contentSha256: string.Empty);
            var sha256 = Convert.ToHexString(SHA256.HashData(executedBytes)).ToLowerInvariant();
            var fileName = sigRequest.LeaseAgreementId.HasValue
                ? $"lease-agreement-{sigRequest.LeaseAgreementId}-executed.pdf"
                : $"lease-addendum-{sigRequest.LeaseAddendumId}-executed.pdf";
            var admission = await _pendingUploads.PrepareAsync(
                sigRequest.PortfolioId,
                actorScopeId: 0,
                purpose: "native-esign-executed",
                clientOperationId: sigRequest.PublicId.ToString("N"),
                requestFingerprint: sha256,
                fileName,
                contentType: "application/pdf",
                sizeBytes: executedBytes.LongLength,
                nowUtc: DateTime.UtcNow,
                ct);
            storageKey = admission.StoragePath;
            await using (var stream = new MemoryStream(executedBytes))
            {
                await _storage.UploadAtAsync(stream, storageKey, fileName, "application/pdf", ct);
            }

            await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("native-esign.finalize", TokenIdentity(sigRequest.PublicId.ToString("N"))),
                new FinalizeNativeEsignRequestCommand(
                    admission.Id,
                    sha256,
                    sigRequest.Id,
                    sigRequest.PublicId,
                    claimToken,
                    storageKey!,
                    fileName,
                    executedBytes.LongLength,
                    sha256),
                new AtomicJsonResultCodec<FinalizeNativeEsignRequestResult>("native-esign.finalize.v1"),
                ct);

            return true;
        }
        catch (NativeEsignExecutionClaimLostException)
        {
            return false;
        }
        catch (Exception ex)
        {
            try
            {
                await _claims.ReleaseForRetryAsync(
                    signatureRequestId, claimToken, ex.Message, CancellationToken.None);
            }
            catch (Exception releaseEx)
            {
                _logger.LogWarning(
                    releaseEx,
                    "Could not release native e-sign execution claim {ClaimToken} for request {SignatureRequestId}; expiry will make it reclaimable.",
                    claimToken,
                    signatureRequestId);
            }
            throw;
        }
    }

    private async Task<ExecutedLeaseData?> BuildExecutedDataAsync(
        SignatureRequest sigRequest,
        DateTime now,
        CancellationToken ct)
    {
        var agreement = sigRequest.LeaseAgreementId.HasValue
            ? await _db.LeaseAgreements.AsNoTracking()
            .Include(candidate => candidate.LeaseManagement!).ThenInclude(relationship => relationship.Property)
            .Include(candidate => candidate.LeaseManagement!).ThenInclude(relationship => relationship.Unit)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == sigRequest.LeaseAgreementId
                    && candidate.PortfolioId == sigRequest.PortfolioId,
                ct)
            : null;
        var addendum = sigRequest.LeaseAddendumId.HasValue
            ? await _db.LeaseAddenda.AsNoTracking()
                .Include(candidate => candidate.BaseAgreement)
                .Include(candidate => candidate.LeaseManagement!).ThenInclude(relationship => relationship.Property)
                .Include(candidate => candidate.LeaseManagement!).ThenInclude(relationship => relationship.Unit)
                .SingleOrDefaultAsync(candidate => candidate.Id == sigRequest.LeaseAddendumId
                    && candidate.PortfolioId == sigRequest.PortfolioId, ct)
            : null;
        agreement ??= addendum?.BaseAgreement;
        if (agreement?.LeaseManagement?.Property is null)
        {
            // BaseAgreement and Addendum share LeaseManagement, but an AsNoTracking include does not
            // fix up the base Agreement's relationship. Use the explicitly loaded Addendum parent.
            if (addendum?.LeaseManagement?.Property is null) return null;
        }

        var portfolio = await _db.Portfolios.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == sigRequest.PortfolioId, ct);
        var landlordName = !string.IsNullOrWhiteSpace(portfolio?.ManagementCompanyName)
            ? portfolio.ManagementCompanyName
            : portfolio?.Name ?? "Landlord";
        var primarySigner = sigRequest.LeaseAgreementId.HasValue
            ? await _db.LeaseAgreementSigners.AsNoTracking()
                .Where(signer => signer.LeaseAgreementId == agreement!.Id
                    && signer.PortfolioId == agreement.PortfolioId
                    && signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                .Select(signer => new { signer.NameSnapshot, signer.EmailSnapshot })
                .FirstOrDefaultAsync(ct)
            : await _db.LeaseAddendumSigners.AsNoTracking()
                .Where(signer => signer.LeaseAddendumId == addendum!.Id
                    && signer.PortfolioId == addendum.PortfolioId
                    && signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                .Select(signer => new { signer.NameSnapshot, signer.EmailSnapshot })
                .FirstOrDefaultAsync(ct);
        var tenantName = primarySigner?.NameSnapshot ?? string.Empty;
        var relationship = addendum?.LeaseManagement ?? agreement!.LeaseManagement!;
        var property = relationship.Property!;
        var propertyAddress = string.Join(", ", new[]
            {
                property.AddressLine1,
                property.AddressLine2,
                $"{property.City}, {property.State} {property.PostalCode}".Trim(),
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var originalDocumentBytes = await TryLoadOriginalDocumentBytesAsync(sigRequest, ct);
        var signerRows = await _db.SignatureSigners.AsNoTracking()
            .Where(signer => signer.SignatureRequestId == sigRequest.Id)
            .OrderBy(signer => signer.SigningOrder)
            .ToListAsync(ct);

        // The executed renderer still accepts its historical presentation DTO. This transient object
        // is never tracked or persisted; every authoritative term comes from LeaseAgreement.
        var presentation = new Lease
        {
            LeaseNumber = addendum?.AddendumNumber ?? agreement!.AgreementNumber,
            StartDate = (addendum?.EffectiveFromOn ?? agreement!.TermStartOn).ToDateTime(TimeOnly.MinValue),
            EndDate = (addendum?.EffectiveThroughOn ?? agreement!.TermEndOn ?? agreement.TermStartOn)
                .ToDateTime(TimeOnly.MinValue),
            MonthlyRent = agreement!.BaseRentAmount,
            SecurityDeposit = agreement.SecurityDepositObligation,
            LateFeeAmount = agreement.LateFeeAmount,
            RentDueDay = agreement.RentDueDay,
        };

        return new ExecutedLeaseData
        {
            Agreement = new LeaseAgreementData
            {
                Lease = presentation,
                LandlordName = landlordName,
                TenantName = tenantName,
                PropertyName = property?.Name ?? string.Empty,
                PropertyAddress = propertyAddress,
                UnitNumber = relationship.Unit?.UnitNumber,
                State = property?.State ?? string.Empty,
                YearBuilt = property?.YearBuilt,
            },
            Signers = await BuildExecutedSignersAsync(
                sigRequest.PortfolioId, signerRows, tenantName, primarySigner?.EmailSnapshot, ct),
            LandlordName = landlordName,
            EnvelopeId = sigRequest.PublicId.ToString("D"),
            DocumentName = sigRequest.IssuedArtifact?.FileName
                ?? (addendum is null ? $"agreement-{agreement.Id}.pdf" : $"addendum-{addendum.Id}.pdf"),
            OriginalDocumentBytes = originalDocumentBytes,
            TemplateFieldSnapshotJson = null,
            CompletedAtUtc = now,
        };
    }

    private async Task<byte[]?> TryLoadOriginalDocumentBytesAsync(
        SignatureRequest sigRequest,
        CancellationToken ct)
    {
        var file = await _db.StoredFiles.AsNoTracking()
            .Where(candidate => candidate.PortfolioId == sigRequest.PortfolioId && candidate.DeletedAt == null)
            .Join(_db.LegalDocumentArtifacts.AsNoTracking().Where(artifact => artifact.Id == sigRequest.IssuedArtifactId),
                file => file.Id, artifact => artifact.StoredFileId, (file, _) => file)
            .SingleOrDefaultAsync(ct);
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

    private async Task<List<ExecutedSigner>> BuildExecutedSignersAsync(
        int portfolioId,
        IReadOnlyList<SignatureSigner> source,
        string tenantName,
        string? tenantEmail,
        CancellationToken ct)
    {
        var drawnFileIds = source.Where(signer => signer.DrawnSignatureStoredFileId.HasValue)
            .Select(signer => signer.DrawnSignatureStoredFileId!.Value).Distinct().ToArray();
        var drawnFiles = await _db.StoredFiles.AsNoTracking()
            .Where(file => file.PortfolioId == portfolioId && drawnFileIds.Contains(file.Id) && file.DeletedAt == null)
            .Select(file => new { file.Id, file.FilePath })
            .ToDictionaryAsync(file => file.Id, file => file.FilePath, ct);
        var drawnImages = new Dictionary<int, byte[]>();
        foreach (var file in drawnFiles)
        {
            try
            {
                await using var stream = await _storage.DownloadAsync(file.Value, ct);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                drawnImages[file.Key] = buffer.ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Drawn signature evidence file {file.Key} is unavailable.", exception);
            }
        }
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
            if (signer.SignatureType == SignatureSignatureType.Drawn
                && (!signer.DrawnSignatureStoredFileId.HasValue
                    || !drawnImages.ContainsKey(signer.DrawnSignatureStoredFileId.Value)))
            {
                throw new InvalidOperationException($"Drawn signature evidence for signer {signer.Id} is unavailable.");
            }
            signers.Add(new ExecutedSigner
            {
                Name = signer.NameSnapshot,
                Email = signer.EmailSnapshot,
                SignerRole = roles[index],
                SignatureType = signer.SignatureType,
                TypedName = signer.TypedName,
                DrawnSignatureImage = signer.DrawnSignatureStoredFileId is { } fileId
                    && drawnImages.TryGetValue(fileId, out var image) ? image : null,
                SignedAtUtc = signer.SignedAtUtc,
                IpAddress = signer.IpAddress,
                UserAgent = signer.UserAgent,
                ViewedAtUtc = signer.ViewedAtUtc,
                ConsentGiven = signer.ConsentGivenAtUtc.HasValue,
            });
        }

        return signers;
    }

    private static bool IsTenantSigner(SignatureSigner signer, string tenantName, string? tenantEmail)
    {
        if (!string.IsNullOrWhiteSpace(tenantEmail)
            && string.Equals(signer.EmailSnapshot.Trim(), tenantEmail.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(tenantName)
            && string.Equals(signer.NameSnapshot.Trim(), tenantName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string TokenIdentity(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

}
