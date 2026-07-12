using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Screening;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Screening;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScreeningService"/>
public sealed class ScreeningService : IScreeningService
{
    private readonly RentalCommandDbContext _db;
    private readonly IScreeningProvider _provider;
    private readonly IFileStorage _storage;
    private readonly IAdverseActionNoticePdfGenerator _pdf;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly ILogger<ScreeningService> _logger;
    private readonly TimeProvider _timeProvider;

    public ScreeningService(
        RentalCommandDbContext db,
        IScreeningProvider provider,
        IFileStorage storage,
        IAdverseActionNoticePdfGenerator pdf,
        IAtomicUnitOfWork atomic,
        ILogger<ScreeningService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _provider = provider;
        _storage = storage;
        _pdf = pdf;
        _atomic = atomic;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<ScreeningWorkspaceResponse?> GetWorkspaceAsync(
        int portfolioId, int applicationId, CancellationToken ct = default)
    {
        if (!await _db.RentalApplications.AsNoTracking()
                .AnyAsync(a => a.Id == applicationId && a.PortfolioId == portfolioId, ct))
            return null;

        var screenings = await _db.ApplicantScreenings
            .ForApplication(portfolioId, applicationId)
            .Select(r => new ApplicantScreeningResponse
            {
                Id = r.Id,
                ApplicationId = r.ApplicationId,
                Mode = r.Mode,
                Status = r.Status,
                ProviderDisplayName = r.ProviderDisplayName,
                ProviderReference = r.ProviderReference,
                ProviderHostedUrl = r.ProviderHostedUrl,
                ConsentConfirmed = r.ConsentConfirmed,
                InvitedAtUtc = r.InvitedAtUtc,
                ApplicantSubmittedAtUtc = r.ApplicantSubmittedAtUtc,
                CompletedAtUtc = r.CompletedAtUtc,
                FailedAtUtc = r.FailedAtUtc,
                LastStatusAtUtc = r.LastStatusAtUtc,
                Decision = r.Decision,
                ConsumerReportUsedForDecision = r.ConsumerReportUsedForDecision,
            })
            .ToListAsync(ct);

        var descriptor = _provider.Descriptor;
        return new ScreeningWorkspaceResponse
        {
            IntegratedProvider = new ScreeningProviderCapabilitiesResponse
            {
                Key = descriptor.Key,
                DisplayName = descriptor.DisplayName,
                IsConfigured = descriptor.IsConfigured,
                CreatesHostedInvitation = descriptor.Capabilities.CreatesHostedInvitation,
                SupportsStatusWebhooks = descriptor.Capabilities.SupportsStatusWebhooks,
                SuppliesAdverseActionAgency = descriptor.Capabilities.SuppliesAdverseActionAgency,
                SupportsApplicantPaidOrders = descriptor.Capabilities.SupportsApplicantPaidOrders,
                SupportsLandlordPaidOrders = descriptor.Capabilities.SupportsLandlordPaidOrders,
            },
            Screenings = screenings,
        };
    }

    public async Task<ApplicantScreeningResponse?> TrackExternalAsync(
        int portfolioId, int applicationId, int userId, TrackExternalScreeningRequest request, CancellationToken ct = default)
    {
        ValidateOperationKey(request.OperationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderDisplayName);
        if (request.Status is ApplicantScreeningStatus.AwaitingProvider)
            throw new ArgumentException("AwaitingProvider is reserved for integrated screening.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var existing = await _db.ApplicantScreenings.FirstOrDefaultAsync(
            s => s.PortfolioId == portfolioId && s.ApplicationId == applicationId && s.OperationKey == request.OperationKey, ct);
        if (existing != null)
        {
            await transaction.CommitAsync(ct);
            return ApplicantScreeningResponse.FromEntity(existing);
        }

        var application = await _db.RentalApplications.FirstOrDefaultAsync(
            a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);
        if (application == null) return null;

        var now = _timeProvider.UtcNow();
        var screening = new ApplicantScreening
        {
            PortfolioId = portfolioId,
            ApplicationId = applicationId,
            Mode = ScreeningMode.External,
            Status = request.Status,
            ProviderDisplayName = request.ProviderDisplayName.Trim(),
            ProviderReference = NullIfBlank(request.ProviderReference),
            ProviderHostedUrl = NullIfBlank(request.ProviderHostedUrl),
            CreditReportingAgencyName = NullIfBlank(request.CreditReportingAgencyName),
            CreditReportingAgencyAddress = NullIfBlank(request.CreditReportingAgencyAddress),
            CreditReportingAgencyPhone = NullIfBlank(request.CreditReportingAgencyPhone),
            OperationKey = request.OperationKey.Trim(),
            ConsentConfirmed = application.ConsentGiven,
            ConsentAtUtc = application.ConsentAtUtc,
            CompletedAtUtc = request.Status == ApplicantScreeningStatus.Completed ? now : null,
            FailedAtUtc = request.Status == ApplicantScreeningStatus.Failed ? now : null,
            LastStatusAtUtc = now,
            CreatedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ApplicantScreenings.Add(screening);
        application.Status = ApplicationStatus.UnderReview;
        application.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ApplicantScreeningResponse.FromEntity(screening);
    }

    public async Task<ApplicantScreeningResponse?> StartIntegratedAsync(
        int portfolioId, int applicationId, int userId, StartIntegratedScreeningRequest request, CancellationToken ct = default)
    {
        ValidateOperationKey(request.OperationKey);
        if (!_provider.Descriptor.IsConfigured)
            throw new ScreeningNotConfiguredException();

        // PREPARE: commit a durable AwaitingProvider intent before crossing the remote boundary.
        // A crash after this commit is recoverable: retrying the same operation key finds this row.
        ApplicantScreening screening;
        ScreeningInvitationRequest invitation;
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            var existing = await _db.ApplicantScreenings.FirstOrDefaultAsync(
                s => s.PortfolioId == portfolioId && s.ApplicationId == applicationId && s.OperationKey == request.OperationKey, ct);
            if (existing != null && existing.Status != ApplicantScreeningStatus.AwaitingProvider)
            {
                await transaction.CommitAsync(ct);
                return ApplicantScreeningResponse.FromEntity(existing);
            }

            var application = await _db.RentalApplications.FirstOrDefaultAsync(
                a => a.Id == applicationId && a.PortfolioId == portfolioId, ct);
            if (application == null) return null;
            if (!application.ConsentGiven || application.ConsentAtUtc == null)
                throw new ConsentRequiredException();
            if (string.IsNullOrWhiteSpace(application.Email))
                throw new ArgumentException("Applicant email is required for integrated screening.");

            var now = _timeProvider.UtcNow();
            screening = existing ?? new ApplicantScreening
            {
                PortfolioId = portfolioId,
                ApplicationId = applicationId,
                Mode = ScreeningMode.Integrated,
                ProviderKey = _provider.Descriptor.Key,
                ProviderDisplayName = _provider.Descriptor.DisplayName,
                OperationKey = request.OperationKey.Trim(),
                ConsentConfirmed = true,
                ConsentAtUtc = application.ConsentAtUtc,
                CreatedByUserId = userId,
                CreatedAt = now,
            };
            screening.Status = ApplicantScreeningStatus.AwaitingProvider;
            screening.LastStatusAtUtc = now;
            screening.UpdatedAt = now;
            if (existing == null) _db.ApplicantScreenings.Add(screening);
            application.Status = ApplicationStatus.UnderReview;
            application.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            invitation = new ScreeningInvitationRequest(
                applicationId, request.OperationKey.Trim(), $"{application.FirstName} {application.LastName}".Trim(),
                application.Email, application.ConsentAtUtc.Value);
        }

        // REMOTE: the same operation key is supplied on every AwaitingProvider retry, so a provider
        // adapter must idempotently return the original invitation rather than create another order.
        var providerResult = await _provider.CreateInvitationAsync(invitation, ct);

        // FINALIZE: this is intentionally a second atomic DB command, never a transaction held open
        // over HTTP. It records provider metadata and the content-free delivery receipt together.
        await using var completionTransaction = await _db.Database.BeginTransactionAsync(ct);
        var persisted = await _db.ApplicantScreenings.FirstAsync(
            s => s.Id == screening.Id && s.PortfolioId == portfolioId, ct);
        var completedAt = _timeProvider.UtcNow();
        persisted.ProviderReference = NullIfBlank(providerResult.ProviderReference);
        persisted.ProviderHostedUrl = NullIfBlank(providerResult.ProviderHostedUrl);
        persisted.CreditReportingAgencyName = NullIfBlank(providerResult.CreditReportingAgencyName);
        persisted.CreditReportingAgencyAddress = NullIfBlank(providerResult.CreditReportingAgencyAddress);
        persisted.CreditReportingAgencyPhone = NullIfBlank(providerResult.CreditReportingAgencyPhone);
        persisted.Status = providerResult.Accepted
            ? ApplicantScreeningStatus.AwaitingApplicant
            : ApplicantScreeningStatus.Failed;
        persisted.InvitedAtUtc = providerResult.Accepted ? providerResult.InvitedAtUtc ?? completedAt : null;
        persisted.FailedAtUtc = providerResult.Accepted ? null : completedAt;
        persisted.LastStatusAtUtc = completedAt;
        persisted.UpdatedAt = completedAt;
        _db.ApplicantScreeningMilestones.Add(new ApplicantScreeningMilestone
        {
            PortfolioId = portfolioId,
            ApplicantScreeningId = persisted.Id,
            Source = _provider.Descriptor.Key,
            DeliveryId = $"invitation:{request.OperationKey.Trim()}",
            EventType = providerResult.Accepted ? "invitation.accepted" : $"invitation.failed:{providerResult.ErrorCode ?? "unknown"}",
            Status = persisted.Status,
            OccurredAtUtc = completedAt,
            RecordedAtUtc = completedAt,
        });
        await _db.SaveChangesAsync(ct);
        await completionTransaction.CommitAsync(ct);
        return ApplicantScreeningResponse.FromEntity(persisted);
    }

    public async Task<ApplicantScreeningResponse?> UpdateExternalAsync(
        int portfolioId, int applicationId, int screeningId,
        UpdateExternalScreeningRequest request, CancellationToken ct = default)
    {
        ValidateOperationKey(request.OperationKey);
        if (request.Status is ApplicantScreeningStatus.Created or ApplicantScreeningStatus.AwaitingProvider)
            throw new ArgumentException("That status is not valid for an externally managed screening.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var screening = await _db.ApplicantScreenings.FirstOrDefaultAsync(
            s => s.Id == screeningId && s.PortfolioId == portfolioId
                && s.ApplicationId == applicationId && s.Mode == ScreeningMode.External, ct);
        if (screening == null) return null;

        var deliveryId = $"manual:{screeningId}:{request.OperationKey.Trim()}";
        if (await _db.ApplicantScreeningMilestones.AsNoTracking()
                .AnyAsync(m => m.Source == "manual" && m.DeliveryId == deliveryId, ct))
        {
            await transaction.CommitAsync(ct);
            return ApplicantScreeningResponse.FromEntity(screening);
        }

        var occurredAt = request.OccurredAtUtc ?? _timeProvider.UtcNow();
        screening.Status = request.Status;
        screening.ProviderReference = NullIfBlank(request.ProviderReference) ?? screening.ProviderReference;
        screening.ProviderHostedUrl = NullIfBlank(request.ProviderHostedUrl) ?? screening.ProviderHostedUrl;
        screening.ApplicantSubmittedAtUtc ??= request.Status == ApplicantScreeningStatus.InProgress ? occurredAt : null;
        screening.CompletedAtUtc = request.Status == ApplicantScreeningStatus.Completed ? occurredAt : screening.CompletedAtUtc;
        screening.FailedAtUtc = request.Status == ApplicantScreeningStatus.Failed ? occurredAt : null;
        screening.LastStatusAtUtc = occurredAt;
        screening.UpdatedAt = _timeProvider.UtcNow();
        _db.ApplicantScreeningMilestones.Add(new ApplicantScreeningMilestone
        {
            PortfolioId = portfolioId,
            ApplicantScreeningId = screening.Id,
            Source = "manual",
            DeliveryId = deliveryId,
            EventType = $"external.{request.Status.ToString().ToLowerInvariant()}",
            Status = request.Status,
            OccurredAtUtc = occurredAt,
            RecordedAtUtc = _timeProvider.UtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ApplicantScreeningResponse.FromEntity(screening);
    }

    public async Task<ApplicantScreeningResponse?> RecordDecisionAsync(
        int portfolioId, int applicationId, int screeningId, int userId,
        RecordScreeningDecisionRequest request, CancellationToken ct = default)
    {
        ValidateOperationKey(request.OperationKey);
        if (request.Decision == null)
            throw new ArgumentException("A screening decision is required.");
        if (request.ConsumerReportUsed && string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("Record the principal decision reason when a consumer report was used.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var screening = await _db.ApplicantScreenings.FirstOrDefaultAsync(
            s => s.Id == screeningId && s.PortfolioId == portfolioId && s.ApplicationId == applicationId, ct);
        if (screening == null) return null;

        var deliveryId = $"decision:{screeningId}:{request.OperationKey.Trim()}";
        if (await _db.ApplicantScreeningMilestones.AsNoTracking()
                .AnyAsync(m => m.Source == "decision" && m.DeliveryId == deliveryId, ct))
        {
            await transaction.CommitAsync(ct);
            return ApplicantScreeningResponse.FromEntity(screening);
        }

        var now = _timeProvider.UtcNow();
        screening.Decision = request.Decision.Value;
        screening.DecisionReason = NullIfBlank(request.Reason);
        screening.DecisionRecordedByUserId = userId;
        screening.DecisionRecordedAtUtc = now;
        screening.ConsumerReportUsedForDecision = request.ConsumerReportUsed;
        screening.UpdatedAt = now;
        _db.ApplicantScreeningMilestones.Add(new ApplicantScreeningMilestone
        {
            PortfolioId = portfolioId,
            ApplicantScreeningId = screening.Id,
            Source = "decision",
            DeliveryId = deliveryId,
            EventType = $"decision.{request.Decision.Value.ToString().ToLowerInvariant()}",
            Status = screening.Status,
            OccurredAtUtc = now,
            RecordedAtUtc = now,
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ApplicantScreeningResponse.FromEntity(screening);
    }

    public async Task<ApplicantScreeningResponse?> ApplyProviderDeliveryAsync(
        ScreeningProviderStatusDelivery delivery, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(delivery.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(delivery.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(delivery.ProviderReference);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var duplicate = await _db.ApplicantScreeningMilestones.AsNoTracking()
            .Where(m => m.Source == delivery.ProviderKey && m.DeliveryId == delivery.DeliveryId)
            .Select(m => m.ApplicantScreeningId)
            .FirstOrDefaultAsync(ct);
        if (duplicate != 0)
        {
            var replay = await _db.ApplicantScreenings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == duplicate, ct);
            await transaction.CommitAsync(ct);
            return replay == null ? null : ApplicantScreeningResponse.FromEntity(replay);
        }

        var screening = await _db.ApplicantScreenings.FirstOrDefaultAsync(
            s => s.Mode == ScreeningMode.Integrated
                && s.ProviderKey == delivery.ProviderKey
                && s.ProviderReference == delivery.ProviderReference, ct);
        if (screening == null) return null;

        if (delivery.OccurredAtUtc >= screening.LastStatusAtUtc)
        {
            screening.Status = delivery.Status;
            screening.ProviderHostedUrl = NullIfBlank(delivery.ProviderHostedUrl) ?? screening.ProviderHostedUrl;
            screening.CreditReportingAgencyName = NullIfBlank(delivery.CreditReportingAgencyName) ?? screening.CreditReportingAgencyName;
            screening.CreditReportingAgencyAddress = NullIfBlank(delivery.CreditReportingAgencyAddress) ?? screening.CreditReportingAgencyAddress;
            screening.CreditReportingAgencyPhone = NullIfBlank(delivery.CreditReportingAgencyPhone) ?? screening.CreditReportingAgencyPhone;
            screening.ApplicantSubmittedAtUtc ??= delivery.Status == ApplicantScreeningStatus.InProgress ? delivery.OccurredAtUtc : null;
            screening.CompletedAtUtc = delivery.Status == ApplicantScreeningStatus.Completed ? delivery.OccurredAtUtc : screening.CompletedAtUtc;
            screening.FailedAtUtc = delivery.Status == ApplicantScreeningStatus.Failed ? delivery.OccurredAtUtc : null;
            screening.LastStatusAtUtc = delivery.OccurredAtUtc;
            screening.UpdatedAt = _timeProvider.UtcNow();
        }
        _db.ApplicantScreeningMilestones.Add(new ApplicantScreeningMilestone
        {
            PortfolioId = screening.PortfolioId,
            ApplicantScreeningId = screening.Id,
            Source = delivery.ProviderKey,
            DeliveryId = delivery.DeliveryId,
            EventType = delivery.EventType,
            Status = delivery.Status,
            OccurredAtUtc = delivery.OccurredAtUtc,
            RecordedAtUtc = _timeProvider.UtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ApplicantScreeningResponse.FromEntity(screening);
    }

    private static void ValidateOperationKey(string operationKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

        var cra = await GetCreditReportingAgencyAsync(portfolioId, applicationId, ct);
        if (cra == null)
            throw new ArgumentException("Credit reporting agency contact details are required before generating an adverse-action notice.");
        var craBlock = $"{cra.Name}, {cra.Address}, {cra.Phone}";

        var now = _timeProvider.UtcNow();
        var pdfBytes = _pdf.Generate(new AdverseActionNoticeData
        {
            ManagementCompanyName = portfolio?.ManagementCompanyName,
            PortfolioName = portfolio?.Name,
            ApplicantName = $"{application.FirstName} {application.LastName}".Trim(),
            PropertyLine = PropertyLine(application.Property),
            NoticeDate = now,
            Reason = reason,
            CreditReportingAgencyName = cra.Name,
            CreditReportingAgencyAddress = cra.Address,
            CreditReportingAgencyPhone = cra.Phone,
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

    private async Task<CreditReportingAgencySnapshot?> GetCreditReportingAgencyAsync(
        int portfolioId, int applicationId, CancellationToken ct) =>
        await _db.ApplicantScreenings.AsNoTracking()
            .Where(s => s.PortfolioId == portfolioId && s.ApplicationId == applicationId
                && s.Status == ApplicantScreeningStatus.Completed
                && s.CreditReportingAgencyName != null
                && s.CreditReportingAgencyAddress != null
                && s.CreditReportingAgencyPhone != null)
            .OrderByDescending(s => s.CompletedAtUtc)
            .Select(s => new CreditReportingAgencySnapshot(
                s.CreditReportingAgencyName!, s.CreditReportingAgencyAddress!, s.CreditReportingAgencyPhone!))
            .FirstOrDefaultAsync(ct);

    /// <summary>Uses only the landlord's recorded decision reason, never restricted report content.</summary>
    private async Task<string> DeriveScreeningReasonAsync(int portfolioId, int applicationId, CancellationToken ct)
    {
        var latest = await _db.ApplicantScreenings
            .AsNoTracking()
            .Where(r => r.PortfolioId == portfolioId && r.ApplicationId == applicationId
                && r.Status == ApplicantScreeningStatus.Completed)
            .OrderByDescending(r => r.CompletedAtUtc)
            .Select(r => r.DecisionReason)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(latest)
            ? "Information contained in a consumer report obtained from the consumer reporting agency named below."
            : latest;
    }

    private sealed record CreditReportingAgencySnapshot(string Name, string Address, string Phone);

    private static string? PropertyLine(Property? property) =>
        property == null
            ? null
            : $"{property.Name} — {property.AddressLine1}, {property.City}, {property.State} {property.PostalCode}".Trim(' ', '—');
}
