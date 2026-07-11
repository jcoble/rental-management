using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

public sealed class PrepareProviderPaymentCreateHandler
    : IAtomicCommandHandler<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult>
{
    public async Task<PrepareProviderPaymentCreateResult> HandleAsync(
        PrepareProviderPaymentCreateCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var leases = attempt.Persistence.Query<Lease>();
        var payment = await attempt.Persistence.Query<Payment>()
            .Where(candidate => candidate.Id == command.PaymentId &&
                                candidate.PortfolioId == command.PortfolioId &&
                                (command.TenantId == null ||
                                 leases.Any(lease =>
                                     lease.Id == candidate.LeaseId &&
                                     lease.PortfolioId == command.PortfolioId &&
                                     lease.TenantId == command.TenantId)))
            .Select(candidate => new { candidate.Id, candidate.PortfolioId, candidate.Amount })
            .SingleOrDefaultAsync(ct);
        if (payment is null)
        {
            return new PrepareProviderPaymentCreateResult(
                PrepareProviderPaymentCreateOutcome.NotFound,
                command.PortfolioId,
                command.PaymentId,
                0,
                0,
                command.Currency,
                command.Provider,
                command.IdempotencyKey);
        }

        var transaction = new PaymentTransaction
        {
            PortfolioId = payment.PortfolioId,
            PaymentId = payment.Id,
            Amount = payment.Amount,
            Currency = command.Currency,
            Provider = command.Provider,
            IdempotencyKey = command.IdempotencyKey,
            Status = PaymentTransactionStatus.Pending,
            CreatedAt = command.PreparedAtUtc,
            UpdatedAt = command.PreparedAtUtc,
        };
        attempt.Persistence.Add(transaction);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(
            payment.PortfolioId,
            transaction.Id,
            AuditLogOperation.Created,
            "Provider payment attempt prepared",
            new
            {
                transaction.PaymentId,
                transaction.Provider,
                transaction.Amount,
                transaction.Currency,
                transaction.Status,
            }));

        return new PrepareProviderPaymentCreateResult(
            PrepareProviderPaymentCreateOutcome.Prepared,
            payment.PortfolioId,
            payment.Id,
            transaction.Id,
            transaction.Amount,
            transaction.Currency,
            transaction.Provider,
            transaction.IdempotencyKey!);
    }

    internal static AtomicSemanticAudit Audit(
        int portfolioId,
        int transactionId,
        AuditLogOperation operation,
        string reason,
        object values) =>
        new(
            portfolioId,
            nameof(PaymentTransaction),
            transactionId,
            operation,
            NewValues: JsonSerializer.Serialize(values),
            ChangeReason: reason);
}

public sealed class FinalizeProviderPaymentCreateHandler
    : IAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>
{
    public async Task<FinalizeProviderPaymentCreateResult> HandleAsync(
        FinalizeProviderPaymentCreateCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var transaction = await attempt.Persistence.Query<PaymentTransaction>()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.PaymentTransactionId &&
                                               candidate.PortfolioId == command.PortfolioId &&
                                               candidate.PaymentId == command.PaymentId &&
                                               candidate.Provider == command.Provider &&
                                               candidate.IdempotencyKey == command.IdempotencyKey, ct);
        if (transaction is null)
        {
            return Result(FinalizeProviderPaymentCreateOutcome.NotFound, command);
        }

        if (!string.IsNullOrWhiteSpace(transaction.ProviderPaymentIntentId))
        {
            if (!string.Equals(
                    transaction.ProviderPaymentIntentId,
                    command.ProviderPaymentId,
                    StringComparison.Ordinal))
            {
                throw new AtomicReceiptInvariantException(
                    $"Payment transaction {transaction.Id} is already bound to a different provider object.");
            }

            return new FinalizeProviderPaymentCreateResult(
                FinalizeProviderPaymentCreateOutcome.AlreadyFinalized,
                transaction.PortfolioId,
                transaction.PaymentId,
                transaction.Id,
                transaction.Provider,
                transaction.ProviderPaymentIntentId,
                transaction.Status);
        }

        transaction.ProviderPaymentIntentId = command.ProviderPaymentId;
        transaction.Status = command.Status;
        transaction.FailureReason = command.FailureReason;
        transaction.UpdatedAt = command.RecordedAtUtc;
        attempt.StageSemanticEvent(PrepareProviderPaymentCreateHandler.Audit(
            transaction.PortfolioId,
            transaction.Id,
            AuditLogOperation.Updated,
            "Provider payment attempt finalized",
            new
            {
                ProviderPaymentId = command.ProviderPaymentId,
                command.Status,
                command.FailureReason,
            }));

        return new FinalizeProviderPaymentCreateResult(
            FinalizeProviderPaymentCreateOutcome.Applied,
            transaction.PortfolioId,
            transaction.PaymentId,
            transaction.Id,
            transaction.Provider,
            command.ProviderPaymentId,
            command.Status);
    }

    private static FinalizeProviderPaymentCreateResult Result(
        FinalizeProviderPaymentCreateOutcome outcome,
        FinalizeProviderPaymentCreateCommand command) =>
        new(
            outcome,
            command.PortfolioId,
            command.PaymentId,
            command.PaymentTransactionId,
            command.Provider,
            command.ProviderPaymentId,
            command.Status);
}

public sealed class RecordVerifiedProviderPaymentEventHandler
    : IAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult>
{
    public async Task<RecordVerifiedProviderPaymentEventResult> HandleAsync(
        RecordVerifiedProviderPaymentEventCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var now = command.ReceivedAtUtc;
        var inbox = new ProviderInboxEvent
        {
            Provider = command.Provider,
            ProviderEventId = command.ProviderEventId,
            EventType = command.ProviderEventType,
            Payload = command.PayloadJson,
            ProviderObjectId = command.ProviderPaymentId,
            ReceivedAtUtc = now,
            NextAttemptAtUtc = now,
        };
        attempt.Persistence.Add(inbox);

        if (command.EventKind == ProviderPaymentEventKind.Ignored)
        {
            inbox.AttemptCount = 1;
            inbox.LastAttemptAtUtc = now;
            inbox.ProcessedAtUtc = now;
            await attempt.FlushBusinessAsync(ct);
            return new RecordVerifiedProviderPaymentEventResult(
                RecordProviderPaymentEventOutcome.Applied,
                inbox.Id,
                null,
                null,
                null,
                null);
        }

        if (command.EventKind == ProviderPaymentEventKind.SetupCompleted)
        {
            return await ApplySetupCompletedAsync(command, attempt, inbox, now, ct);
        }

        var transaction = await attempt.Persistence.Query<PaymentTransaction>()
            .Include(candidate => candidate.Payment)
            .SingleOrDefaultAsync(candidate => candidate.Provider == command.Provider &&
                                               candidate.ProviderPaymentIntentId == command.ProviderPaymentId, ct);
        if (transaction is null)
        {
            inbox.FailureKind = ProviderInboxFailureKind.Unmatched;
            inbox.LastError = "No local provider payment transaction matched this verified event.";
            await attempt.FlushBusinessAsync(ct);
            return new RecordVerifiedProviderPaymentEventResult(
                RecordProviderPaymentEventOutcome.Unmatched,
                inbox.Id,
                null,
                null,
                null,
                null);
        }

        inbox.PortfolioId = transaction.PortfolioId;
        inbox.AttemptCount = 1;
        inbox.LastAttemptAtUtc = now;
        inbox.ProcessedAtUtc = now;
        inbox.NextAttemptAtUtc = now;
        transaction.Status = MapStatus(command.EventKind);
        transaction.FailureReason = command.FailureReason;
        transaction.UpdatedAt = command.OccurredAtUtc ?? now;

        if (command.EventKind == ProviderPaymentEventKind.Succeeded && transaction.Payment is not null)
        {
            var payment = transaction.Payment;
            payment.Status = PaymentStatus.Paid;
            payment.PaidDate = command.OccurredAtUtc ?? now;
            payment.Method = "online";
            payment.ExternalReference = command.ProviderPaymentId;
            payment.UpdatedAt = command.OccurredAtUtc ?? now;
            attempt.BindSemanticAudit(payment, new AtomicSemanticAudit(
                payment.PortfolioId,
                nameof(Payment),
                payment.Id,
                AuditLogOperation.Updated,
                NewValues: JsonSerializer.Serialize(new
                {
                    payment.Status,
                    payment.PaidDate,
                    payment.Method,
                    payment.ExternalReference,
                }),
                ChangeReason: $"Verified {command.Provider} payment event {command.ProviderEventId}."));
        }

        attempt.StageSemanticEvent(PrepareProviderPaymentCreateHandler.Audit(
            transaction.PortfolioId,
            transaction.Id,
            AuditLogOperation.Updated,
            "Verified provider payment event reconciled",
            new
            {
                command.ProviderEventId,
                command.ProviderEventType,
                command.EventKind,
                transaction.Status,
                command.FailureReason,
            }));

        await attempt.FlushBusinessAsync(ct);
        return new RecordVerifiedProviderPaymentEventResult(
            RecordProviderPaymentEventOutcome.Applied,
            inbox.Id,
            transaction.PortfolioId,
            transaction.PaymentId,
            transaction.Id,
            transaction.Status);
    }

    private static async Task<RecordVerifiedProviderPaymentEventResult> ApplySetupCompletedAsync(
        RecordVerifiedProviderPaymentEventCommand command,
        IAtomicWriteAttempt attempt,
        ProviderInboxEvent inbox,
        DateTime now,
        CancellationToken ct)
    {
        if (command.EnrollmentPortfolioId is not int portfolioId ||
            command.EnrollmentLeaseId is not int leaseId ||
            command.EnrollmentTenantId is not int tenantId ||
            string.IsNullOrWhiteSpace(command.ProviderCustomerId) ||
            string.IsNullOrWhiteSpace(command.ProviderPaymentMethodId))
        {
            inbox.FailureKind = ProviderInboxFailureKind.Permanent;
            inbox.DeadLetteredAtUtc = now;
            inbox.LastError = "Verified setup event did not contain complete enrollment facts.";
            await attempt.FlushBusinessAsync(ct);
            return new RecordVerifiedProviderPaymentEventResult(
                RecordProviderPaymentEventOutcome.Unmatched,
                inbox.Id,
                command.EnrollmentPortfolioId,
                null,
                null,
                null);
        }

        var leaseExists = await attempt.Persistence.Query<Lease>()
            .AnyAsync(lease => lease.Id == leaseId &&
                               lease.PortfolioId == portfolioId &&
                               lease.TenantId == tenantId, ct);
        if (!leaseExists)
        {
            inbox.FailureKind = ProviderInboxFailureKind.Permanent;
            inbox.DeadLetteredAtUtc = now;
            inbox.LastError = "Enrollment scope did not match a lease and tenant in the workspace.";
            await attempt.FlushBusinessAsync(ct);
            return new RecordVerifiedProviderPaymentEventResult(
                RecordProviderPaymentEventOutcome.Unmatched,
                inbox.Id,
                portfolioId,
                null,
                null,
                null);
        }

        var enrollment = await attempt.Persistence.Query<AutopayEnrollment>()
            .SingleOrDefaultAsync(candidate => candidate.LeaseId == leaseId && candidate.Active, ct);
        if (enrollment is null)
        {
            enrollment = new AutopayEnrollment
            {
                PortfolioId = portfolioId,
                LeaseId = leaseId,
                TenantId = tenantId,
                CreatedAt = now,
            };
            attempt.Persistence.Add(enrollment);
        }

        enrollment.StripeCustomerId = command.ProviderCustomerId;
        enrollment.StripePaymentMethodId = command.ProviderPaymentMethodId;
        enrollment.Active = true;
        enrollment.UpdatedAt = command.OccurredAtUtc ?? now;
        inbox.PortfolioId = portfolioId;
        inbox.AttemptCount = 1;
        inbox.LastAttemptAtUtc = now;
        inbox.ProcessedAtUtc = now;
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(new AtomicSemanticAudit(
            portfolioId,
            nameof(AutopayEnrollment),
            enrollment.Id,
            AuditLogOperation.Updated,
            NewValues: JsonSerializer.Serialize(new
            {
                enrollment.LeaseId,
                enrollment.TenantId,
                enrollment.Active,
                Provider = command.Provider,
            }),
            ChangeReason: $"Verified {command.Provider} setup event {command.ProviderEventId}."));

        return new RecordVerifiedProviderPaymentEventResult(
            RecordProviderPaymentEventOutcome.Applied,
            inbox.Id,
            portfolioId,
            null,
            null,
            null);
    }

    private static PaymentTransactionStatus MapStatus(ProviderPaymentEventKind kind) => kind switch
    {
        ProviderPaymentEventKind.Pending => PaymentTransactionStatus.Pending,
        ProviderPaymentEventKind.Succeeded => PaymentTransactionStatus.Succeeded,
        ProviderPaymentEventKind.Failed => PaymentTransactionStatus.Failed,
        ProviderPaymentEventKind.Canceled => PaymentTransactionStatus.Canceled,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
