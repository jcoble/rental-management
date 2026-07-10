using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;

namespace RentalCommand.Data.Screening;

/// <summary>Pure database handler for an adverse-action notice package.</summary>
public sealed class CreateAdverseActionNoticeHandler
    : IAtomicCommandHandler<CreateAdverseActionNoticeCommand, CreateAdverseActionNoticeResult>
{
    public async Task<CreateAdverseActionNoticeResult> HandleAsync(
        CreateAdverseActionNoticeCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var applicant = await attempt.Persistence.Query<RentalApplication>()
            .Where(application =>
                application.Id == command.ApplicationId
                && application.PortfolioId == command.PortfolioId)
            .Select(application => new { application.Email })
            .SingleOrDefaultAsync(ct);
        if (applicant is null)
        {
            throw new InvalidOperationException("The rental application no longer exists in the requested portfolio.");
        }

        var applicantEmail = applicant.Email;

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

        var sentAtUtc = command.SendToApplicant && !string.IsNullOrWhiteSpace(applicantEmail)
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
            UpdatedAt = command.GeneratedAtUtc,
        };

        attempt.Persistence.Add(notice);
        await attempt.FlushBusinessAsync(ct);

        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AdverseActionNotice),
            notice.Id,
            AuditLogOperation.Created,
            UserId: command.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                notice.ApplicationId,
                notice.StoredFileId,
                notice.GeneratedAtUtc,
                DeliveryRequested = command.SendToApplicant,
                DeliveryEnqueued = sentAtUtc.HasValue,
            }),
            ChangeReason: $"FCRA adverse-action notice generated for application #{command.ApplicationId}."));

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
                    to = applicantEmail,
                    subject = "Notice regarding your rental application",
                    body = "Please find attached a notice regarding the decision on your rental application, "
                        + "including your rights under the Fair Credit Reporting Act (FCRA).",
                }),
                IdempotencyKey = command.DeliveryIdempotencyKey,
                CreatedAtUtc = command.GeneratedAtUtc,
                NextAttemptAtUtc = command.GeneratedAtUtc,
            });
        }

        return new CreateAdverseActionNoticeResult(
            notice.Id,
            notice.ApplicationId,
            notice.Reason,
            notice.CreditReportingAgency,
            notice.GeneratedAtUtc,
            storedFile.Id,
            notice.SentAtUtc);
    }

}
