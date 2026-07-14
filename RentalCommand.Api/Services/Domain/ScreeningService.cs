using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Screening;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Screening;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IScreeningService"/>
public sealed class ScreeningService : IScreeningService
{
    private static readonly AtomicJsonResultCodec<ScreeningMutationResult> MutationCodec =
        new("screening.mutation.v1");
    private static readonly AtomicJsonResultCodec<PrepareIntegratedScreeningResult> IntegratedPrepareCodec =
        new("screening.integrated.prepare.v1");
    private static readonly AtomicJsonResultCodec<PrepareAdverseActionNoticeResult> AdversePrepareCodec =
        new("adverse-action.prepare.v1");
    private static readonly AtomicJsonResultCodec<CreateAdverseActionNoticeResult> AdverseFinalizeCodec =
        new("adverse-action.finalize.v1");

    private readonly RentalCommandDbContext _db;
    private readonly IScreeningProvider _provider;
    private readonly IFileStorage _storage;
    private readonly IAdverseActionNoticePdfGenerator _pdf;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public ScreeningService(
        RentalCommandDbContext db,
        IScreeningProvider provider,
        IFileStorage storage,
        IAdverseActionNoticePdfGenerator pdf,
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider)
    {
        _db = db;
        _provider = provider;
        _storage = storage;
        _pdf = pdf;
        _atomic = atomic;
        _timeProvider = timeProvider;
    }

    public async Task<ScreeningWorkspaceResponse?> GetWorkspaceAsync(
        WorkspaceReadScope scope, int applicationId, CancellationToken ct = default)
    {
        var securityNow = _timeProvider.UtcNow();
        var applicationExists = await _db.RentalApplications.AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                [CapabilityKeys.LeasingApplicationsManage],
                securityNow)
            .AnyAsync(application => application.Id == applicationId, ct);
        if (!applicationExists)
            return null;

        var snapshots = await _db.ApplicantScreenings
            .ForAuthorizedApplication(_db, scope, applicationId, securityNow)
            .ProjectSnapshots()
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
            Screenings = snapshots.Select(Response).ToArray(),
        };
    }

    public async Task<ApplicantScreeningResponse?> TrackExternalAsync(
        WorkspaceReadScope scope,
        int applicationId,
        TrackExternalScreeningRequest request,
        CancellationToken ct = default)
    {
        var operationKey = NormalizeOperationKey(request.OperationKey);
        var digest = Digest(operationKey);
        var command = new TrackExternalScreeningCommand(
            scope.PortfolioId,
            applicationId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            operationKey,
            request.ProviderDisplayName,
            request.ProviderReference,
            request.ProviderHostedUrl,
            request.CreditReportingAgencyName,
            request.CreditReportingAgencyAddress,
            request.CreditReportingAgencyPhone,
            request.Status,
            $"screening-external-create:{digest}");
        var outcome = await _atomic.ExecuteAsync(
            Identity("screening.external.create", scope.PortfolioId, applicationId, digest),
            command,
            MutationCodec,
            ct);
        return Response(outcome.Value);
    }

    public async Task<ApplicantScreeningResponse?> StartIntegratedAsync(
        WorkspaceReadScope scope,
        int applicationId,
        StartIntegratedScreeningRequest request,
        CancellationToken ct = default)
    {
        if (!_provider.Descriptor.IsConfigured)
            throw new ScreeningNotConfiguredException();
        var operationKey = NormalizeOperationKey(request.OperationKey);
        var digest = Digest(operationKey);
        var descriptor = _provider.Descriptor;
        var prepare = await _atomic.ExecuteAsync(
            Identity("screening.integrated.prepare", scope.PortfolioId, applicationId, digest),
            new PrepareIntegratedScreeningCommand(
                scope.PortfolioId,
                applicationId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationKey,
                descriptor.Key,
                descriptor.DisplayName,
                $"screening-integrated-prepare:{digest}"),
            IntegratedPrepareCodec,
            ct);
        if (prepare.Value.Outcome == ScreeningMutationOutcome.NotFound
            || prepare.Value.Screening is null)
            return null;
        if (prepare.Value.ApplicantName is null
            || prepare.Value.ApplicantEmail is null
            || prepare.Value.ConsentAtUtc is null)
            return Response(prepare.Value.Screening);

        // No database transaction is open across the provider call. The exact client operation key
        // survives retries and is the adapter's remote idempotency key.
        var providerResult = await _provider.CreateInvitationAsync(
            new ScreeningInvitationRequest(
                prepare.Value.ApplicationId,
                prepare.Value.OperationKey,
                prepare.Value.ApplicantName,
                prepare.Value.ApplicantEmail,
                prepare.Value.ConsentAtUtc.Value),
            ct);

        var finalize = await _atomic.ExecuteAsync(
            Identity("screening.integrated.finalize", scope.PortfolioId, applicationId, digest),
            new FinalizeIntegratedScreeningCommand(
                scope.PortfolioId,
                applicationId,
                prepare.Value.Screening.Id,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationKey,
                descriptor.Key,
                providerResult.Accepted,
                providerResult.ProviderReference,
                providerResult.ProviderHostedUrl,
                providerResult.InvitedAtUtc,
                providerResult.ErrorCode,
                providerResult.CreditReportingAgencyName,
                providerResult.CreditReportingAgencyAddress,
                providerResult.CreditReportingAgencyPhone,
                $"screening-integrated-finalize:{digest}"),
            MutationCodec,
            ct);
        return Response(finalize.Value);
    }

    public async Task<ApplicantScreeningResponse?> UpdateExternalAsync(
        WorkspaceReadScope scope,
        int applicationId,
        int screeningId,
        UpdateExternalScreeningRequest request,
        CancellationToken ct = default)
    {
        var operationKey = NormalizeOperationKey(request.OperationKey);
        var digest = Digest(operationKey);
        var outcome = await _atomic.ExecuteAsync(
            Identity("screening.external.update", scope.PortfolioId, applicationId, digest),
            new UpdateExternalScreeningCommand(
                scope.PortfolioId,
                applicationId,
                screeningId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationKey,
                request.Status,
                request.ProviderReference,
                request.ProviderHostedUrl,
                request.CreditReportingAgencyName,
                request.CreditReportingAgencyAddress,
                request.CreditReportingAgencyPhone,
                request.OccurredAtUtc,
                $"screening-external-update:{screeningId}:{digest}"),
            MutationCodec,
            ct);
        return Response(outcome.Value);
    }

    public async Task<ApplicantScreeningResponse?> RecordDecisionAsync(
        WorkspaceReadScope scope,
        int applicationId,
        int screeningId,
        RecordScreeningDecisionRequest request,
        CancellationToken ct = default)
    {
        var operationKey = NormalizeOperationKey(request.OperationKey);
        if (request.Decision is null)
            throw new ArgumentException("A screening decision is required.");
        var digest = Digest(operationKey);
        var outcome = await _atomic.ExecuteAsync(
            Identity("screening.decision", scope.PortfolioId, applicationId, digest),
            new RecordScreeningDecisionCommand(
                scope.PortfolioId,
                applicationId,
                screeningId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationKey,
                request.Decision.Value,
                request.Reason,
                request.ConsumerReportUsed,
                $"screening-decision:{screeningId}:{digest}"),
            MutationCodec,
            ct);
        return Response(outcome.Value);
    }

    public async Task<ApplicantScreeningResponse?> ApplyProviderDeliveryAsync(
        ScreeningProviderStatusDelivery delivery,
        CancellationToken ct = default)
    {
        var digest = Digest($"{delivery.ProviderKey}:{delivery.DeliveryId}");
        var outcome = await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("screening.provider-delivery", digest),
            new ApplyScreeningProviderDeliveryCommand(
                delivery.ProviderKey,
                delivery.DeliveryId,
                delivery.ProviderReference,
                delivery.EventType,
                delivery.Status,
                delivery.OccurredAtUtc,
                delivery.ProviderHostedUrl,
                delivery.CreditReportingAgencyName,
                delivery.CreditReportingAgencyAddress,
                delivery.CreditReportingAgencyPhone,
                $"screening-provider:{digest}"),
            MutationCodec,
            ct);
        return Response(outcome.Value);
    }

    public async Task<AdverseActionNoticeResponse?> GenerateAdverseActionAsync(
        WorkspaceReadScope scope,
        int applicationId,
        GenerateAdverseActionRequest request,
        CancellationToken ct = default)
    {
        var operationKey = NormalizeOperationKey(request.OperationKey);
        var digest = Digest(operationKey);
        var prepared = await _atomic.ExecuteAsync(
            Identity("adverse-action.prepare", scope.PortfolioId, applicationId, digest),
            new PrepareAdverseActionNoticeCommand(
                scope.PortfolioId,
                applicationId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                operationKey,
                request.Reason,
                request.SendToApplicant),
            AdversePrepareCodec,
            ct);
        if (prepared.Value.Outcome == ScreeningMutationOutcome.NotFound)
            return null;
        var value = prepared.Value;
        if (value.Reason is null || value.CreditReportingAgencyName is null
            || value.CreditReportingAgencyAddress is null || value.CreditReportingAgencyPhone is null
            || value.CreditReportingAgencyBlock is null || value.FileName is null
            || value.StorageKey is null || value.ApplicantName is null)
            throw new AtomicReceiptInvariantException("The adverse-action preparation receipt is incomplete.");

        var pdfBytes = _pdf.Generate(new AdverseActionNoticeData
        {
            ManagementCompanyName = value.ManagementCompanyName,
            PortfolioName = value.PortfolioName,
            ApplicantName = value.ApplicantName,
            PropertyLine = PropertyLine(value),
            NoticeDate = value.GeneratedAtUtc,
            Reason = value.Reason,
            CreditReportingAgencyName = value.CreditReportingAgencyName,
            CreditReportingAgencyAddress = value.CreditReportingAgencyAddress,
            CreditReportingAgencyPhone = value.CreditReportingAgencyPhone,
        });

        var committedFileSize = await _db.StoredFiles.AsNoTracking()
            .Where(file => file.PortfolioId == scope.PortfolioId
                && file.FilePath == value.StorageKey
                && _db.RentalApplications.AsNoTracking()
                    .WhereAuthorized(
                        _db,
                        scope,
                        [CapabilityKeys.LeasingApplicationsManage],
                        _timeProvider.UtcNow())
                    .Any(application => application.Id == applicationId))
            .Select(file => (long?)file.FileSize)
            .SingleOrDefaultAsync(ct);
        if (!committedFileSize.HasValue)
        {
            await _storage.UploadAtAsync(
                new MemoryStream(pdfBytes, writable: false),
                value.StorageKey,
                value.FileName,
                "application/pdf",
                ct);
        }

        var finalized = await _atomic.ExecuteAsync(
            Identity("adverse-action.finalize", scope.PortfolioId, applicationId, digest),
            new CreateAdverseActionNoticeCommand(
                scope.PortfolioId,
                applicationId,
                scope.UserId,
                scope.SessionId,
                scope.AccessContextId,
                scope.AccessRevision,
                value.Reason,
                value.CreditReportingAgencyBlock,
                value.FileName,
                value.StorageKey,
                committedFileSize ?? pdfBytes.LongLength,
                value.SendToApplicant,
                $"adverse-action:{scope.PortfolioId}:{applicationId}:{digest}",
                value.GeneratedAtUtc),
            AdverseFinalizeCodec,
            ct);
        return new AdverseActionNoticeResponse
        {
            Id = finalized.Value.NoticeId,
            ApplicationId = finalized.Value.ApplicationId,
            Reason = finalized.Value.Reason,
            CreditReportingAgency = finalized.Value.CreditReportingAgency,
            GeneratedAtUtc = finalized.Value.GeneratedAtUtc,
            StoredFileId = finalized.Value.StoredFileId,
            SentAtUtc = finalized.Value.SentAtUtc,
        };
    }

    private static ApplicantScreeningResponse? Response(ScreeningMutationResult result) =>
        result.Outcome == ScreeningMutationOutcome.NotFound || result.Screening is null
            ? null
            : Response(result.Screening);

    private static ApplicantScreeningResponse Response(ApplicantScreeningSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        ApplicationId = snapshot.ApplicationId,
        Mode = snapshot.Mode,
        Status = snapshot.Status,
        ProviderDisplayName = snapshot.ProviderDisplayName,
        ProviderReference = snapshot.ProviderReference,
        ProviderHostedUrl = snapshot.ProviderHostedUrl,
        ConsentConfirmed = snapshot.ConsentConfirmed,
        InvitedAtUtc = snapshot.InvitedAtUtc,
        ApplicantSubmittedAtUtc = snapshot.ApplicantSubmittedAtUtc,
        CompletedAtUtc = snapshot.CompletedAtUtc,
        FailedAtUtc = snapshot.FailedAtUtc,
        LastStatusAtUtc = snapshot.LastStatusAtUtc,
        Decision = snapshot.Decision,
        DecisionReason = snapshot.DecisionReason,
        ConsumerReportUsedForDecision = snapshot.ConsumerReportUsedForDecision,
        CreditReportingAgencyName = snapshot.CreditReportingAgencyName,
        CreditReportingAgencyAddress = snapshot.CreditReportingAgencyAddress,
        CreditReportingAgencyPhone = snapshot.CreditReportingAgencyPhone,
        HasCompleteCreditReportingAgencyContact = snapshot.HasCompleteCreditReportingAgencyContact,
        CanGenerateAdverseAction = snapshot.CanGenerateAdverseAction,
        StatusSummary = snapshot.StatusSummary,
        NextAction = snapshot.NextAction,
        IsTerminal = snapshot.IsTerminal,
        CanOpenProvider = snapshot.CanOpenProvider,
    };

    private static string NormalizeOperationKey(string operationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var normalized = operationKey.Trim();
        if (normalized.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(operationKey), "Operation key cannot exceed 200 characters.");
        return normalized;
    }

    private static AtomicCommandIdentity Identity(
        string commandType, int portfolioId, int applicationId, string digest) =>
        new(commandType, $"{portfolioId}:{applicationId}:{digest}");

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? PropertyLine(PrepareAdverseActionNoticeResult value) =>
        value.PropertyName is null
            ? null
            : $"{value.PropertyName} — {value.PropertyAddressLine1}, {value.PropertyCity}, {value.PropertyState} {value.PropertyPostalCode}"
                .Trim(' ', '—');
}
