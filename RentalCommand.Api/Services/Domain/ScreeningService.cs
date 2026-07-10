using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Screening;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScreeningService"/>
public sealed class ScreeningService : IScreeningService
{
    private const string ScreeningEntityType = "ScreeningResult";

    private readonly RentalCommandDbContext _db;
    private readonly IScreeningProvider _provider;
    private readonly ScreeningConfig _config;
    private readonly IFileStorage _storage;
    private readonly IAdverseActionNoticePdfGenerator _pdf;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<ScreeningService> _logger;
    private readonly TimeProvider _timeProvider;

    public ScreeningService(
        RentalCommandDbContext db,
        IScreeningProvider provider,
        IOptions<ScreeningConfig> config,
        IFileStorage storage,
        IAdverseActionNoticePdfGenerator pdf,
        IAtomicUnitOfWork atomic,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        ILogger<ScreeningService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _provider = provider;
        _config = config.Value;
        _storage = storage;
        _pdf = pdf;
        _atomic = atomic;
        _dataUpdate = dataUpdate;
        _audit = audit;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<ScreeningResultResponse?> RequestScreeningAsync(
        int portfolioId, int applicationId, int userId, CancellationToken ct = default)
    {
        var application = await _db.RentalApplications
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);
        if (application == null)
            return null;

        // FCRA control: NEVER run a screening without recorded consent.
        if (!application.ConsentGiven)
            throw new ConsentRequiredException();

        // Gated provider: when no key is configured, do not invent a result — surface "not configured".
        if (!_provider.IsConfigured)
            throw new ScreeningNotConfiguredException();

        var providerResult = await _provider.RequestScreeningAsync(new ScreeningRequest
        {
            ApplicationId = application.Id,
            FullName = $"{application.FirstName} {application.LastName}".Trim(),
            Email = application.Email ?? string.Empty,
            Address = application.CurrentAddress,
        }, ct);

        // Defensive: a provider that flips to unconfigured mid-call must never produce a stored "passed".
        if (!providerResult.IsConfigured)
            throw new ScreeningNotConfiguredException();

        var now = _timeProvider.UtcNow();
        var result = new ScreeningResult
        {
            PortfolioId = portfolioId,
            ApplicationId = application.Id,
            Status = providerResult.Completed ? ScreeningStatus.Completed : ScreeningStatus.Failed,
            CreditScoreBand = providerResult.CreditScoreBand,
            HasCriminalRecord = providerResult.HasCriminalRecord,
            HasEvictionRecord = providerResult.HasEvictionRecord,
            Recommendation = providerResult.Recommendation,
            ProviderReference = providerResult.ProviderReference,
            RawResultJson = providerResult.RawResultJson,
            RequestedAtUtc = now,
            CompletedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ScreeningResults.Add(result);

        // Screening moves the application into review.
        application.Status = ApplicationStatus.UnderReview;
        application.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            ScreeningEntityType,
            result.Id,
            AuditLogOperation.Created,
            userId: userId,
            changeReason: $"Screening requested for application #{application.Id} (status {result.Status}).",
            ct: ct);

        var appResponse = ApplicationResponse.FromEntity(application);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, "RentalApplication", application.Id, appResponse, ct);

        return ScreeningResultResponse.FromEntity(result);
    }

    public async Task<IReadOnlyList<ScreeningResultResponse>?> GetScreeningResultsAsync(
        int portfolioId, int applicationId, CancellationToken ct = default)
    {
        var exists = await _db.RentalApplications
            .AsNoTracking()
            .AnyAsync(a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);
        if (!exists)
            return null;

        var results = await _db.ScreeningResults
            .AsNoTracking()
            .Where(r => r.PortfolioId == portfolioId && r.ApplicationId == applicationId)
            .OrderByDescending(r => r.RequestedAtUtc)
            .ToListAsync(ct);

        return results.Select(ScreeningResultResponse.FromEntity).ToList();
    }

    public async Task<AdverseActionNoticeResponse?> GenerateAdverseActionAsync(
        int portfolioId, int applicationId, int userId, GenerateAdverseActionRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationKey);

        var application = await _db.RentalApplications
            .Include(a => a.Property)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);
        if (application == null)
            return null;

        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .Where(p => p.Id == portfolioId)
            .Select(p => new { p.Name, p.ManagementCompanyName })
            .FirstOrDefaultAsync(ct);

        // Reason precedence: explicit override → application decision reason → screening-derived reason.
        var reason = !string.IsNullOrWhiteSpace(request.Reason)
            ? request.Reason!.Trim()
            : !string.IsNullOrWhiteSpace(application.DecisionReason)
                ? application.DecisionReason!.Trim()
                : await DeriveScreeningReasonAsync(portfolioId, applicationId, ct);

        var craBlock = $"{_config.CreditReportingAgencyName}, {_config.CreditReportingAgencyAddress}, {_config.CreditReportingAgencyPhone}";

        var now = _timeProvider.UtcNow();
        var pdfBytes = _pdf.Generate(new AdverseActionNoticeData
        {
            ManagementCompanyName = portfolio?.ManagementCompanyName,
            PortfolioName = portfolio?.Name,
            ApplicantName = $"{application.FirstName} {application.LastName}".Trim(),
            PropertyLine = PropertyLine(application.Property),
            NoticeDate = now,
            Reason = reason,
            CreditReportingAgencyName = _config.CreditReportingAgencyName,
            CreditReportingAgencyAddress = _config.CreditReportingAgencyAddress,
            CreditReportingAgencyPhone = _config.CreditReportingAgencyPhone,
        });

        // Store the PDF as a StoredFile attached to the application.
        var fileName = $"adverse-action-application-{application.Id}.pdf";
        string storageKey;
        await using (var ms = new MemoryStream(pdfBytes))
        {
            storageKey = await _storage.UploadAsync(ms, fileName, "application/pdf", ct);
        }

        AtomicCommandOutcome<CreateAdverseActionNoticeResult> outcome;
        var scopedOperationKey = ScopedOperationKey(portfolioId, applicationId, request.OperationKey);
        try
        {
            outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "adverse-action.generate",
                    scopedOperationKey),
                new CreateAdverseActionNoticeCommand(
                    portfolioId,
                    application.Id,
                    userId,
                    reason,
                    craBlock,
                    fileName,
                    storageKey,
                    pdfBytes.LongLength,
                    request.SendToApplicant,
                    $"{scopedOperationKey}:email",
                    now),
                new AtomicJsonResultCodec<CreateAdverseActionNoticeResult>("adverse-action.generate.v1"),
                ct);
        }
        catch
        {
            await DeleteFailedUploadIfUnreferencedAsync(storageKey);
            throw;
        }

        if (outcome.Disposition == AtomicCommandDisposition.Replayed)
        {
            await DeleteReplayUploadIfUnreferencedAsync(outcome.Value.StoredFileId, storageKey);
        }

        return new AdverseActionNoticeResponse
        {
            Id = outcome.Value.NoticeId,
            ApplicationId = outcome.Value.ApplicationId,
            Reason = outcome.Value.Reason,
            CreditReportingAgency = outcome.Value.CreditReportingAgency,
            GeneratedAtUtc = outcome.Value.GeneratedAtUtc,
            StoredFileId = outcome.Value.StoredFileId,
            SentAtUtc = outcome.Value.SentAtUtc,
        };
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
                    "Could not verify adverse-action stored file {StoredFileId}; preserving replay upload {StorageKey}.",
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
            _logger.LogWarning(
                ex,
                "Could not verify whether adverse-action replay upload {StorageKey} is referenced; preserving it.",
                currentStorageKey);
        }
    }

    private async Task DeleteFailedUploadIfUnreferencedAsync(string currentStorageKey)
    {
        try
        {
            var referenced = await _db.StoredFiles
                .AsNoTracking()
                .AnyAsync(file => file.FilePath == currentStorageKey, CancellationToken.None);
            if (!referenced)
            {
                await DeleteUploadedBlobAsync(currentStorageKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not verify failed adverse-action upload {StorageKey}; preserving it for reconciliation.",
                currentStorageKey);
        }
    }

    private async Task DeleteUploadedBlobAsync(string storageKey)
    {
        try
        {
            await _storage.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove uncommitted adverse-action blob {StorageKey}.", storageKey);
        }
    }

    private static string ScopedOperationKey(int portfolioId, int applicationId, string operationKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationKey.Trim())))
            .ToLowerInvariant();
        return $"adverse-action:{portfolioId}:{applicationId}:{hash}";
    }

    /// <summary>Builds a human reason from the most recent completed screening when no explicit reason exists.</summary>
    private async Task<string> DeriveScreeningReasonAsync(int portfolioId, int applicationId, CancellationToken ct)
    {
        var latest = await _db.ScreeningResults
            .AsNoTracking()
            .Where(r => r.PortfolioId == portfolioId && r.ApplicationId == applicationId && r.Status == ScreeningStatus.Completed)
            .OrderByDescending(r => r.RequestedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (latest == null)
            return "Information contained in a consumer report obtained from the consumer reporting agency named below.";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(latest.CreditScoreBand))
            parts.Add($"credit history ({latest.CreditScoreBand})");
        if (latest.HasCriminalRecord == true)
            parts.Add("information in your criminal background check");
        if (latest.HasEvictionRecord == true)
            parts.Add("information in your eviction history");

        return parts.Count == 0
            ? "Information contained in a consumer report obtained from the consumer reporting agency named below."
            : "Our decision was based in whole or in part on the following: " + string.Join("; ", parts) + ".";
    }

    private static string? PropertyLine(Property? property) =>
        property == null
            ? null
            : $"{property.Name} — {property.AddressLine1}, {property.City}, {property.State} {property.PostalCode}".Trim(' ', '—');
}
