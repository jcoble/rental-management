using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;

namespace RentalCommand.Data.Screening;

public sealed class PrepareAdverseActionNoticeHandler
    : IAtomicCommandHandler<PrepareAdverseActionNoticeCommand, PrepareAdverseActionNoticeResult>,
      IAtomicReplayAuthorizer<PrepareAdverseActionNoticeCommand>
{
    public async Task<PrepareAdverseActionNoticeResult> HandleAsync(
        PrepareAdverseActionNoticeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ScreeningCommandSupport.RequireKey(command.OperationKey);
        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);

        var prepared = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                attempt.Persistence, now, tracking: false)
            .Select(application => new
            {
                application.Id,
                application.FirstName,
                application.LastName,
                application.DecisionReason,
                PortfolioName = application.Portfolio != null ? application.Portfolio.Name : null,
                ManagementCompanyName = application.Portfolio != null
                    ? application.Portfolio.ManagementCompanyName : null,
                PropertyName = application.Property != null ? application.Property.Name : null,
                PropertyAddressLine1 = application.Property != null ? application.Property.AddressLine1 : null,
                PropertyCity = application.Property != null ? application.Property.City : null,
                PropertyState = application.Property != null ? application.Property.State : null,
                PropertyPostalCode = application.Property != null ? application.Property.PostalCode : null,
                Screening = attempt.Persistence.Query<ApplicantScreening>().AsNoTracking()
                    .Where(screening => screening.PortfolioId == command.PortfolioId
                        && screening.ApplicationId == command.ApplicationId
                        && screening.Status == ApplicantScreeningStatus.Completed)
                    .OrderByDescending(screening => screening.CompletedAtUtc)
                    .Select(screening => new
                    {
                        screening.Decision,
                        screening.ConsumerReportUsedForDecision,
                        screening.CreditReportingAgencyName,
                        screening.CreditReportingAgencyAddress,
                        screening.CreditReportingAgencyPhone,
                        screening.DecisionReason,
                    })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (prepared is null)
            throw ScreeningCommandSupport.Denied();
        if (prepared.Screening is not
            {
                Decision: ScreeningDecision.Decline,
                ConsumerReportUsedForDecision: true,
                CreditReportingAgencyName: not null,
                CreditReportingAgencyAddress: not null,
                CreditReportingAgencyPhone: not null,
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
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(command.OperationKey))).ToLowerInvariant();
        var fileName = $"adverse-action-application-{command.ApplicationId}.pdf";
        var storageKey = $"adverse-action-{command.PortfolioId}-{command.ApplicationId}-{digest}.pdf";
        var craBlock = $"{prepared.Screening.CreditReportingAgencyName}, "
            + $"{prepared.Screening.CreditReportingAgencyAddress}, "
            + prepared.Screening.CreditReportingAgencyPhone;

        return new PrepareAdverseActionNoticeResult(
            ScreeningMutationOutcome.Applied,
            prepared.Id,
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
            storageKey,
            command.SendToApplicant,
            now);
    }

    public Task AuthorizeReplayAsync(
        PrepareAdverseActionNoticeCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}

/// <summary>Pure database finalizer for an adverse-action notice package.</summary>
public sealed class CreateAdverseActionNoticeHandler
    : IAtomicCommandHandler<CreateAdverseActionNoticeCommand, CreateAdverseActionNoticeResult>,
      IAtomicReplayAuthorizer<CreateAdverseActionNoticeCommand>
{
    public async Task<CreateAdverseActionNoticeResult> HandleAsync(
        CreateAdverseActionNoticeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        ScreeningCommandSupport.ValidateActor(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CreditReportingAgency);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StorageKey);
        if (command.FileSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.FileSize));

        await ScreeningCommandSupport.LockStaffApplicationAsync(attempt, command.PortfolioId,
            command.ApplicationId, command.AuthSessionId, command.AccessContextId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        var applicant = await ScreeningCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                attempt.Persistence, now, tracking: false)
            .Select(application => new { application.Email })
            .SingleOrDefaultAsync(ct);
        if (applicant is null)
            throw ScreeningCommandSupport.Denied();

        var storedFile = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            FileName = command.FileName,
            FilePath = command.StorageKey,
            ContentType = "application/pdf",
            FileSize = command.FileSize,
            EntityType = "Application",
            EntityId = command.ApplicationId,
            UploadedAt = command.GeneratedAtUtc,
        };
        var sentAtUtc = command.SendToApplicant && !string.IsNullOrWhiteSpace(applicant.Email)
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
        attempt.Persistence.Add(notice);
        attempt.BindSemanticAudit(storedFile, ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(StoredFile), 0,
            AuditLogOperation.Created, "Adverse-action PDF stored."));
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(ScreeningCommandSupport.Audit(
            command.PortfolioId, command.ActorUserId, nameof(AdverseActionNotice), notice.Id,
            AuditLogOperation.Created, "FCRA adverse-action notice generated."), now);

        if (sentAtUtc.HasValue)
        {
            attempt.StageOutbox(new OutboxMessage
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
                    to = applicant.Email,
                    subject = "Notice regarding your rental application",
                    body = "Please find attached a notice regarding the decision on your rental application, "
                        + "including your rights under the Fair Credit Reporting Act (FCRA).",
                }),
                IdempotencyKey = command.DeliveryIdempotencyKey,
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        attempt.StageOutbox(new OutboxMessage
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

    public Task AuthorizeReplayAsync(
        CreateAdverseActionNoticeCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ScreeningCommandSupport.AuthorizeReplayAsync(command.PortfolioId, command.ApplicationId,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, persistence, ct);
}
