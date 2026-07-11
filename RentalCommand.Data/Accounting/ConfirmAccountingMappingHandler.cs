using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

internal sealed record AccountingPromotionContext(
    int PortfolioId,
    int AccountingConnectionId,
    int UserId,
    string ClientOperationId,
    DateTime OccurredAtUtc);

internal sealed record AccountingPromotionBatchResult(int PromotedCount, bool HasMore);

public sealed class ConfirmAccountingMappingHandler
    : IAtomicCommandHandler<ConfirmAccountingMappingCommand, ConfirmAccountingMappingResult>
{
    internal const int PromotionBatchSize = 256;
    private const int MaxPushDevices = 32;
    private static readonly LeaseStatus[] LiveLeaseStatuses = [LeaseStatus.Active, LeaseStatus.NoticeGiven];
    private static readonly string[] ImportableCategoryNames = Enum.GetNames<ScheduleECategory>()
        .Where(name => name != nameof(ScheduleECategory.Depreciation))
        .ToArray();

    public async Task<ConfirmAccountingMappingResult> HandleAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AccountingConnection,
            command.AccountingConnectionId,
            ct);

        var connection = await attempt.Persistence.Query<AccountingConnection>()
            .SingleOrDefaultAsync(row => row.Id == command.AccountingConnectionId
                && row.PortfolioId == command.PortfolioId
                && row.Provider == command.Provider, ct);
        if (connection is null) return Empty(ConfirmAccountingMappingOutcome.ConnectionNotFound);
        if (!await IsValidTargetAsync(command, attempt, ct))
            return Empty(ConfirmAccountingMappingOutcome.InvalidTarget);

        var mapping = await attempt.Persistence.Query<AccountingEntityMapping>()
            .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                && row.AccountingConnectionId == command.AccountingConnectionId
                && row.ExternalType == command.ExternalType
                && row.ExternalId == command.ExternalId, ct);
        if ((mapping?.Revision ?? 0) != command.ExpectedRevision)
            return Empty(ConfirmAccountingMappingOutcome.StaleRevision, mapping);

        var created = mapping is null;
        mapping ??= new AccountingEntityMapping
        {
            PortfolioId = command.PortfolioId,
            AccountingConnectionId = command.AccountingConnectionId,
            ExternalType = command.ExternalType,
            ExternalId = command.ExternalId,
            CreatedAt = command.ConfirmedAtUtc,
        };
        if (created) attempt.Persistence.Add(mapping);

        var confirmationTimestamp = created
            ? command.ConfirmedAtUtc
            : NextMutationTimestamp(command.ConfirmedAtUtc, mapping.UpdatedAt);
        mapping.ExternalDisplayName = command.ExternalDisplayName;
        mapping.LocalEntityType = command.LocalEntityType;
        mapping.LocalEntityId = command.LocalEntityId;
        mapping.LocalEnumValue = command.LocalEnumValue;
        mapping.ConfirmedAt = confirmationTimestamp;
        mapping.ConfirmedByUserId = command.ConfirmedByUserId;
        mapping.UpdatedAt = confirmationTimestamp;
        mapping.Revision++;
        var mappingAudit = MappingAudit(command, created ? 0 : mapping.Id, mapping.Revision, confirmationTimestamp);
        attempt.BindSemanticAudit(mapping, created
            ? mappingAudit with { Operation = AuditLogOperation.Created }
            : mappingAudit);

        await attempt.FlushBusinessAsync(ct);

        var context = new AccountingPromotionContext(
            command.PortfolioId,
            command.AccountingConnectionId,
            command.ConfirmedByUserId,
            command.ClientOperationId,
            command.ConfirmedAtUtc);
        var promotion = await PromoteOneBatchAsync(context, attempt, ct);
        var promoted = promotion.PromotedCount;
        var hasMore = promotion.HasMore;
        AccountingMappingPromotionJob? continuation = null;
        if (hasMore)
        {
            continuation = new AccountingMappingPromotionJob
            {
                Id = Guid.NewGuid(),
                PortfolioId = command.PortfolioId,
                AccountingConnectionId = command.AccountingConnectionId,
                AccountingEntityMappingId = mapping.Id,
                MappingRevision = mapping.Revision,
                PromotedCount = promoted,
                CreatedAtUtc = command.ConfirmedAtUtc,
            };
            attempt.Persistence.Add(continuation);
        }

        connection.UpdatedAt = NextMutationTimestamp(command.ConfirmedAtUtc, connection.UpdatedAt);
        attempt.BindSemanticAudit(connection, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AccountingConnection),
            connection.Id,
            AuditLogOperation.Updated,
            UserId: command.ConfirmedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                MappingId = mapping.Id,
                MappingRevision = mapping.Revision,
                PromotedCount = promoted,
                ContinuationId = continuation?.Id,
                ConfirmedAt = connection.UpdatedAt,
                command.ClientOperationId,
            }),
            ChangeReason: "Accounting mapping confirmation and one bounded parked-transaction batch committed."));

        await StageConfirmationNoticeAsync(command, attempt, mapping.Id, promoted, hasMore, ct);
        return new ConfirmAccountingMappingResult(
            ConfirmAccountingMappingOutcome.Applied,
            mapping.Id,
            mapping.Revision,
            promoted,
            continuation?.Id,
            hasMore);
    }

    private static async Task<bool> IsValidTargetAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (command.ExternalType == ExternalKind.Customer
            && command.LocalEntityType == LocalEntityKind.Tenant
            && command.LocalEntityId is { } tenantId
            && command.LocalEnumValue is null)
        {
            return await attempt.Persistence.Query<Tenant>()
                .AnyAsync(row => row.Id == tenantId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        }
        if (command.ExternalType == ExternalKind.Vendor
            && command.LocalEntityType == LocalEntityKind.Vendor
            && command.LocalEntityId is { } vendorId
            && command.LocalEnumValue is null)
        {
            return await attempt.Persistence.Query<Vendor>()
                .AnyAsync(row => row.Id == vendorId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        }
        if (command.ExternalType == ExternalKind.Class
            && command.LocalEntityType == LocalEntityKind.Property
            && command.LocalEntityId is { } propertyId
            && command.LocalEnumValue is null)
        {
            return await attempt.Persistence.Query<Property>()
                .AnyAsync(row => row.Id == propertyId && row.PortfolioId == command.PortfolioId && row.DeletedAt == null, ct);
        }
        return command.ExternalType == ExternalKind.Account
            && command.LocalEntityType == LocalEntityKind.ScheduleECategory
            && command.LocalEntityId is null
            && command.LocalEnumValue is { } category
            && Enum.TryParse<ScheduleECategory>(category, out _);
    }

    internal static async Task<AccountingPromotionBatchResult> PromoteOneBatchAsync(
        AccountingPromotionContext command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var payments = await PromotePaymentBatchAsync(command, attempt, PromotionBatchSize, ct);
        var remaining = PromotionBatchSize - payments.PromotedCount;
        var expenses = await PromoteExpenseBatchAsync(command, attempt, remaining, ct);
        return new AccountingPromotionBatchResult(
            payments.PromotedCount + expenses.PromotedCount,
            payments.HasMore || expenses.HasMore);
    }

    private static async Task<AccountingPromotionBatchResult> PromotePaymentBatchAsync(
        AccountingPromotionContext command,
        IAtomicWriteAttempt attempt,
        int take,
        CancellationToken ct)
    {
        var ledgers = attempt.Persistence.Query<AccountingSyncMap>();
        var parked = attempt.Persistence.Query<AccountingParkedTransaction>();
        var mappings = attempt.Persistence.Query<AccountingEntityMapping>();
        var leases = attempt.Persistence.Query<Lease>();
        var candidates =
            from ledger in ledgers
            join payload in parked on ledger.Id equals payload.Id
            join customer in mappings on payload.CustomerExternalId equals customer.ExternalId
            join lease in leases on customer.LocalEntityId equals (int?)lease.TenantId
            where ledger.PortfolioId == command.PortfolioId
                && ledger.AccountingConnectionId == command.AccountingConnectionId
                && ledger.Direction == LedgerDirection.Import
                && ledger.ExternalType == ExternalKind.Payment
                && (ledger.Status == LedgerStatus.NeedsReview || ledger.Status == LedgerStatus.Unmatched)
                && ledger.LocalEntityId == null
                && customer.PortfolioId == command.PortfolioId
                && customer.AccountingConnectionId == command.AccountingConnectionId
                && customer.ExternalType == ExternalKind.Customer
                && customer.LocalEntityType == LocalEntityKind.Tenant
                && customer.ConfirmedAt != null
                && customer.LocalEntityId != null
                && lease.PortfolioId == command.PortfolioId
                && lease.DeletedAt == null
                && LiveLeaseStatuses.Contains(lease.Status)
                && !leases.Any(other => other.PortfolioId == command.PortfolioId
                    && other.DeletedAt == null
                    && other.TenantId == lease.TenantId
                    && LiveLeaseStatuses.Contains(other.Status)
                    && (other.StartDate > lease.StartDate
                        || (other.StartDate == lease.StartDate && other.Id > lease.Id)))
            select new
            {
                Ledger = ledger,
                LeaseId = lease.Id,
                payload.Amount,
                payload.TxnDateUtc,
                payload.PaymentMethod,
                payload.ReferenceNumber,
                payload.ExternalId,
                IsDeposit = mappings.Any(account => account.PortfolioId == command.PortfolioId
                    && account.AccountingConnectionId == command.AccountingConnectionId
                    && account.ExternalType == ExternalKind.Account
                    && account.ExternalId == payload.DepositAccountExternalId
                    && account.ExternalDisplayName != null
                    && (account.ExternalDisplayName.ToLower().Contains("security deposit")
                        || account.ExternalDisplayName.ToLower().Contains("deposit held")
                        || account.ExternalDisplayName.ToLower().Contains("tenant deposit"))),
            };
        var batch = await candidates
            .OrderBy(row => row.Ledger.Id)
            .Select(row => new
            {
                row.Ledger,
                row.LeaseId,
                row.Amount,
                row.TxnDateUtc,
                row.PaymentMethod,
                row.ReferenceNumber,
                row.ExternalId,
                row.IsDeposit,
                HasFollowing = candidates.Any(other => other.Ledger.Id > row.Ledger.Id),
            })
            .Take(take)
            .ToListAsync(ct);
        var hasMore = batch.Count == take && batch[^1].HasFollowing;

        var pairs = batch.Select(row => new
        {
            row.Ledger,
            Payment = new Payment
            {
                PortfolioId = command.PortfolioId,
                LeaseId = row.LeaseId,
                PaymentType = row.IsDeposit ? PaymentType.SecurityDeposit : PaymentType.Rent,
                Status = PaymentStatus.Paid,
                Amount = row.Amount,
                DueDate = row.TxnDateUtc,
                PaidDate = row.TxnDateUtc,
                Method = row.PaymentMethod,
                ExternalReference = row.ReferenceNumber ?? row.ExternalId,
                CreatedAt = command.OccurredAtUtc,
                UpdatedAt = command.OccurredAtUtc,
            },
        }).ToList();
        attempt.Persistence.AddRange(pairs.Select(pair => pair.Payment));
        if (pairs.Count != 0) await attempt.FlushBusinessAsync(ct);
        foreach (var pair in pairs)
        {
            MarkImported(pair.Ledger, LocalEntityKind.Payment, pair.Payment.Id, command.OccurredAtUtc);
            attempt.StageSemanticEvent(PromotionAudit(command, pair.Ledger, LocalEntityKind.Payment, pair.Payment.Id));
        }
        if (pairs.Count != 0) await attempt.FlushBusinessAsync(ct);
        return new AccountingPromotionBatchResult(pairs.Count, hasMore);
    }

    private static async Task<AccountingPromotionBatchResult> PromoteExpenseBatchAsync(
        AccountingPromotionContext command,
        IAtomicWriteAttempt attempt,
        int take,
        CancellationToken ct)
    {
        var ledgers = attempt.Persistence.Query<AccountingSyncMap>();
        var parked = attempt.Persistence.Query<AccountingParkedTransaction>();
        var mappings = attempt.Persistence.Query<AccountingEntityMapping>();
        var vendors = attempt.Persistence.Query<Vendor>();
        var properties = attempt.Persistence.Query<Property>();
        var candidates =
            from ledger in ledgers
            join payload in parked on ledger.Id equals payload.Id
            where ledger.PortfolioId == command.PortfolioId
                && ledger.AccountingConnectionId == command.AccountingConnectionId
                && ledger.Direction == LedgerDirection.Import
                && (ledger.ExternalType == ExternalKind.Purchase || ledger.ExternalType == ExternalKind.Bill)
                && (ledger.Status == LedgerStatus.NeedsReview || ledger.Status == LedgerStatus.Unmatched)
                && ledger.LocalEntityId == null
            select new
            {
                Ledger = ledger,
                payload.Amount,
                payload.TxnDateUtc,
                payload.ReferenceNumber,
                payload.ExternalId,
                VendorId = mappings.Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Vendor
                        && mapping.ExternalId == payload.VendorExternalId
                        && mapping.LocalEntityType == LocalEntityKind.Vendor
                        && mapping.ConfirmedAt != null && mapping.LocalEntityId != null)
                    .Join(vendors.Where(vendor => vendor.PortfolioId == command.PortfolioId && vendor.DeletedAt == null),
                        mapping => mapping.LocalEntityId, vendor => (int?)vendor.Id, (_, vendor) => (int?)vendor.Id)
                    .FirstOrDefault(),
                PropertyId = mappings.Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Class
                        && mapping.ExternalId == payload.ClassExternalId
                        && mapping.LocalEntityType == LocalEntityKind.Property
                        && mapping.ConfirmedAt != null && mapping.LocalEntityId != null)
                    .Join(properties.Where(property => property.PortfolioId == command.PortfolioId && property.DeletedAt == null),
                        mapping => mapping.LocalEntityId, property => (int?)property.Id, (_, property) => (int?)property.Id)
                    .FirstOrDefault(),
                Category = mappings.Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Account
                        && mapping.ExternalId == payload.AccountExternalId
                        && mapping.LocalEntityType == LocalEntityKind.ScheduleECategory
                        && mapping.ConfirmedAt != null && mapping.LocalEnumValue != null)
                    .Select(mapping => mapping.LocalEnumValue)
                    .FirstOrDefault(),
            };
        var eligible = candidates.Where(row => (row.Category == null || row.Category != nameof(ScheduleECategory.Depreciation))
                && (row.VendorId != null || row.PropertyId != null
                    || (row.Category != null && ImportableCategoryNames.Contains(row.Category)
                        && row.Category != nameof(ScheduleECategory.Other))));
        var queryTake = take == 0 ? 1 : take;
        var batch = await eligible
            .OrderBy(row => row.Ledger.Id)
            .Select(row => new
            {
                row.Ledger,
                row.Amount,
                row.TxnDateUtc,
                row.ReferenceNumber,
                row.ExternalId,
                row.VendorId,
                row.PropertyId,
                row.Category,
                HasFollowing = eligible.Any(other => other.Ledger.Id > row.Ledger.Id),
            })
            .Take(queryTake)
            .ToListAsync(ct);
        if (take == 0)
        {
            return new AccountingPromotionBatchResult(0, batch.Count != 0);
        }
        var hasMore = batch.Count == take && batch[^1].HasFollowing;
        var pairs = batch.Select(row => new
        {
            row.Ledger,
            Expense = new Expense
            {
                PortfolioId = command.PortfolioId,
                PropertyId = row.PropertyId,
                VendorId = row.VendorId,
                Category = Enum.TryParse<ScheduleECategory>(row.Category, out var category) ? category : ScheduleECategory.Other,
                Status = ExpenseStatus.Paid,
                Amount = row.Amount,
                IncurredAt = row.TxnDateUtc,
                PaidAt = row.TxnDateUtc,
                Description = !string.IsNullOrWhiteSpace(row.ReferenceNumber)
                    ? $"Imported expense {row.ReferenceNumber}" : $"Imported expense {row.ExternalId}",
                CreatedAt = command.OccurredAtUtc,
                UpdatedAt = command.OccurredAtUtc,
            },
        }).ToList();
        attempt.Persistence.AddRange(pairs.Select(pair => pair.Expense));
        if (pairs.Count != 0) await attempt.FlushBusinessAsync(ct);
        foreach (var pair in pairs)
        {
            MarkImported(pair.Ledger, LocalEntityKind.Expense, pair.Expense.Id, command.OccurredAtUtc);
            attempt.StageSemanticEvent(PromotionAudit(command, pair.Ledger, LocalEntityKind.Expense, pair.Expense.Id));
        }
        if (pairs.Count != 0) await attempt.FlushBusinessAsync(ct);
        return new AccountingPromotionBatchResult(pairs.Count, hasMore);
    }

    private static async Task StageConfirmationNoticeAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        int mappingId,
        int promotedCount,
        bool hasMore,
        CancellationToken ct)
    {
        var notification = new Notification
        {
            PortfolioId = command.PortfolioId,
            UserId = command.ConfirmedByUserId,
            Type = "AccountingMappingConfirmed",
            Title = "Accounting mapping confirmed",
            Message = hasMore
                ? $"{promotedCount} parked transactions were imported; more are queued."
                : promotedCount == 1 ? "1 parked transaction was imported." : $"{promotedCount} parked transactions were imported.",
            Severity = "Info",
            ActionUrl = "/settings/accounting",
            RelatedEntityType = nameof(AccountingEntityMapping),
            RelatedEntityId = mappingId,
            CreatedAt = command.ConfirmedAtUtc,
        };
        attempt.Persistence.Add(notification);
        await attempt.FlushBusinessAsync(ct);
        var devices = await attempt.Persistence.Query<DeviceToken>()
            .Where(device => device.PortfolioId == command.PortfolioId && device.UserId == command.ConfirmedByUserId)
            .OrderBy(device => device.Id)
            .Select(device => new { device.Id, device.Token })
            .Take(MaxPushDevices)
            .ToListAsync(ct);
        foreach (var device in devices)
        {
            attempt.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "push",
                Payload = JsonSerializer.Serialize(new
                {
                    deviceToken = device.Token,
                    title = notification.Title,
                    body = notification.Message,
                    actionUrl = notification.ActionUrl,
                    type = notification.Type,
                    relatedEntityType = notification.RelatedEntityType,
                    relatedEntityId = mappingId.ToString(),
                }),
                IdempotencyKey = $"accounting-mapping:{mappingId}:{command.ClientOperationId}:push:{device.Id}",
                CreatedAtUtc = command.ConfirmedAtUtc,
                NextAttemptAtUtc = command.ConfirmedAtUtc,
            });
        }
    }

    private static void MarkImported(AccountingSyncMap ledger, string localType, int localId, DateTime occurredAt)
    {
        ledger.Status = LedgerStatus.Imported;
        ledger.LocalEntityType = localType;
        ledger.LocalEntityId = localId;
        ledger.AttemptCount += 1;
        ledger.LastError = null;
        ledger.LastAttemptAt = occurredAt;
        ledger.UpdatedAt = occurredAt;
    }

    private static DateTime NextMutationTimestamp(DateTime requestedAt, DateTime persistedAt)
    {
        if (requestedAt > persistedAt) return requestedAt;
        if (persistedAt == DateTime.MaxValue)
            throw new InvalidOperationException("A confirmation timestamp cannot advance beyond DateTime.MaxValue.");
        return persistedAt.AddTicks(1);
    }

    private static AtomicSemanticAudit MappingAudit(
        ConfirmAccountingMappingCommand command,
        int mappingId,
        long revision,
        DateTime confirmationTimestamp) => new(
            command.PortfolioId,
            nameof(AccountingEntityMapping),
            mappingId,
            AuditLogOperation.Updated,
            UserId: command.ConfirmedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.ExternalType,
                command.ExternalId,
                command.LocalEntityType,
                command.LocalEntityId,
                command.LocalEnumValue,
                Revision = revision,
                ConfirmedAt = confirmationTimestamp,
                command.ClientOperationId,
            }),
            ChangeReason: "External accounting entity mapping confirmed.");

    private static AtomicSemanticAudit PromotionAudit(
        AccountingPromotionContext command,
        AccountingSyncMap ledger,
        string localType,
        int localId) => new(
            command.PortfolioId,
            nameof(AccountingSyncMap),
            ledger.Id,
            AuditLogOperation.Updated,
            UserId: command.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                ledger.ExternalType,
                ledger.ExternalId,
                LocalEntityType = localType,
                LocalEntityId = localId,
                Status = LedgerStatus.Imported,
                command.ClientOperationId,
            }),
            ChangeReason: "Parked accounting transaction promoted after mapping confirmation.");

    private static ConfirmAccountingMappingResult Empty(
        ConfirmAccountingMappingOutcome outcome,
        AccountingEntityMapping? mapping = null) =>
        new(outcome, mapping?.Id ?? 0, mapping?.Revision ?? 0, 0, null, false);
}

public sealed class ContinueAccountingMappingPromotionHandler
    : IAtomicCommandHandler<ContinueAccountingMappingPromotionCommand, ContinueAccountingMappingPromotionResult>
{
    public async Task<ContinueAccountingMappingPromotionResult> HandleAsync(
        ContinueAccountingMappingPromotionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.AccountingConnection,
            command.AccountingConnectionId,
            ct);
        var job = await attempt.Persistence.Query<AccountingMappingPromotionJob>()
            .SingleOrDefaultAsync(row => row.Id == command.ContinuationId
                && row.PortfolioId == command.PortfolioId
                && row.AccountingConnectionId == command.AccountingConnectionId, ct);
        if (job is null) return Result(ContinueAccountingMappingPromotionOutcome.NotFound, command, 0, 0, false);
        if (job.CompletedAtUtc is not null)
            return Result(ContinueAccountingMappingPromotionOutcome.Completed, command, 0, job.PromotedCount, false);

        var currentRevision = await attempt.Persistence.Query<AccountingEntityMapping>()
            .Where(row => row.Id == job.AccountingEntityMappingId && row.PortfolioId == command.PortfolioId)
            .Select(row => (long?)row.Revision)
            .SingleOrDefaultAsync(ct);
        if (currentRevision != job.MappingRevision)
        {
            job.CompletedAtUtc = command.AppliedAtUtc;
            return Result(ContinueAccountingMappingPromotionOutcome.Superseded, command, 0, job.PromotedCount, false);
        }

        var context = new AccountingPromotionContext(
            command.PortfolioId,
            command.AccountingConnectionId,
            command.RequestedByUserId,
            command.ClientOperationId,
            command.AppliedAtUtc);
        var promotion = await ConfirmAccountingMappingHandler.PromoteOneBatchAsync(context, attempt, ct);
        var promoted = promotion.PromotedCount;
        job.PromotedCount += promoted;
        var hasMore = promotion.HasMore;
        if (!hasMore) job.CompletedAtUtc = command.AppliedAtUtc;
        return Result(ContinueAccountingMappingPromotionOutcome.Applied, command, promoted, job.PromotedCount, hasMore);
    }

    private static ContinueAccountingMappingPromotionResult Result(
        ContinueAccountingMappingPromotionOutcome outcome,
        ContinueAccountingMappingPromotionCommand command,
        int promoted,
        int total,
        bool hasMore) => new(outcome, command.ContinuationId, promoted, total, hasMore);
}
