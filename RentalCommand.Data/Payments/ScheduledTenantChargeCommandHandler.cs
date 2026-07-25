using System.Text.Json;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Payments;

/// <summary>
/// Atomic, set-based scheduled tenant billing. PostgreSQL owns every eligibility and duplicate
/// decision; this handler only attaches durable audit/outbox companions to rows returned by the
/// two bounded insert statements.
/// </summary>
public sealed class ApplyScheduledTenantChargeBatchHandler
    : IAtomicCommandHandler<ApplyScheduledTenantChargeBatchCommand, ApplyScheduledTenantChargeBatchResult>
{
    public async Task<ApplyScheduledTenantChargeBatchResult> HandleAsync(
        ApplyScheduledTenantChargeBatchCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.RunToken == Guid.Empty)
            throw new ArgumentException("A scheduled tenant-charge run token is required.");
        if (command.BatchSize is <= 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));
        if (!command.IncludeRentCharges && !command.IncludeLateFeeCharges)
            throw new ArgumentException("At least one scheduled tenant-charge type is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StateLateFeeCapsJson);

        var rent = command.IncludeRentCharges
            ? await attempt.TenantMoney.PostScheduledRentChargesAsync(command.BatchSize, ct)
            : [];
        var lateFees = command.IncludeLateFeeCharges
            ? await attempt.TenantMoney.PostScheduledLateFeesAsync(
                command.BatchSize,
                command.StateLateFeeCapsJson,
                ct)
            : [];

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        foreach (var charge in rent.Concat(lateFees))
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                charge.PortfolioId,
                nameof(TenantAccount),
                charge.TenantAccountId,
                AuditLogOperation.Updated,
                ActorLabel: "system:scheduled-tenant-billing",
                NewValues: JsonSerializer.Serialize(new
                {
                    charge.LedgerEntryId,
                    charge.LeaseAgreementId,
                    charge.EntryType,
                    charge.Amount,
                    charge.EffectiveOn,
                    charge.DueOn,
                    charge.BusinessKey,
                }),
                ChangeReason: charge.EntryType == nameof(TenantLedgerEntryType.RentCharge)
                    ? "Posted scheduled agreement rent charge."
                    : "Posted scheduled late-fee charge."),
                now);
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = charge.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(TenantLedgerEntry),
                    entityId = charge.LedgerEntryId,
                    data = new
                    {
                        charge.TenantAccountId,
                        charge.LeaseAgreementId,
                        charge.EntryType,
                    },
                }),
                IdempotencyKey = OutboxIdempotency.Create(
                    "scheduled-tenant-charge",
                    $"{charge.TenantAccountId}:{charge.BusinessKey}"),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        return new ApplyScheduledTenantChargeBatchResult(rent.Count, lateFees.Count);
    }
}
