using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;

namespace RentalCommand.Data.Screening;

public sealed class TrackExternalScreeningHandler
    : IAtomicCommandHandler<TrackExternalScreeningCommand, ScreeningMutationResult>,
      IAtomicReplayAuthorizer<TrackExternalScreeningCommand>
{
    public async Task<ScreeningMutationResult> HandleAsync(
        TrackExternalScreeningCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderDisplayName);
        if (command.Status == ApplicantScreeningStatus.AwaitingProvider)
            throw new ArgumentException("AwaitingProvider is reserved for integrated screening.");

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        var application = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(ct);
        if (application is null)
            throw ScreeningCommandSupport.Denied();

        var existingId = await attempt.Persistence.Query<ApplicantScreening>().AsNoTracking()
            .Where(screening => screening.PortfolioId == command.PortfolioId
                && screening.ApplicationId == command.ApplicationId
                && screening.OperationKey == command.OperationKey)
            .Select(screening => (int?)screening.Id)
            .SingleOrDefaultAsync(ct);
        if (existingId.HasValue)
            return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
                attempt.Persistence, command.PortfolioId, existingId.Value, ct));

        var screening = new ApplicantScreening
        {
            PortfolioId = command.PortfolioId,
            ApplicationId = command.ApplicationId,
            Mode = ScreeningMode.External,
            Status = command.Status,
            ProviderDisplayName = command.ProviderDisplayName.Trim(),
            ProviderReference = ScreeningCommandSupport.Clean(command.ProviderReference),
            ProviderHostedUrl = ScreeningCommandSupport.Clean(command.ProviderHostedUrl),
            CreditReportingAgencyName = ScreeningCommandSupport.Clean(command.CreditReportingAgencyName),
            CreditReportingAgencyAddress = ScreeningCommandSupport.Clean(command.CreditReportingAgencyAddress),
            CreditReportingAgencyPhone = ScreeningCommandSupport.Clean(command.CreditReportingAgencyPhone),
            OperationKey = command.OperationKey,
            ConsentConfirmed = application.ConsentGiven,
            ConsentAtUtc = application.ConsentAtUtc,
            CompletedAtUtc = command.Status == ApplicantScreeningStatus.Completed ? now : null,
            FailedAtUtc = command.Status == ApplicantScreeningStatus.Failed ? now : null,
            LastStatusAtUtc = now,
            CreatedByUserId = command.ActorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        attempt.Persistence.Add(screening);
        attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), 0,
            AuditLogOperation.Created, "External applicant screening tracked."));
        ScreeningCommandSupport.MarkApplicationUnderReview(
            application, command.ActorUserId, attempt, now);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.StageUpdate(attempt, command.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, "external-create");
        return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, command.PortfolioId, screening.Id, ct));
    }

    public Task AuthorizeReplayAsync(
        TrackExternalScreeningCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

public sealed class PrepareIntegratedScreeningHandler
    : IAtomicCommandHandler<PrepareIntegratedScreeningCommand, PrepareIntegratedScreeningResult>,
      IAtomicReplayAuthorizer<PrepareIntegratedScreeningCommand>
{
    public async Task<PrepareIntegratedScreeningResult> HandleAsync(
        PrepareIntegratedScreeningCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderDisplayName);

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        var application = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(ct);
        if (application is null)
            throw ScreeningCommandSupport.Denied();

        var screening = await attempt.Persistence.Query<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.PortfolioId == command.PortfolioId
                && candidate.ApplicationId == command.ApplicationId
                && candidate.OperationKey == command.OperationKey, ct);
        if (screening is not null && screening.Status != ApplicantScreeningStatus.AwaitingProvider)
        {
            return new PrepareIntegratedScreeningResult(
                ScreeningMutationOutcome.Applied,
                await ApplicantScreeningQueries.LoadSnapshotAsync(
                    attempt.Persistence, command.PortfolioId, screening.Id, ct),
                command.ApplicationId,
                command.OperationKey,
                null,
                null,
                null);
        }

        if (!application.ConsentGiven || application.ConsentAtUtc is null)
            throw new ArgumentException("Applicant screening consent is required.");
        if (string.IsNullOrWhiteSpace(application.Email))
            throw new ArgumentException("Applicant email is required for integrated screening.");

        if (screening is null)
        {
            screening = new ApplicantScreening
            {
                PortfolioId = command.PortfolioId,
                ApplicationId = command.ApplicationId,
                Mode = ScreeningMode.Integrated,
                ProviderKey = command.ProviderKey.Trim(),
                ProviderDisplayName = command.ProviderDisplayName.Trim(),
                OperationKey = command.OperationKey,
                ConsentConfirmed = true,
                ConsentAtUtc = application.ConsentAtUtc,
                CreatedByUserId = command.ActorUserId,
                CreatedAt = now,
            };
            attempt.Persistence.Add(screening);
            attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
                command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), 0,
                AuditLogOperation.Created, "Integrated applicant screening prepared."));
        }
        else
        {
            attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
                command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), screening.Id,
                AuditLogOperation.Updated, "Integrated applicant screening preparation resumed."));
        }

        screening.Status = ApplicantScreeningStatus.AwaitingProvider;
        screening.LastStatusAtUtc = now;
        screening.UpdatedAt = now;
        ScreeningCommandSupport.MarkApplicationUnderReview(
            application, command.ActorUserId, attempt, now);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.StageUpdate(attempt, command.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, "integrated-prepare");
        var snapshot = await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, command.PortfolioId, screening.Id, ct);
        return new PrepareIntegratedScreeningResult(
            ScreeningMutationOutcome.Applied,
            snapshot,
            command.ApplicationId,
            command.OperationKey,
            $"{application.FirstName} {application.LastName}".Trim(),
            application.Email,
            application.ConsentAtUtc);
    }

    public Task AuthorizeReplayAsync(
        PrepareIntegratedScreeningCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

public sealed class FinalizeIntegratedScreeningHandler
    : IAtomicCommandHandler<FinalizeIntegratedScreeningCommand, ScreeningMutationResult>,
      IAtomicReplayAuthorizer<FinalizeIntegratedScreeningCommand>
{
    public async Task<ScreeningMutationResult> HandleAsync(
        FinalizeIntegratedScreeningCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderKey);

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        if (!await ScreeningCommandSupport.IsAuthorizedAsync(command.PortfolioId, command.ApplicationId,
                command.ActorUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, attempt.Persistence, now, ct))
            throw ScreeningCommandSupport.Denied();

        var screening = await attempt.Persistence.Query<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.ScreeningId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.ApplicationId == command.ApplicationId
                && candidate.Mode == ScreeningMode.Integrated
                && candidate.ProviderKey == command.ProviderKey
                && candidate.OperationKey == command.OperationKey, ct);
        if (screening is null)
            return new(ScreeningMutationOutcome.NotFound);

        screening.ProviderReference = ScreeningCommandSupport.Clean(command.ProviderReference);
        screening.ProviderHostedUrl = ScreeningCommandSupport.Clean(command.ProviderHostedUrl);
        screening.CreditReportingAgencyName = ScreeningCommandSupport.Clean(command.CreditReportingAgencyName);
        screening.CreditReportingAgencyAddress = ScreeningCommandSupport.Clean(command.CreditReportingAgencyAddress);
        screening.CreditReportingAgencyPhone = ScreeningCommandSupport.Clean(command.CreditReportingAgencyPhone);
        screening.Status = command.Accepted
            ? ApplicantScreeningStatus.AwaitingApplicant
            : ApplicantScreeningStatus.Failed;
        screening.InvitedAtUtc = command.Accepted ? command.InvitedAtUtc ?? now : null;
        screening.FailedAtUtc = command.Accepted ? null : now;
        screening.LastStatusAtUtc = now;
        screening.UpdatedAt = now;
        attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), screening.Id,
            AuditLogOperation.Updated,
            command.Accepted ? "Integrated screening invitation accepted." : "Integrated screening invitation failed."));
        var milestone = ScreeningCommandSupport.Milestone(
            screening, command.ProviderKey, command.DeliveryIdempotencyKey,
            command.Accepted ? "invitation.accepted" : $"invitation.failed:{command.ErrorCode ?? "unknown"}",
            screening.Status, now, now);
        attempt.Persistence.Add(milestone);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.AuditMilestone(attempt, milestone, now);
        ScreeningCommandSupport.StageUpdate(attempt, command.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, "integrated-finalize");
        return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, command.PortfolioId, screening.Id, ct));
    }

    public Task AuthorizeReplayAsync(
        FinalizeIntegratedScreeningCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

public sealed class UpdateExternalScreeningHandler
    : IAtomicCommandHandler<UpdateExternalScreeningCommand, ScreeningMutationResult>,
      IAtomicReplayAuthorizer<UpdateExternalScreeningCommand>
{
    public async Task<ScreeningMutationResult> HandleAsync(
        UpdateExternalScreeningCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        if (command.Status is ApplicantScreeningStatus.Created or ApplicantScreeningStatus.AwaitingProvider)
            throw new ArgumentException("That status is not valid for an externally managed screening.");

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        if (!await ScreeningCommandSupport.IsAuthorizedAsync(command.PortfolioId, command.ApplicationId,
                command.ActorUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, attempt.Persistence, now, ct))
            throw ScreeningCommandSupport.Denied();
        var screening = await attempt.Persistence.Query<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.ScreeningId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.ApplicationId == command.ApplicationId
                && candidate.Mode == ScreeningMode.External, ct);
        if (screening is null)
            return new(ScreeningMutationOutcome.NotFound);

        var occurredAt = command.OccurredAtUtc ?? now;
        var nextStatus = command.Status ?? screening.Status;
        screening.Status = nextStatus;
        screening.ProviderReference = ScreeningCommandSupport.Clean(command.ProviderReference) ?? screening.ProviderReference;
        screening.ProviderHostedUrl = ScreeningCommandSupport.Clean(command.ProviderHostedUrl) ?? screening.ProviderHostedUrl;
        screening.CreditReportingAgencyName = ScreeningCommandSupport.Clean(command.CreditReportingAgencyName) ?? screening.CreditReportingAgencyName;
        screening.CreditReportingAgencyAddress = ScreeningCommandSupport.Clean(command.CreditReportingAgencyAddress) ?? screening.CreditReportingAgencyAddress;
        screening.CreditReportingAgencyPhone = ScreeningCommandSupport.Clean(command.CreditReportingAgencyPhone) ?? screening.CreditReportingAgencyPhone;
        screening.ApplicantSubmittedAtUtc ??= nextStatus == ApplicantScreeningStatus.InProgress ? occurredAt : null;
        screening.CompletedAtUtc = nextStatus == ApplicantScreeningStatus.Completed ? occurredAt : screening.CompletedAtUtc;
        screening.FailedAtUtc = nextStatus == ApplicantScreeningStatus.Failed ? occurredAt : null;
        screening.LastStatusAtUtc = occurredAt;
        screening.UpdatedAt = now;
        attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), screening.Id,
            AuditLogOperation.Updated, "External applicant screening updated."));
        var milestone = ScreeningCommandSupport.Milestone(
            screening, "manual", command.DeliveryIdempotencyKey,
            command.Status is null
                ? "external.cra-contact-updated"
                : $"external.{nextStatus.ToString().ToLowerInvariant()}",
            nextStatus, occurredAt, now);
        attempt.Persistence.Add(milestone);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.AuditMilestone(attempt, milestone, now, command.ActorUserId);
        ScreeningCommandSupport.StageUpdate(attempt, command.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, "external-update");
        return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, command.PortfolioId, screening.Id, ct));
    }

    public Task AuthorizeReplayAsync(
        UpdateExternalScreeningCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

public sealed class RecordScreeningDecisionHandler
    : IAtomicCommandHandler<RecordScreeningDecisionCommand, ScreeningMutationResult>,
      IAtomicReplayAuthorizer<RecordScreeningDecisionCommand>
{
    public async Task<ScreeningMutationResult> HandleAsync(
        RecordScreeningDecisionCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        if (command.ConsumerReportUsed && string.IsNullOrWhiteSpace(command.Reason))
            throw new ArgumentException("Record the principal decision reason when a consumer report was used.");

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        if (!await ScreeningCommandSupport.IsAuthorizedAsync(command.PortfolioId, command.ApplicationId,
                command.ActorUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, attempt.Persistence, now, ct))
            throw ScreeningCommandSupport.Denied();
        var screening = await attempt.Persistence.Query<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.ScreeningId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.ApplicationId == command.ApplicationId, ct);
        if (screening is null)
            return new(ScreeningMutationOutcome.NotFound);
        if (command.ConsumerReportUsed && screening.Status != ApplicantScreeningStatus.Completed)
            throw new ArgumentException(
                "Mark the screening complete before recording a decision influenced by its consumer report.");
        if (command.ConsumerReportUsed && !ScreeningCommandSupport.HasCompleteCraContact(screening))
            throw new ArgumentException(
                "Add the consumer reporting agency name, mailing address, and phone before recording a report-based decision.");

        screening.Decision = command.Decision;
        screening.DecisionReason = ScreeningCommandSupport.Clean(command.Reason);
        screening.DecisionRecordedByUserId = command.ActorUserId;
        screening.DecisionRecordedAtUtc = now;
        screening.ConsumerReportUsedForDecision = command.ConsumerReportUsed;
        screening.UpdatedAt = now;
        attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(ApplicantScreening), screening.Id,
            AuditLogOperation.Updated, "Applicant screening decision recorded."));
        var milestone = ScreeningCommandSupport.Milestone(
            screening, "decision", command.DeliveryIdempotencyKey,
            $"decision.{command.Decision.ToString().ToLowerInvariant()}", screening.Status, now, now);
        attempt.Persistence.Add(milestone);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.AuditMilestone(attempt, milestone, now, command.ActorUserId);
        ScreeningCommandSupport.StageUpdate(attempt, command.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, "decision");
        return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, command.PortfolioId, screening.Id, ct));
    }

    public Task AuthorizeReplayAsync(
        RecordScreeningDecisionCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

public sealed class ApplyScreeningProviderDeliveryHandler
    : IAtomicCommandHandler<ApplyScreeningProviderDeliveryCommand, ScreeningMutationResult>,
      IAtomicReplayAuthorizer<ApplyScreeningProviderDeliveryCommand>
{
    public async Task<ScreeningMutationResult> HandleAsync(
        ApplyScreeningProviderDeliveryCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateProviderDelivery(command);
        var target = await attempt.Persistence.Query<ApplicantScreening>().AsNoTracking()
            .Where(screening => screening.Mode == ScreeningMode.Integrated
                && screening.ProviderKey == command.ProviderKey
                && screening.ProviderReference == command.ProviderReference)
            .Select(screening => new { screening.Id, screening.PortfolioId, screening.ApplicationId })
            .SingleOrDefaultAsync(ct);
        if (target is null)
            return new(ScreeningMutationOutcome.NotFound);

        await attempt.Locking.AcquireAsync(
            AtomicLockResource.RentalApplication, target.ApplicationId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        var screening = await attempt.Persistence.Query<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.Id == target.Id
                && candidate.PortfolioId == target.PortfolioId
                && candidate.ApplicationId == target.ApplicationId
                && candidate.Mode == ScreeningMode.Integrated
                && candidate.ProviderKey == command.ProviderKey
                && candidate.ProviderReference == command.ProviderReference, ct);
        if (screening is null)
            return new(ScreeningMutationOutcome.NotFound);

        var applied = command.OccurredAtUtc >= screening.LastStatusAtUtc
            && ScreeningCommandSupport.CanApplyProviderStatus(screening.Status, command.Status);
        if (applied)
        {
            screening.Status = command.Status;
            screening.ProviderHostedUrl = ScreeningCommandSupport.Clean(command.ProviderHostedUrl) ?? screening.ProviderHostedUrl;
            screening.CreditReportingAgencyName = ScreeningCommandSupport.Clean(command.CreditReportingAgencyName) ?? screening.CreditReportingAgencyName;
            screening.CreditReportingAgencyAddress = ScreeningCommandSupport.Clean(command.CreditReportingAgencyAddress) ?? screening.CreditReportingAgencyAddress;
            screening.CreditReportingAgencyPhone = ScreeningCommandSupport.Clean(command.CreditReportingAgencyPhone) ?? screening.CreditReportingAgencyPhone;
            screening.ApplicantSubmittedAtUtc ??= command.Status == ApplicantScreeningStatus.InProgress
                ? command.OccurredAtUtc : null;
            screening.CompletedAtUtc = command.Status == ApplicantScreeningStatus.Completed
                ? command.OccurredAtUtc : null;
            screening.FailedAtUtc = command.Status == ApplicantScreeningStatus.Failed
                ? command.OccurredAtUtc : null;
            screening.LastStatusAtUtc = command.OccurredAtUtc;
            screening.UpdatedAt = now;
            attempt.BindSemanticAudit(screening, ScreeningCommandSupport.Audit(
                screening.PortfolioId, null, nameof(ApplicantScreening), screening.Id,
                AuditLogOperation.Updated, "Verified screening provider delivery applied."));
        }

        var milestone = ScreeningCommandSupport.Milestone(
            screening, command.ProviderKey, command.DeliveryId, command.EventType,
            command.Status, command.OccurredAtUtc, now);
        attempt.Persistence.Add(milestone);
        await attempt.FlushBusinessAsync(ct);
        ScreeningCommandSupport.AuditMilestone(attempt, milestone, now);
        ScreeningCommandSupport.StageUpdate(attempt, screening.PortfolioId, screening.Id,
            command.DeliveryIdempotencyKey, now, applied ? "provider-delivery" : "provider-delivery-stale");
        return ScreeningCommandSupport.Applied(await ApplicantScreeningQueries.LoadSnapshotAsync(
            attempt.Persistence, screening.PortfolioId, screening.Id, ct));
    }

    public Task AuthorizeReplayAsync(
        ApplyScreeningProviderDeliveryCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateProviderDelivery(command);
        // The controller re-verifies the provider signature before every call. A valid delivery
        // whose target was absent must still replay its original NotFound receipt rather than
        // changing meaning if a matching reference later appears or disappears.
        return Task.CompletedTask;
    }
}

internal static class ScreeningCommandSupport
{
    internal static void ValidateActor(
        int portfolioId, int applicationId, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision)
    {
        if (portfolioId <= 0 || applicationId <= 0 || actorUserId <= 0
            || authSessionId == Guid.Empty || accessContextId <= 0 || expectedAccessRevision <= 0)
            throw new UnauthorizedAccessException("An active workspace context is required.");
    }

    internal static void RequireKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(value), "Operation key cannot exceed 200 characters.");
    }

    internal static async Task LockStaffApplicationAsync(
        IAtomicWriteAttempt attempt, int portfolioId, int applicationId,
        Guid authSessionId, int accessContextId, CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, authSessionId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.WorkspaceAccessContext, accessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, portfolioId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.RentalApplication, applicationId, ct);
    }

    internal static IQueryable<RentalApplication> AuthorizedApplications(
        int portfolioId,
        int applicationId,
        int actorUserId,
        Guid authSessionId,
        int accessContextId,
        long expectedAccessRevision,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc,
        bool tracking)
    {
        var applications = tracking
            ? persistence.Query<RentalApplication>()
            : persistence.Query<RentalApplication>().AsNoTracking();
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking();
        return applications.Where(application =>
            application.Id == applicationId
            && application.PortfolioId == portfolioId
            && persistence.Query<AuthSession>().AsNoTracking().Any(session =>
                session.Id == authSessionId
                && session.UserId == actorUserId
                && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && persistence.Query<WorkspaceAccessContext>().AsNoTracking().Any(context =>
                context.Id == accessContextId
                && context.UserId == actorUserId
                && context.PortfolioId == portfolioId
                && context.AccessRevision == expectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().AsNoTracking().Any(membership =>
                membership.AccessContextId == accessContextId
                && membership.PortfolioId == portfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == portfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= securityNowUtc
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc)
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        grant.CapabilityDefinition!.Key == CapabilityKeys.LeasingApplicationsManage
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property)
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (application.PropertyId != null
                            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(selected =>
                                selected.PortfolioId == portfolioId
                                && selected.PropertyId == application.PropertyId))))));
    }

    internal static Task<bool> IsAuthorizedAsync(
        int portfolioId, int applicationId, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        AuthorizedApplications(portfolioId, applicationId, actorUserId, authSessionId,
                accessContextId, expectedAccessRevision, persistence, now, tracking: false)
            .AnyAsync(ct);

    internal static async Task AuthorizeReplayAsync(
        int portfolioId, int applicationId, int actorUserId, Guid authSessionId,
        int accessContextId, long expectedAccessRevision,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        ValidateActor(portfolioId, applicationId, actorUserId, authSessionId,
            accessContextId, expectedAccessRevision);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await IsAuthorizedAsync(portfolioId, applicationId, actorUserId, authSessionId,
                accessContextId, expectedAccessRevision, persistence, now, ct))
            throw Denied();
    }

    internal static void MarkApplicationUnderReview(
        RentalApplication application, int actorUserId, IAtomicWriteAttempt attempt, DateTime now)
    {
        if (application.Status == ApplicationStatus.UnderReview)
            return;
        application.Status = ApplicationStatus.UnderReview;
        application.UpdatedAt = now;
        attempt.BindSemanticAudit(application, Audit(
            application.PortfolioId, actorUserId, nameof(RentalApplication), application.Id,
            AuditLogOperation.Updated, "Application moved to review for screening."));
    }

    internal static ApplicantScreeningMilestone Milestone(
        ApplicantScreening screening, string source, string deliveryId, string eventType,
        ApplicantScreeningStatus status, DateTime occurredAtUtc, DateTime recordedAtUtc) => new()
    {
        PortfolioId = screening.PortfolioId,
        ApplicantScreeningId = screening.Id,
        Source = source,
        DeliveryId = deliveryId,
        EventType = eventType,
        Status = status,
        OccurredAtUtc = occurredAtUtc,
        RecordedAtUtc = recordedAtUtc,
    };

    internal static void AuditMilestone(
        IAtomicWriteAttempt attempt, ApplicantScreeningMilestone milestone,
        DateTime now, int? userId = null) =>
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            milestone.PortfolioId,
            nameof(ApplicantScreeningMilestone),
            milestone.Id,
            AuditLogOperation.Created,
            userId,
            NewValues: JsonSerializer.Serialize(new
            {
                milestone.ApplicantScreeningId,
                milestone.Source,
                milestone.DeliveryId,
                milestone.EventType,
                milestone.Status,
                milestone.OccurredAtUtc,
            }),
            ChangeReason: "Applicant screening milestone recorded."), now);

    internal static AtomicSemanticAudit Audit(
        int portfolioId, int? userId, string entityType, int entityId,
        AuditLogOperation operation, string reason) =>
        new(portfolioId, entityType, entityId, operation, userId, ChangeReason: reason);

    internal static void StageUpdate(
        IAtomicWriteAttempt attempt, int portfolioId, int screeningId,
        string idempotencyKey, DateTime now, string operation) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(ApplicantScreening),
                entityId = screeningId,
                operation,
            }),
            IdempotencyKey = $"{idempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    internal static ScreeningMutationResult Applied(ApplicantScreeningSnapshot snapshot) =>
        new(ScreeningMutationOutcome.Applied, snapshot);

    internal static UnauthorizedAccessException Denied() =>
        new("Workspace access changed. Refresh and try again.");

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool HasCompleteCraContact(ApplicantScreening screening) =>
        !string.IsNullOrWhiteSpace(screening.CreditReportingAgencyName)
        && !string.IsNullOrWhiteSpace(screening.CreditReportingAgencyAddress)
        && !string.IsNullOrWhiteSpace(screening.CreditReportingAgencyPhone);

    internal static bool CanApplyProviderStatus(
        ApplicantScreeningStatus current, ApplicantScreeningStatus next) =>
        current == next || current switch
        {
            ApplicantScreeningStatus.Created => next is ApplicantScreeningStatus.AwaitingApplicant
                or ApplicantScreeningStatus.InProgress or ApplicantScreeningStatus.Completed
                or ApplicantScreeningStatus.Failed or ApplicantScreeningStatus.Cancelled,
            ApplicantScreeningStatus.AwaitingProvider => next is ApplicantScreeningStatus.AwaitingApplicant
                or ApplicantScreeningStatus.InProgress or ApplicantScreeningStatus.Completed
                or ApplicantScreeningStatus.Failed or ApplicantScreeningStatus.Cancelled,
            ApplicantScreeningStatus.AwaitingApplicant => next is ApplicantScreeningStatus.InProgress
                or ApplicantScreeningStatus.Completed or ApplicantScreeningStatus.Failed
                or ApplicantScreeningStatus.Cancelled,
            ApplicantScreeningStatus.InProgress => next is ApplicantScreeningStatus.Completed
                or ApplicantScreeningStatus.Failed or ApplicantScreeningStatus.Cancelled,
            _ => false,
        };

    internal static void ValidateProviderDelivery(ApplyScreeningProviderDeliveryCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DeliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ProviderReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.EventType);
        if (command.ProviderKey.Length > 80 || command.DeliveryId.Length > 200
            || command.ProviderReference.Length > 200 || command.EventType.Length > 120)
            throw new ArgumentOutOfRangeException(nameof(command), "Provider delivery metadata exceeds its persisted limits.");
        if (command.Status is ApplicantScreeningStatus.Created or ApplicantScreeningStatus.AwaitingProvider)
            throw new ArgumentException(
                "Provider callbacks cannot move a screening to an internal preparation status.");
    }
}
