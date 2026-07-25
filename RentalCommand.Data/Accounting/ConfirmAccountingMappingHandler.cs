using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Navigation;

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
        var parties = attempt.Persistence.Query<LeaseManagementParty>();
        var managements = attempt.Persistence.Query<LeaseManagement>();
        var accounts = attempt.Persistence.Query<TenantAccount>();
        var depositAccounts = attempt.Persistence.Query<SecurityDepositAccount>();
        var chargeBalances = attempt.Persistence.Query<TenantChargeBalanceProjection>();
        var openAccountLinks = (
            from party in parties
            join management in managements
                on new { party.LeaseManagementId, party.PortfolioId }
                equals new { LeaseManagementId = management.Id, management.PortfolioId }
            join account in accounts
                on new { LeaseManagementId = management.Id, management.PortfolioId }
                equals new { account.LeaseManagementId, account.PortfolioId }
            where party.PortfolioId == command.PortfolioId
                && management.CanceledAtUtc == null
                && account.ClosedAtUtc == null
            select new
            {
                party.TenantId,
                AccountId = account.Id,
                account.Currency,
            }).Distinct();
        var uniqueOpenAccounts =
            from link in openAccountLinks
            group link by link.TenantId into grouped
            where grouped.Count() == 1
            select new
            {
                TenantId = grouped.Key,
                AccountId = grouped.Max(row => row.AccountId),
                Currency = grouped.Max(row => row.Currency),
            };
        var candidates =
            from ledger in ledgers
            join payload in parked on ledger.Id equals payload.Id
            join customer in mappings on payload.CustomerExternalId equals customer.ExternalId
            join account in uniqueOpenAccounts on customer.LocalEntityId equals (int?)account.TenantId
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
                && payload.Amount > 0
            select new
            {
                Ledger = ledger,
                TenantAccountId = account.AccountId,
                account.Currency,
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
                SecurityDepositAccountId = depositAccounts
                    .Where(deposit => deposit.PortfolioId == command.PortfolioId
                        && deposit.TenantAccountId == account.AccountId)
                    .Select(deposit => (int?)deposit.Id)
                    .SingleOrDefault(),
                OpenDepositAmount = chargeBalances
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.TenantAccountId == account.AccountId
                        && balance.EntryType == nameof(TenantLedgerEntryType.DepositCharge)
                        && balance.OpenAmount > 0)
                    .Sum(balance => (decimal?)balance.OpenAmount) ?? 0m,
            };
        var eligible = candidates.Where(row => !row.IsDeposit
            || (row.SecurityDepositAccountId != null && row.OpenDepositAmount >= row.Amount));
        var batch = await eligible
            .OrderBy(row => row.Ledger.Id)
            .Select(row => new
            {
                row.Ledger,
                row.TenantAccountId,
                row.Currency,
                row.Amount,
                row.TxnDateUtc,
                row.PaymentMethod,
                row.ReferenceNumber,
                row.ExternalId,
                row.IsDeposit,
                row.SecurityDepositAccountId,
                HasFollowing = eligible.Any(other => other.Ledger.Id > row.Ledger.Id),
            })
            .Take(take)
            .ToListAsync(ct);
        var hasMore = batch.Count != 0 && batch.Count == take && batch[^1].HasFollowing;

        var pairs = batch.Select(row =>
        {
            var businessKey = ImportedPaymentBusinessKey(command.AccountingConnectionId, row.ExternalId);
            var paymentAttempt = new TenantPaymentAttempt
            {
                PortfolioId = command.PortfolioId,
                TenantAccountId = row.TenantAccountId,
                Provider = $"Accounting:{command.AccountingConnectionId}",
                ProviderObjectId = row.ExternalId,
                IdempotencyKey = businessKey,
                AttemptType = TenantPaymentAttemptType.Charge,
                State = TenantPaymentAttemptState.Succeeded,
                Amount = row.Amount,
                Currency = row.Currency,
                PaymentMethodSummary = row.PaymentMethod,
                PreparedAtUtc = row.TxnDateUtc,
                SubmittedAtUtc = row.TxnDateUtc,
                SettledAtUtc = row.TxnDateUtc,
                UpdatedAtUtc = command.OccurredAtUtc,
                CreatedByUserId = command.UserId,
            };
            var receipt = new TenantLedgerEntry
            {
                PortfolioId = command.PortfolioId,
                TenantAccountId = row.TenantAccountId,
                EntryType = TenantLedgerEntryType.PaymentReceipt,
                Direction = TenantLedgerDirection.Credit,
                Amount = row.Amount,
                Currency = row.Currency,
                EffectiveOn = DateOnly.FromDateTime(row.TxnDateUtc),
                PostedAtUtc = command.OccurredAtUtc,
                Description = ImportedPaymentDescription(row.ReferenceNumber, row.ExternalId),
                BusinessKey = businessKey,
                ProviderPaymentAttempt = paymentAttempt,
                CreatedByUserId = command.UserId,
            };
            SecurityDepositEntry? deposit = null;
            if (row.IsDeposit && row.SecurityDepositAccountId is { } depositAccountId)
            {
                deposit = new SecurityDepositEntry
                {
                    PortfolioId = command.PortfolioId,
                    SecurityDepositAccountId = depositAccountId,
                    EntryType = SecurityDepositEntryType.Receipt,
                    Direction = SecurityDepositDirection.Increase,
                    Amount = row.Amount,
                    Currency = row.Currency,
                    EffectiveOn = receipt.EffectiveOn,
                    PostedAtUtc = command.OccurredAtUtc,
                    BusinessKey = $"{businessKey}:deposit",
                    Description = receipt.Description,
                    TenantLedgerEntry = receipt,
                    CreatedByUserId = command.UserId,
                };
            }
            return new { row.Ledger, Receipt = receipt, PaymentAttempt = paymentAttempt, Deposit = deposit };
        }).ToList();
        attempt.Persistence.AddRange(pairs.Select(pair => pair.PaymentAttempt));
        attempt.Persistence.AddRange(pairs.Select(pair => pair.Receipt));
        attempt.Persistence.AddRange(pairs.Where(pair => pair.Deposit is not null)
            .Select(pair => pair.Deposit!));
        if (pairs.Count != 0) await attempt.FlushBusinessAsync(ct);
        if (pairs.Count != 0)
        {
            await attempt.TenantMoney.AllocateImportedReceiptsAsync(
                pairs.Select(pair => pair.Receipt.Id).ToArray(), command.OccurredAtUtc, ct);
        }
        foreach (var pair in pairs)
        {
            MarkImported(pair.Ledger, LocalEntityKind.TenantLedgerEntry, pair.Receipt.Id, command.OccurredAtUtc);
            attempt.StageSemanticEvent(PromotionAudit(
                command, pair.Ledger, LocalEntityKind.TenantLedgerEntry, pair.Receipt.Id));
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
        var accessContext = await attempt.Persistence.Query<WorkspaceAccessContext>()
            .AsNoTracking()
            .Where(context => context.PortfolioId == command.PortfolioId
                && context.UserId == command.ConfirmedByUserId
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            .OrderByDescending(context => context.UpdatedAtUtc)
            .Select(context => new { AccessContextId = context.Id, context.AccessRevision })
            .FirstOrDefaultAsync(ct);
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
            RelatedEntityType = nameof(AccountingEntityMapping),
            RelatedEntityId = mappingId,
            CreatedAt = command.ConfirmedAtUtc,
        };
        if (accessContext is not null)
        {
            notification.NavigationExperience = NavigationExperience.Management;
            notification.NavigationDestination = NavigationDestination.Money;
            notification.NavigationAccessContextId = accessContext.AccessContextId;
            notification.NavigationAccessRevision = accessContext.AccessRevision;
            notification.NavigationAction = NavigationAction.Review;
            notification.NavigationExpiresAtUtc = command.ConfirmedAtUtc.AddDays(7);
            notification.NavigationFallbackDestination = NavigationDestination.Home;
        }
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
                    navigationIntent = accessContext is null ? null : new
                    {
                        experience = NavigationExperience.Management.ToString(),
                        destination = NavigationDestination.Money.ToString(),
                        accessContextId = accessContext.AccessContextId,
                        accessRevision = accessContext.AccessRevision,
                        resource = (object?)null,
                        parentResource = (object?)null,
                        childResource = (object?)null,
                        action = NavigationAction.Review.ToString(),
                        expiresAtUtc = command.ConfirmedAtUtc.AddDays(7),
                        fallbackDestination = NavigationDestination.Home.ToString(),
                    },
                }),
                IdempotencyKey = $"accounting-mapping:{mappingId}:{command.ClientOperationId}:push:{device.Id}",
                CreatedAtUtc = command.ConfirmedAtUtc,
                NextAttemptAtUtc = command.ConfirmedAtUtc,
            });
        }
    }

    private static void MarkImported(AccountingSyncMap ledger, string localType, long localId, DateTime occurredAt)
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
        if (persistedAt > DateTime.MaxValue.AddTicks(-TimeSpan.TicksPerMicrosecond))
            throw new InvalidOperationException("A confirmation timestamp cannot advance beyond DateTime.MaxValue.");
        // PostgreSQL timestamp values have microsecond precision. A single .NET tick is
        // only 100 nanoseconds and is lost when the value is persisted, so advance by
        // the smallest increment both stores can represent.
        return persistedAt.AddTicks(TimeSpan.TicksPerMicrosecond);
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
        long localId) => new(
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

    private static string ImportedPaymentBusinessKey(int connectionId, string externalId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalId)));
        return $"accounting-import:{connectionId}:{digest}";
    }

    private static string ImportedPaymentDescription(string? referenceNumber, string externalId)
    {
        var description = !string.IsNullOrWhiteSpace(referenceNumber)
            ? $"Imported payment {referenceNumber}"
            : $"Imported payment {externalId}";
        return description.Length <= 500 ? description : description[..500];
    }

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
