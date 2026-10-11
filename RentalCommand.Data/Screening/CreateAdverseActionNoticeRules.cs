using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Constants;
using RentalCommand.Core;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;
using RentalCommand.Data.Documents;

namespace RentalCommand.Data.Screening;

public sealed class PrepareAdverseActionNoticeRule
{
    private readonly RentalCommandDbContext _db;

    public PrepareAdverseActionNoticeRule(RentalCommandDbContext db) => _db = db;

    public async Task<PrepareAdverseActionNoticeResult> ExecuteAsync(
        PrepareAdverseActionNoticeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        var now = await context.ReadDatabaseClockUtcAsync(ct);

        var prepared = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                _db, now, tracking: false)
            .Select(application => new
            {
                application.Id,
                application.FirstName,
                application.LastName,
                application.Status,
                application.DecisionReason,
                application.ReviewedAtUtc,
                PortfolioName = application.Portfolio != null ? application.Portfolio.Name : null,
                ManagementCompanyName = application.Portfolio != null
                    ? application.Portfolio.ManagementCompanyName : null,
                PropertyName = application.Property != null ? application.Property.Name : null,
                PropertyAddressLine1 = application.Property != null ? application.Property.AddressLine1 : null,
                PropertyCity = application.Property != null ? application.Property.City : null,
                PropertyState = application.Property != null ? application.Property.State : null,
                PropertyPostalCode = application.Property != null ? application.Property.PostalCode : null,
                Screening = _db.Set<ApplicantScreening>().AsNoTracking()
                    .Where(screening => screening.PortfolioId == command.PortfolioId
                        && screening.ApplicationId == command.ApplicationId
                        && screening.Status == ApplicantScreeningStatus.Completed)
                    .OrderByDescending(screening => screening.CompletedAtUtc)
                    .Select(screening => new
                    {
                        screening.Id,
                        screening.Status,
                        screening.Decision,
                        screening.ConsumerReportUsedForDecision,
                        screening.CreditReportingAgencyName,
                        screening.CreditReportingAgencyAddress,
                        screening.CreditReportingAgencyPhone,
                        screening.DecisionReason,
                        screening.DecisionRecordedByUserId,
                        screening.DecisionRecordedAtUtc,
                    })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (prepared is null)
            throw ScreeningCommandSupport.Denied();
        if (prepared.Status != ApplicationStatus.Declined)
        {
            throw new ArgumentException(
                "The application must be declined before an adverse-action notice can be prepared.");
        }
        if (prepared.Screening is not
            {
                Id: > 0,
                Status: ApplicantScreeningStatus.Completed,
                Decision: ScreeningDecision.Decline,
                ConsumerReportUsedForDecision: true,
                CreditReportingAgencyName: not null,
                CreditReportingAgencyAddress: not null,
                CreditReportingAgencyPhone: not null,
                DecisionRecordedAtUtc: not null,
            })
        {
            throw new ArgumentException(
                "Record a declined screening decision that was influenced by a consumer report, including the consumer reporting agency name, mailing address, and phone, before generating an adverse-action notice.");
        }

        var reason = !string.IsNullOrWhiteSpace(command.Reason)
            ? command.Reason.Trim()
            : !string.IsNullOrWhiteSpace(prepared.DecisionReason)
                ? prepared.DecisionReason.Trim()
                : string.IsNullOrWhiteSpace(prepared.Screening.DecisionReason)
                    ? "Information contained in a consumer report obtained from the consumer reporting agency named below."
                    : prepared.Screening.DecisionReason;
        var fileName = $"adverse-action-application-{command.ApplicationId}.pdf";
        var craBlock = $"{prepared.Screening.CreditReportingAgencyName}, "
            + $"{prepared.Screening.CreditReportingAgencyAddress}, "
            + prepared.Screening.CreditReportingAgencyPhone;

        return new PrepareAdverseActionNoticeResult(
            ScreeningMutationOutcome.Applied,
            prepared.Id,
            prepared.Screening.Id,
            prepared.ManagementCompanyName,
            prepared.PortfolioName,
            $"{prepared.FirstName} {prepared.LastName}".Trim(),
            prepared.PropertyName,
            prepared.PropertyAddressLine1,
            prepared.PropertyCity,
            prepared.PropertyState,
            prepared.PropertyPostalCode,
            reason,
            prepared.Screening.CreditReportingAgencyName,
            prepared.Screening.CreditReportingAgencyAddress,
            prepared.Screening.CreditReportingAgencyPhone,
            craBlock,
            fileName,
            prepared.Screening.DecisionRecordedAtUtc,
            ScreeningCommandSupport.ComputeAdverseActionDecisionFingerprint(
                new ScreeningCommandSupport.AdverseActionDecisionSnapshot(
                    prepared.Id,
                    prepared.Screening.Id,
                    prepared.Status,
                    prepared.DecisionReason,
                    prepared.ReviewedAtUtc,
                    prepared.Screening.Status,
                    prepared.Screening.Decision,
                    prepared.Screening.DecisionReason,
                    prepared.Screening.DecisionRecordedByUserId,
                    prepared.Screening.DecisionRecordedAtUtc,
                    prepared.Screening.ConsumerReportUsedForDecision,
                    prepared.Screening.CreditReportingAgencyName,
                    prepared.Screening.CreditReportingAgencyAddress,
                    prepared.Screening.CreditReportingAgencyPhone)),
            command.SendToApplicant,
            now);
    }

    public Task AuthorizeAsync(
        PrepareAdverseActionNoticeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, _db, ct);
}

/// <summary>Pure database finalizer for an adverse-action notice package.</summary>
public sealed class CreateAdverseActionNoticeRule
{
    private readonly RentalCommandDbContext _db;

    public CreateAdverseActionNoticeRule(RentalCommandDbContext db) => _db = db;

    public async Task<CreateAdverseActionNoticeResult> ExecuteAsync(
        CreateAdverseActionNoticeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CreditReportingAgency);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DecisionFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OperationKeyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StoragePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ContentType);
        if (command.ScreeningId <= 0 || command.PendingUploadId == Guid.Empty
            || command.DecisionRecordedAtUtc == default || command.FileSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.FileSize));

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        var application = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                _db, now, tracking: false)
            .SingleOrDefaultAsync(ct);
        if (application is null)
            throw ScreeningCommandSupport.Denied();

        var screening = await _db.Set<ApplicantScreening>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.ScreeningId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.ApplicationId == command.ApplicationId, ct);
        if (screening is null
            || application.Status != ApplicationStatus.Declined
            || screening.Status != ApplicantScreeningStatus.Completed
            || screening.Decision != ScreeningDecision.Decline
            || !screening.ConsumerReportUsedForDecision
            || !ScreeningCommandSupport.HasCompleteCraContact(screening)
            || screening.DecisionRecordedAtUtc != command.DecisionRecordedAtUtc
            || !ScreeningCommandSupport.FingerprintsMatch(
                command.DecisionFingerprint,
                ScreeningCommandSupport.ComputeAdverseActionDecisionFingerprint(
                    new ScreeningCommandSupport.AdverseActionDecisionSnapshot(
                        application.Id,
                        screening.Id,
                        application.Status,
                        application.DecisionReason,
                        application.ReviewedAtUtc,
                        screening.Status,
                        screening.Decision,
                        screening.DecisionReason,
                        screening.DecisionRecordedByUserId,
                        screening.DecisionRecordedAtUtc,
                        screening.ConsumerReportUsedForDecision,
                        screening.CreditReportingAgencyName,
                        screening.CreditReportingAgencyAddress,
                        screening.CreditReportingAgencyPhone))))
        {
            throw new InvalidOperationException(
                "The screening decision changed after this notice was prepared. Generate a new adverse-action notice.");
        }

        var pending = (await AtomicPendingFileUploadPersistence.LockPreparedSetAsync(
            _db,
            context,
            command.PortfolioId,
            command.ActorUserId,
            [new AtomicPendingFileUploadExpectation(
                command.PendingUploadId,
                command.Purpose,
                command.OperationKeyHash,
                command.RequestFingerprint,
                command.StoragePath,
                command.FileName,
                command.ContentType,
                command.FileSize)],
            ct)).SingleOrDefault()
            ?? throw new DomainValidationException(
                "This adverse-action notice upload is no longer available; retry with a new request key.",
                409);

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StoragePath,
            ContentType = command.ContentType,
            FileSize = command.FileSize,
            EntityType = "Application",
            EntityId = command.ApplicationId,
            UploadedAt = command.GeneratedAtUtc,
        };
        var sentAtUtc = command.SendToApplicant && !string.IsNullOrWhiteSpace(application.Email)
            ? command.GeneratedAtUtc
            : (DateTime?)null;
        var notice = new AdverseActionNotice
        {
            PortfolioId = command.PortfolioId,
            ApplicationId = command.ApplicationId,
            Reason = command.Reason,
            CreditReportingAgency = command.CreditReportingAgency,
            GeneratedAtUtc = command.GeneratedAtUtc,
            StoredFile = storedFile,
            SentAtUtc = sentAtUtc,
            CreatedAt = command.GeneratedAtUtc,
            UpdatedAt = now,
        };
        _db.Add(notice);
        context.BindSemanticAudit(storedFile, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(StoredFile), 0,
            AuditLogOperation.Created, "Adverse-action PDF stored."));
        await context.FlushBusinessAsync(ct);
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = storedFile.Id;
        pending.UpdatedAtUtc = now;
        await context.FlushBusinessAsync(ct);
        context.StageSemanticEvent(ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(AdverseActionNotice), notice.Id,
            AuditLogOperation.Created, "FCRA adverse-action notice generated."), now);

        if (sentAtUtc.HasValue)
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.AdverseActionNotice,
                    adverseActionNoticeId = notice.Id,
                    applicationId = command.ApplicationId,
                    attachmentStoredFileId = storedFile.Id,
                    attachmentFileName = storedFile.FileName,
                    attachmentContentType = storedFile.ContentType,
                    to = application.Email,
                    subject = "Notice regarding your rental application",
                    body = "Please find attached a notice regarding the decision on your rental application, "
                        + "including your rights under the Fair Credit Reporting Act (FCRA).",
                }),
                IdempotencyKey = command.DeliveryIdempotencyKey,
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(AdverseActionNotice),
                entityId = notice.Id,
                operation = "adverse-action",
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new CreateAdverseActionNoticeResult(
            notice.Id,
            notice.ApplicationId,
            notice.Reason,
            notice.CreditReportingAgency,
            notice.GeneratedAtUtc,
            storedFile.Id,
            notice.SentAtUtc);
    }

    public Task AuthorizeAsync(
        CreateAdverseActionNoticeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, _db, ct);
}
