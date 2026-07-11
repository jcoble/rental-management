using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;

namespace RentalCommand.Data.Accounting;

public sealed class ConfirmAccountingMappingHandler
    : IAtomicCommandHandler<ConfirmAccountingMappingCommand, ConfirmAccountingMappingResult>
{
    private const int PromotionBatchSize = 256;
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
        if (connection is null)
        {
            return Empty(ConfirmAccountingMappingOutcome.ConnectionNotFound);
        }

        if (!await IsValidTargetAsync(command, attempt, ct))
        {
            return Empty(ConfirmAccountingMappingOutcome.InvalidTarget);
        }

        var mapping = await attempt.Persistence.Query<AccountingEntityMapping>()
            .SingleOrDefaultAsync(row => row.PortfolioId == command.PortfolioId
                && row.AccountingConnectionId == command.AccountingConnectionId
                && row.ExternalType == command.ExternalType
                && row.ExternalId == command.ExternalId, ct);
        var created = mapping is null;
        mapping ??= new AccountingEntityMapping
        {
            PortfolioId = command.PortfolioId,
            AccountingConnectionId = command.AccountingConnectionId,
            ExternalType = command.ExternalType,
            ExternalId = command.ExternalId,
            CreatedAt = command.ConfirmedAtUtc,
        };
        if (created)
        {
            attempt.Persistence.Add(mapping);
        }

        mapping.ExternalDisplayName = command.ExternalDisplayName;
        mapping.LocalEntityType = command.LocalEntityType;
        mapping.LocalEntityId = command.LocalEntityId;
        mapping.LocalEnumValue = command.LocalEnumValue;
        mapping.ConfirmedAt = command.ConfirmedAtUtc;
        mapping.ConfirmedByUserId = command.ConfirmedByUserId;
        mapping.UpdatedAt = command.ConfirmedAtUtc;
        if (!created)
        {
            attempt.BindSemanticAudit(mapping, MappingAudit(command, mapping.Id));
        }

        await attempt.FlushBusinessAsync(ct);
        if (created)
        {
            attempt.StageSemanticEvent(MappingAudit(command, mapping.Id) with
            {
                Operation = AuditLogOperation.Created,
            });
        }

        var paymentIds = new List<int>();
        var expenseIds = new List<int>();
        while (await PromotePaymentBatchAsync(command, attempt, paymentIds, ct) != 0)
        {
        }
        while (await PromoteExpenseBatchAsync(command, attempt, expenseIds, ct) != 0)
        {
        }

        connection.UpdatedAt = command.ConfirmedAtUtc;
        attempt.BindSemanticAudit(connection, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(AccountingConnection),
            connection.Id,
            AuditLogOperation.Updated,
            UserId: command.ConfirmedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                MappingId = mapping.Id,
                PromotedCount = paymentIds.Count + expenseIds.Count,
            }),
            ChangeReason: "Accounting mapping confirmation and parked-transaction promotion committed."));

        await StageConfirmationNoticeAsync(command, attempt, mapping.Id, paymentIds.Count + expenseIds.Count, ct);
        return new ConfirmAccountingMappingResult(
            ConfirmAccountingMappingOutcome.Applied,
            mapping.Id,
            paymentIds.Count + expenseIds.Count,
            paymentIds,
            expenseIds);
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
                .AnyAsync(row => row.Id == tenantId
                    && row.PortfolioId == command.PortfolioId
                    && row.DeletedAt == null, ct);
        }

        if (command.ExternalType == ExternalKind.Vendor
            && command.LocalEntityType == LocalEntityKind.Vendor
            && command.LocalEntityId is { } vendorId
            && command.LocalEnumValue is null)
        {
            return await attempt.Persistence.Query<Vendor>()
                .AnyAsync(row => row.Id == vendorId
                    && row.PortfolioId == command.PortfolioId
                    && row.DeletedAt == null, ct);
        }

        if (command.ExternalType == ExternalKind.Class
            && command.LocalEntityType == LocalEntityKind.Property
            && command.LocalEntityId is { } propertyId
            && command.LocalEnumValue is null)
        {
            return await attempt.Persistence.Query<Property>()
                .AnyAsync(row => row.Id == propertyId
                    && row.PortfolioId == command.PortfolioId
                    && row.DeletedAt == null, ct);
        }

        return command.ExternalType == ExternalKind.Account
            && command.LocalEntityType == LocalEntityKind.ScheduleECategory
            && command.LocalEntityId is null
            && command.LocalEnumValue is { } category
            && Enum.TryParse<ScheduleECategory>(category, out _);
    }

    private static async Task<int> PromotePaymentBatchAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        List<int> promotedIds,
        CancellationToken ct)
    {
        var ledgers = attempt.Persistence.Query<AccountingSyncMap>();
        var parked = attempt.Persistence.Query<AccountingParkedTransaction>();
        var mappings = attempt.Persistence.Query<AccountingEntityMapping>();
        var leases = attempt.Persistence.Query<Lease>();

        var batch = await (
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
            orderby ledger.Id
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
            })
            .Take(PromotionBatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            return 0;
        }

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
                CreatedAt = command.ConfirmedAtUtc,
                UpdatedAt = command.ConfirmedAtUtc,
            },
        }).ToList();
        attempt.Persistence.AddRange(pairs.Select(pair => pair.Payment));
        await attempt.FlushBusinessAsync(ct);

        foreach (var pair in pairs)
        {
            MarkImported(pair.Ledger, LocalEntityKind.Payment, pair.Payment.Id, command.ConfirmedAtUtc);
            promotedIds.Add(pair.Payment.Id);
            attempt.StageSemanticEvent(PromotionAudit(
                command, pair.Ledger, LocalEntityKind.Payment, pair.Payment.Id));
        }
        await attempt.FlushBusinessAsync(ct);
        return pairs.Count;
    }

    private static async Task<int> PromoteExpenseBatchAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        List<int> promotedIds,
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
                VendorId = mappings
                    .Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Vendor
                        && mapping.ExternalId == payload.VendorExternalId
                        && mapping.LocalEntityType == LocalEntityKind.Vendor
                        && mapping.ConfirmedAt != null
                        && mapping.LocalEntityId != null)
                    .Join(vendors.Where(vendor => vendor.PortfolioId == command.PortfolioId && vendor.DeletedAt == null),
                        mapping => mapping.LocalEntityId,
                        vendor => (int?)vendor.Id,
                        (_, vendor) => (int?)vendor.Id)
                    .FirstOrDefault(),
                PropertyId = mappings
                    .Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Class
                        && mapping.ExternalId == payload.ClassExternalId
                        && mapping.LocalEntityType == LocalEntityKind.Property
                        && mapping.ConfirmedAt != null
                        && mapping.LocalEntityId != null)
                    .Join(properties.Where(property => property.PortfolioId == command.PortfolioId && property.DeletedAt == null),
                        mapping => mapping.LocalEntityId,
                        property => (int?)property.Id,
                        (_, property) => (int?)property.Id)
                    .FirstOrDefault(),
                Category = mappings
                    .Where(mapping => mapping.PortfolioId == command.PortfolioId
                        && mapping.AccountingConnectionId == command.AccountingConnectionId
                        && mapping.ExternalType == ExternalKind.Account
                        && mapping.ExternalId == payload.AccountExternalId
                        && mapping.LocalEntityType == LocalEntityKind.ScheduleECategory
                        && mapping.ConfirmedAt != null
                        && mapping.LocalEnumValue != null)
                    .Select(mapping => mapping.LocalEnumValue)
                    .FirstOrDefault(),
            };

        var batch = await candidates
            .Where(row => (row.Category == null || row.Category != nameof(ScheduleECategory.Depreciation))
                && (row.VendorId != null
                    || row.PropertyId != null
                    || (row.Category != null
                        && ImportableCategoryNames.Contains(row.Category)
                        && row.Category != nameof(ScheduleECategory.Other))))
            .OrderBy(row => row.Ledger.Id)
            .Take(PromotionBatchSize)
            .ToListAsync(ct);
        if (batch.Count == 0)
        {
            return 0;
        }

        var pairs = batch.Select(row => new
        {
            row.Ledger,
            Expense = new Expense
            {
                PortfolioId = command.PortfolioId,
                PropertyId = row.PropertyId,
                VendorId = row.VendorId,
                Category = Enum.TryParse<ScheduleECategory>(row.Category, out var category)
                    ? category
                    : ScheduleECategory.Other,
                Status = ExpenseStatus.Paid,
                Amount = row.Amount,
                IncurredAt = row.TxnDateUtc,
                PaidAt = row.TxnDateUtc,
                Description = !string.IsNullOrWhiteSpace(row.ReferenceNumber)
                    ? $"Imported expense {row.ReferenceNumber}"
                    : $"Imported expense {row.ExternalId}",
                CreatedAt = command.ConfirmedAtUtc,
                UpdatedAt = command.ConfirmedAtUtc,
            },
        }).ToList();
        attempt.Persistence.AddRange(pairs.Select(pair => pair.Expense));
        await attempt.FlushBusinessAsync(ct);

        foreach (var pair in pairs)
        {
            MarkImported(pair.Ledger, LocalEntityKind.Expense, pair.Expense.Id, command.ConfirmedAtUtc);
            promotedIds.Add(pair.Expense.Id);
            attempt.StageSemanticEvent(PromotionAudit(
                command, pair.Ledger, LocalEntityKind.Expense, pair.Expense.Id));
        }
        await attempt.FlushBusinessAsync(ct);
        return pairs.Count;
    }

    private static async Task StageConfirmationNoticeAsync(
        ConfirmAccountingMappingCommand command,
        IAtomicWriteAttempt attempt,
        int mappingId,
        int promotedCount,
        CancellationToken ct)
    {
        var notification = new Notification
        {
            PortfolioId = command.PortfolioId,
            UserId = command.ConfirmedByUserId,
            Type = "AccountingMappingConfirmed",
            Title = "Accounting mapping confirmed",
            Message = promotedCount == 1
                ? "1 parked transaction was imported."
                : $"{promotedCount} parked transactions were imported.",
            Severity = "Info",
            ActionUrl = "/settings/accounting",
            RelatedEntityType = nameof(AccountingEntityMapping),
            RelatedEntityId = mappingId,
            CreatedAt = command.ConfirmedAtUtc,
        };
        attempt.Persistence.Add(notification);
        await attempt.FlushBusinessAsync(ct);

        var devices = await attempt.Persistence.Query<DeviceToken>()
            .Where(device => device.PortfolioId == command.PortfolioId
                && device.UserId == command.ConfirmedByUserId)
            .OrderBy(device => device.Id)
            .Select(device => new { device.Id, device.Token })
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
                IdempotencyKey = $"accounting-mapping:{mappingId}:{command.RequestIdentity}:push:{device.Id}",
                CreatedAtUtc = command.ConfirmedAtUtc,
                NextAttemptAtUtc = command.ConfirmedAtUtc,
            });
        }
    }

    private static void MarkImported(
        AccountingSyncMap ledger,
        string localType,
        int localId,
        DateTime occurredAt)
    {
        ledger.Status = LedgerStatus.Imported;
        ledger.LocalEntityType = localType;
        ledger.LocalEntityId = localId;
        ledger.AttemptCount += 1;
        ledger.LastError = null;
        ledger.LastAttemptAt = occurredAt;
        ledger.UpdatedAt = occurredAt;
    }

    private static AtomicSemanticAudit MappingAudit(
        ConfirmAccountingMappingCommand command,
        int mappingId) => new(
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
            }),
            ChangeReason: "External accounting entity mapping confirmed.");

    private static AtomicSemanticAudit PromotionAudit(
        ConfirmAccountingMappingCommand command,
        AccountingSyncMap ledger,
        string localType,
        int localId) => new(
            command.PortfolioId,
            nameof(AccountingSyncMap),
            ledger.Id,
            AuditLogOperation.Updated,
            UserId: command.ConfirmedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                ledger.ExternalType,
                ledger.ExternalId,
                LocalEntityType = localType,
                LocalEntityId = localId,
                Status = LedgerStatus.Imported,
            }),
            ChangeReason: "Parked accounting transaction promoted after mapping confirmation.");

    private static ConfirmAccountingMappingResult Empty(ConfirmAccountingMappingOutcome outcome) =>
        new(outcome, 0, 0, [], []);
}
