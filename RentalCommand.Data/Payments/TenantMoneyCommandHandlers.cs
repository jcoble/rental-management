using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Accounting;

namespace RentalCommand.Data.Payments;

public sealed class RecordTenantReceiptHandler
    : IAtomicCommandHandler<RecordTenantReceiptCommand, RecordTenantReceiptResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordTenantReceiptHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordTenantReceiptResult> HandleAsync(
        RecordTenantReceiptCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport.AuthorizedAccounts(command, _db, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
                TargetOpenAmount = command.TargetChargeEntryId == null
                    ? null
                    : _db.Set<TenantChargeBalanceProjection>()
                        .Where(balance =>
                            balance.PortfolioId == command.PortfolioId
                            && balance.TenantAccountId == row.Id
                            && balance.TenantLedgerEntryId == command.TargetChargeEntryId.Value
                            && balance.OpenAmount > 0)
                        .Select(balance => (decimal?)balance.OpenAmount)
                        .SingleOrDefault(),
                TargetEntryType = command.TargetChargeEntryId == null
                    ? (TenantLedgerEntryType?)null
                    : _db.Set<TenantLedgerEntry>()
                        .Where(entry => entry.PortfolioId == command.PortfolioId
                            && entry.TenantAccountId == row.Id
                            && entry.Id == command.TargetChargeEntryId.Value)
                        .Select(entry => (TenantLedgerEntryType?)entry.EntryType)
                        .SingleOrDefault(),
                ManualProviderObjectWasFullyRefunded =
                    !string.IsNullOrWhiteSpace(command.ExternalReference)
                    && _db.Set<TenantPaymentAttempt>().Any(original =>
                        original.PortfolioId == command.PortfolioId
                        && original.TenantAccountId == row.Id
                        && original.Provider == "manual"
                        && original.ProviderObjectId == command.ExternalReference.Trim()
                        && (original.AttemptType == TenantPaymentAttemptType.Charge
                            || original.AttemptType == TenantPaymentAttemptType.UnappliedReceipt)
                        && original.State == TenantPaymentAttemptState.Succeeded
                        && _db.Set<TenantPaymentAttempt>().Any(refund =>
                            refund.PortfolioId == command.PortfolioId
                            && refund.TenantAccountId == row.Id
                            && refund.Provider == "manual"
                            && refund.RefundsPaymentAttemptId == original.Id
                            && refund.AttemptType == TenantPaymentAttemptType.Refund
                            && refund.State == TenantPaymentAttemptState.Succeeded
                            && refund.Amount == original.Amount
                            && refund.Currency == original.Currency)),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Receipt source provenance must belong to the current portfolio.");
        if (command.TargetChargeEntryId is not null && account.TargetOpenAmount is null)
            throw new ArgumentException(
                "Receipt target charge must be an open debit on the selected tenant account.");
        if (account.TargetEntryType == TenantLedgerEntryType.DepositCharge
            && account.TargetOpenAmount is { } targetOpenAmount
            && command.Amount > targetOpenAmount)
        {
            // The receipt journal has one cash leg. Allocation currently does not return a
            // split-cash accounting result, so accepting an overage here would misclassify the
            // non-deposit portion as trust cash. Reject before any payment, allocation, or journal
            // row is written instead of manufacturing a mixed cash posting.
            throw new ArgumentException(
                "A targeted deposit receipt cannot exceed the deposit target open amount.");
        }

        var recordedAtUtc = TenantMoneyCommandSupport.CommandTimestamp(command.RecordedAtUtc, times.WallClockUtc);
        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, account.Currency, command.Amount, command.PaymentMethodSummary,
            account.ManualProviderObjectWasFullyRefunded
                ? command.BusinessKey
                : command.ExternalReference,
            command.PayerName, command.CheckNumber, command.BankName,
            command.TargetChargeEntryId is null
                ? TenantPaymentAttemptType.UnappliedReceipt
                : TenantPaymentAttemptType.Charge,
            command.TargetChargeEntryId,
            recordedAtUtc);
        _db.Add(paymentAttempt);
        await context.FlushBusinessAsync(ct);

        var receipt = new TenantLedgerEntry
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = command.Amount,
            Currency = account.Currency,
            EffectiveOn = command.EffectiveOn,
            PostedAtUtc = recordedAtUtc,
            Description = command.Description.Trim(),
            BusinessKey = command.BusinessKey,
            ProviderPaymentAttemptId = paymentAttempt.Id,
            SourceStoredFileId = command.SourceStoredFileId,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(receipt);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantReceiptAsync(
            _db, context, receipt, command.ActorUserId,
            account.TargetEntryType == TenantLedgerEntryType.DepositCharge
                ? "security-deposit-trust-cash"
                : "operating-cash",
            ct);

        var allocations = command.AllocateOldestCharges
            ? await TenantMoneyCommandSupport.AllocateOldestAsync(
                _db, command.PortfolioId, command.TenantAccountId, receipt.Id,
                command.Amount, command.BusinessKey, command.ActorUserId, recordedAtUtc,
                context, ct)
            : command.TargetChargeEntryId is null
                ? new TenantMoneyAllocationSummary()
                : await TenantMoneyCommandSupport.AllocateTargetChargeAsync(
                _db, command.PortfolioId, command.TenantAccountId, receipt.Id,
                command.TargetChargeEntryId.Value, command.Amount, command.BusinessKey,
                command.ActorUserId, recordedAtUtc, context, ct);

        TenantMoneyCommandSupport.StageMutation(
            context, command, recordedAtUtc, nameof(TenantLedgerEntry), receipt.Id,
            "Tenant payment receipt recorded", new { receipt.Amount, receipt.EffectiveOn, allocations = allocations.AllocationCount });
        var notificationCreatedAtUtc = TenantReceiptNotificationStaging.TenantFacingEventAtUtc(times.BusinessDate);
        var notificationExpiresAtUtc = TenantReceiptNotificationStaging.TenantFacingExpirationUtc(
            command.EffectiveOn,
            times.BusinessDate);
        await TenantReceiptNotificationStaging.StageAsync(
            _db,
            context,
            receipt.Id,
            recordedAtUtc,
            notificationCreatedAtUtc,
            notificationExpiresAtUtc,
            ct);
        return new RecordTenantReceiptResult(true, account.Id, receipt.Id, paymentAttempt.Id,
            receipt.Amount, allocations.AllocatedAmount, allocations.AllocationCount);
    }

    public Task AuthorizeReplayAsync(RecordTenantReceiptCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

internal static class TenantReceiptNotificationStaging
{
    public static async Task StageAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long ledgerEntryId,
        DateTime now,
        DateTime notificationCreatedAtUtc,
        DateTime notificationExpiresAtUtc,
        CancellationToken ct)
    {
        var recipientRows = await (
                from entry in db.Set<TenantLedgerEntry>()
                join access in db.Set<EffectiveTenantAccessProjection>().AsNoTracking()
                    on new { entry.PortfolioId, entry.TenantAccountId }
                    equals new { access.PortfolioId, TenantAccountId = access.TenantAccountId!.Value }
                join user in db.Set<ApplicationUser>().AsNoTracking()
                    on access.UserId equals user.Id
                join savedPreference in db.Set<UserAlertPreference>().AsNoTracking()
                        .Select(preference => new
                        {
                            preference.PortfolioId,
                            preference.UserId,
                            EnableInApp = (bool?)preference.EnableInApp,
                            EnableMobilePush = (bool?)preference.EnableMobilePush,
                            EnableEmail = (bool?)preference.EnableEmail,
                            EnableSms = (bool?)preference.EnableSms,
                        })
                    on new { access.PortfolioId, access.UserId }
                    equals new { savedPreference.PortfolioId, savedPreference.UserId } into preferences
                from preference in preferences.DefaultIfEmpty()
                where entry.Id == ledgerEntryId
                    && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                    && entry.Direction == TenantLedgerDirection.Credit
                    && access.TenantAccountId != null
                    && access.AccessContextId == (
                        from candidate in db.Set<EffectiveTenantAccessProjection>().AsNoTracking()
                        join candidateParty in db.Set<LeaseManagementParty>().AsNoTracking()
                            on new { candidate.LeaseManagementPartyId, candidate.PortfolioId }
                            equals new { LeaseManagementPartyId = candidateParty.Id, candidateParty.PortfolioId }
                        where candidate.PortfolioId == entry.PortfolioId
                            && candidate.TenantAccountId == entry.TenantAccountId
                            && candidate.UserId == access.UserId
                        orderby candidateParty.Role == LeaseManagementPartyRole.PrimaryTenant ? 0 :
                                candidateParty.Role == LeaseManagementPartyRole.CoTenant ? 1 :
                                candidateParty.Role == LeaseManagementPartyRole.Guarantor ? 2 : 3,
                            candidate.UserId,
                            candidate.AccessContextId
                        select candidate.AccessContextId).First()
                    && !db.Set<Notification>().Any(notification =>
                        notification.PortfolioId == entry.PortfolioId
                        && notification.Type == RentReceivedNotificationType
                        && notification.UserId == access.UserId
                        && notification.RelatedEntityType == nameof(TenantLedgerEntry)
                        && notification.RelatedEntityId == entry.Id)
                orderby access.UserId
                select new TenantReceiptRecipient(
                    entry.Id,
                    entry.PortfolioId,
                    entry.TenantAccountId,
                    entry.BusinessKey,
                    access.UserId,
                    access.AccessContextId,
                    access.AccessRevision,
                    preference.EnableInApp ?? true,
                    (preference.EnableEmail ?? true) ? user.Email : null,
                    (preference.EnableSms ?? false) ? user.PhoneNumber : null,
                    (preference.EnableMobilePush ?? true)
                        ? db.Set<DeviceToken>().AsNoTracking()
                            .Where(token => token.PortfolioId == entry.PortfolioId
                                && token.UserId == access.UserId)
                            .OrderByDescending(token => token.LastSeenAt)
                            .ThenByDescending(token => token.Id)
                            .Select(token => token.Token)
                            .FirstOrDefault()
                        : null))
            .TagWith("YS-304 tenant receipt notification recipients")
            .ToListAsync(ct);

        if (recipientRows.Count == 0)
        {
            return;
        }

        var insertedNotifications = await InsertInAppNotificationsAsync(
            db,
            context,
            ledgerEntryId,
            notificationCreatedAtUtc,
            notificationExpiresAtUtc,
            ct);
        foreach (var notification in insertedNotifications)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                notification.PortfolioId,
                nameof(Notification),
                notification.NotificationId,
                AuditLogOperation.Created,
                ActorLabel: "system:tenant-receipt",
                NewValues: JsonSerializer.Serialize(new
                {
                    notification.LedgerEntryId,
                    notification.TenantAccountId,
                    notification.BusinessKey,
                    notification.UserId,
                }),
                ChangeReason: "Posted tenant rent-received notification."),
                now);
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = notification.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Notification),
                    entityId = notification.NotificationId,
                    operation = "create",
                    data = new
                    {
                        notification.LedgerEntryId,
                        notification.TenantAccountId,
                        notification.UserId,
                    },
                }),
                IdempotencyKey = NotificationKey(notification, "data-update", notification.NotificationId.ToString()),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        foreach (var row in recipientRows)
        {
            StageDeliveryOutbox(context, row, now, notificationExpiresAtUtc);
        }
    }

    private static async Task<IReadOnlyList<TenantReceiptInsertedNotification>> InsertInAppNotificationsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        long ledgerEntryId,
        DateTime notificationCreatedAtUtc,
        DateTime notificationExpiresAtUtc,
        CancellationToken ct)
    {
        var mutationTargets = new AtomicSqlMutationTarget[]
        {
            new("Notifications", AtomicSqlMutationOperation.Insert),
        };
        return await db.ExecuteAtomicSqlMutationAsync<TenantReceiptInsertedNotification>(context, $"""
            WITH receipt AS MATERIALIZED (
                SELECT entry."Id" AS ledger_entry_id,
                       entry."PortfolioId" AS portfolio_id,
                       entry."TenantAccountId" AS tenant_account_id,
                       entry."BusinessKey" AS business_key
                FROM "TenantLedgerEntries" AS entry
                WHERE entry."Id" = {ledgerEntryId}
                  AND entry."EntryType" = 'PaymentReceipt'
                  AND entry."Direction" = 'Credit'
            ),
            recipients AS MATERIALIZED (
                SELECT receipt.ledger_entry_id,
                       receipt.portfolio_id,
                       receipt.tenant_account_id,
                       receipt.business_key,
                       access."UserId" AS user_id,
                       access."AccessContextId" AS access_context_id,
                       access."AccessRevision" AS access_revision
                FROM receipt
                JOIN "vw_effective_tenant_access" AS access
                  ON access."PortfolioId" = receipt.portfolio_id
                 AND access."TenantAccountId" = receipt.tenant_account_id
                LEFT JOIN "UserAlertPreferences" AS preference
                  ON preference."PortfolioId" = access."PortfolioId"
                 AND preference."UserId" = access."UserId"
                WHERE COALESCE(preference."EnableInApp", TRUE)
                  AND access."AccessContextId" = (
                      SELECT candidate."AccessContextId"
                      FROM "vw_effective_tenant_access" AS candidate
                      JOIN "LeaseManagementParties" AS candidate_party
                        ON candidate_party."Id" = candidate."LeaseManagementPartyId"
                       AND candidate_party."PortfolioId" = candidate."PortfolioId"
                      WHERE candidate."PortfolioId" = receipt.portfolio_id
                        AND candidate."TenantAccountId" = receipt.tenant_account_id
                        AND candidate."UserId" = access."UserId"
                      ORDER BY CASE candidate_party."Role"
                            WHEN 'PrimaryTenant' THEN 0
                            WHEN 'CoTenant' THEN 1
                            WHEN 'Guarantor' THEN 2
                            ELSE 3
                          END,
                          candidate."UserId",
                          candidate."AccessContextId"
                      LIMIT 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM "Notifications" AS notification
                      WHERE notification."PortfolioId" = receipt.portfolio_id
                        AND notification."Type" = {RentReceivedNotificationType}
                        AND notification."UserId" = access."UserId"
                        AND notification."RelatedEntityType" = {nameof(TenantLedgerEntry)}
                        AND notification."RelatedEntityId" = receipt.ledger_entry_id::integer)
            ),
            inserted AS (
                INSERT INTO "Notifications" (
                    "PortfolioId", "UserId", "Type", "Title", "Message", "Severity",
                    "NavigationExperience", "NavigationDestination", "NavigationAccessContextId",
                    "NavigationAccessRevision", "NavigationResourceKind", "NavigationResourceId",
                    "NavigationParentResourceKind", "NavigationParentResourceId", "NavigationAction",
                    "NavigationExpiresAtUtc", "NavigationFallbackDestination",
                    "RelatedEntityType", "RelatedEntityId", "CreatedAt")
                SELECT recipient.portfolio_id,
                       recipient.user_id,
                       {RentReceivedNotificationType},
                       'Rent received',
                       'We received a rent payment on your account. Open Rental Command to review the receipt.',
                       'Info',
                       'Tenant',
                       'TenantLedgerEntry',
                       recipient.access_context_id,
                       recipient.access_revision,
                       {nameof(TenantLedgerEntry)},
                       recipient.ledger_entry_id::integer,
                       {nameof(TenantAccount)},
                       recipient.tenant_account_id,
                       'Open',
                       {notificationExpiresAtUtc},
                       'Home',
                       {nameof(TenantLedgerEntry)},
                       recipient.ledger_entry_id::integer,
                       {notificationCreatedAtUtc}
                FROM recipients AS recipient
                ORDER BY recipient.user_id
                RETURNING "Id" AS notification_id,
                          "PortfolioId" AS portfolio_id,
                          "UserId" AS user_id,
                          "RelatedEntityId" AS ledger_entry_id,
                          "NavigationParentResourceId" AS tenant_account_id
            )
            SELECT inserted.notification_id AS "NotificationId",
                   inserted.portfolio_id AS "PortfolioId",
                   inserted.user_id AS "UserId",
                   inserted.ledger_entry_id::bigint AS "LedgerEntryId",
                   inserted.tenant_account_id AS "TenantAccountId",
                   receipt.business_key AS "BusinessKey"
            FROM inserted
            JOIN receipt
              ON receipt.ledger_entry_id = inserted.ledger_entry_id
            ORDER BY inserted.notification_id
            """, mutationTargets, ct);
    }

    private static void StageDeliveryOutbox(
        IAtomicCommandContext context,
        TenantReceiptRecipient row,
        DateTime now,
        DateTime notificationExpiresAtUtc)
    {
        const string title = "Rent received";
        const string previewBody = "We received a rent payment on your Rental Command tenant account.";
        if (!string.IsNullOrWhiteSpace(row.Email))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = row.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    to = row.Email,
                    subject = title,
                    body = previewBody + " Sign in to Rental Command to review the receipt.",
                }),
                IdempotencyKey = NotificationKey(row, "email", row.Email),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        if (!string.IsNullOrWhiteSpace(row.PhoneNumber))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = row.PortfolioId,
                MessageType = "sms",
                Payload = JsonSerializer.Serialize(new
                {
                    to = row.PhoneNumber,
                    message = previewBody,
                }),
                IdempotencyKey = NotificationKey(row, "sms", row.PhoneNumber),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        if (!string.IsNullOrWhiteSpace(row.PushToken))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = row.PortfolioId,
                MessageType = "push",
                Payload = JsonSerializer.Serialize(new
                {
                    deviceToken = row.PushToken,
                    title,
                    body = previewBody,
                    navigationIntent = new
                    {
                        experience = NavigationExperience.Tenant.ToString(),
                        destination = NavigationDestination.TenantLedgerEntry.ToString(),
                        accessContextId = row.AccessContextId,
                        accessRevision = row.AccessRevision,
                        resource = new
                        {
                            kind = nameof(TenantLedgerEntry),
                            id = checked((int)row.LedgerEntryId),
                        },
                        parentResource = new
                        {
                            kind = nameof(TenantAccount),
                            id = row.TenantAccountId,
                        },
                        childResource = (object?)null,
                        action = NavigationAction.Open.ToString(),
                        expiresAtUtc = notificationExpiresAtUtc,
                        fallbackDestination = NavigationDestination.Home.ToString(),
                    },
                }),
                IdempotencyKey = NotificationKey(row, "push", row.PushToken),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }
    }

    private static string NotificationKey(
        TenantReceiptRecipient row,
        string channel,
        string destination) =>
        OutboxIdempotency.Create(
            "tenant-rent-received-notification",
            $"{row.TenantAccountId}:{row.BusinessKey}:{row.UserId}:{channel}:{DestinationHash(destination)}");

    private static string NotificationKey(
        TenantReceiptInsertedNotification row,
        string channel,
        string destination) =>
        OutboxIdempotency.Create(
            "tenant-rent-received-notification",
            $"{row.TenantAccountId}:{row.BusinessKey}:{row.UserId}:{channel}:{DestinationHash(destination)}");

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    internal static DateTime TenantFacingEventAtUtc(DateOnly businessDate) =>
        DateTime.SpecifyKind(businessDate.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Utc);

    internal static DateTime TenantFacingExpirationUtc(DateOnly effectiveOn, DateOnly businessDate)
    {
        var expirationBase = effectiveOn > businessDate ? effectiveOn : businessDate;
        return DateTime.SpecifyKind(expirationBase.ToDateTime(new TimeOnly(23, 59, 59)), DateTimeKind.Utc)
            .AddDays(30);
    }

    internal const string RentReceivedNotificationType = "TenantRentReceived";

    private sealed record TenantReceiptRecipient(
        long LedgerEntryId,
        int PortfolioId,
        int TenantAccountId,
        string BusinessKey,
        int UserId,
        int AccessContextId,
        long AccessRevision,
        bool EnableInApp,
        string? Email,
        string? PhoneNumber,
        string? PushToken);

    private sealed class TenantReceiptInsertedNotification
    {
        public int NotificationId { get; set; }
        public int PortfolioId { get; set; }
        public int UserId { get; set; }
        public long LedgerEntryId { get; set; }
        public int TenantAccountId { get; set; }
        public string BusinessKey { get; set; } = string.Empty;
    }
}

public sealed class PostTenantChargeHandler
    : IAtomicCommandHandler<PostTenantChargeCommand, TenantChargeMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public PostTenantChargeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantChargeMutationResult> HandleAsync(
        PostTenantChargeCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, _db, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Charge source provenance must belong to the current portfolio.");
        var incomeLedgerAccountId = await TenantMoneyCommandSupport.ResolveIncomeAccountIdAsync(
            _db, command.PortfolioId, command.IncomeLedgerAccountId, ct);

        var charge = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit, command.Amount,
            account.Currency, command.EffectiveOn, command.DueOn, command.Description,
            command.BusinessKey, times.WallClockUtc, sourceStoredFileId: command.SourceStoredFileId,
            servicePeriodStartOn: command.ServicePeriodStartOn,
            servicePeriodEndOn: command.ServicePeriodEndOn);
        _db.Add(charge);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantChargeAsync(
            _db, context, charge, command.ActorUserId,
            incomeLedgerAccountId, ct);

        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), charge.Id,
            "Tenant charge posted", new
            {
                entryType = nameof(TenantLedgerEntryType.ManualCharge),
                charge.Amount,
                charge.EffectiveOn,
                charge.DueOn,
                charge.ServicePeriodStartOn,
                charge.ServicePeriodEndOn,
                charge.SourceStoredFileId,
            });
        return new TenantChargeMutationResult(true, true, account.Id, charge.Id, null,
            charge.Amount, null);
    }

    public Task AuthorizeReplayAsync(PostTenantChargeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class ReverseTenantChargeHandler
    : IAtomicCommandHandler<ReverseTenantChargeCommand, TenantChargeMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ReverseTenantChargeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantChargeMutationResult> HandleAsync(
        ReverseTenantChargeCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, _db, times.WallClockUtc)
            join entry in _db.Set<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
                && entry.Direction == TenantLedgerDirection.Debit
                && (entry.EntryType == TenantLedgerEntryType.RentCharge
                    || entry.EntryType == TenantLedgerEntryType.AddendumCharge
                    || entry.EntryType == TenantLedgerEntryType.LateFeeCharge
                    || entry.EntryType == TenantLedgerEntryType.DepositCharge
                    || entry.EntryType == TenantLedgerEntryType.ManualCharge)
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Amount,
                entry.Currency,
                ReversedAmount = _db.Set<TenantLedgerEntry>()
                    .Where(reversal => reversal.PortfolioId == command.PortfolioId
                        && reversal.TenantAccountId == command.TenantAccountId
                        && reversal.EntryType == TenantLedgerEntryType.Reversal
                        && reversal.ReversesEntryId == entry.Id)
                    .Sum(reversal => (decimal?)reversal.Amount) ?? 0m,
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException("Reversal source provenance must belong to the current portfolio.");
        if (target.ReversedAmount > 0m)
        {
            return new TenantChargeMutationResult(true, false, target.Id, 0, target.EntryId,
                target.Amount, "The charge has already been reversed.");
        }

        var reversal = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Reversal, TenantLedgerDirection.Credit,
            target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
            command.BusinessKey, times.WallClockUtc, sourceStoredFileId: command.SourceStoredFileId);
        reversal.ReversesEntryId = target.EntryId;
        _db.Add(reversal);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantReversalAsync(
            _db, context, reversal, new TenantLedgerEntry
            {
                Id = target.EntryId,
                PortfolioId = command.PortfolioId,
                TenantAccountId = command.TenantAccountId,
                EntryType = target.EntryType,
                Currency = target.Currency,
                Amount = target.Amount,
            }, command.ActorUserId, ct);

        var reversedAllocations = await TenantMoneyPersistence.ReverseEntryAllocationsAsync(_db,
            context,
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId, times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), reversal.Id,
            "Tenant charge reversed", new
            {
                reversal.Amount,
                reversal.EffectiveOn,
                reversal.ReversesEntryId,
                reason = command.Reason.Trim(),
                reversedAllocations.AllocationCount,
                reversedAllocations.AllocatedAmount,
            });
        return new TenantChargeMutationResult(true, true, target.Id, reversal.Id,
            target.EntryId, reversal.Amount, null);
    }

    public Task AuthorizeReplayAsync(ReverseTenantChargeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class PostTenantCreditHandler
    : IAtomicCommandHandler<PostTenantCreditCommand, TenantLedgerMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public PostTenantCreditHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantLedgerMutationResult> HandleAsync(
        PostTenantCreditCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, _db, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Credit source provenance must belong to the current portfolio.");

        int? resolvedIncomeLedgerAccountId;
        var target = command.TargetChargeEntryId is not long targetChargeEntryId
            ? null
            : await _db.Set<TenantLedgerEntry>()
                .AsNoTracking()
                .Where(entry => entry.PortfolioId == command.PortfolioId
                    && entry.TenantAccountId == command.TenantAccountId
                    && entry.Id == targetChargeEntryId
                    && entry.Direction == TenantLedgerDirection.Debit
                    && (entry.EntryType == TenantLedgerEntryType.RentCharge
                        || entry.EntryType == TenantLedgerEntryType.AddendumCharge
                        || entry.EntryType == TenantLedgerEntryType.LateFeeCharge
                        || entry.EntryType == TenantLedgerEntryType.DepositCharge
                        || entry.EntryType == TenantLedgerEntryType.ManualCharge))
                .Select(entry => new
                {
                    entry.Id,
                    entry.EntryType,
                    AvailableTargetedAmount = entry.Amount
                        - (_db.Set<TenantLedgerEntry>()
                            .Where(correction => correction.PortfolioId == entry.PortfolioId
                                && correction.TenantAccountId == entry.TenantAccountId
                                && ((correction.EntryType == TenantLedgerEntryType.Reversal
                                        && correction.ReversesEntryId == entry.Id)
                                    || (correction.EntryType == TenantLedgerEntryType.Credit
                                        && correction.RelatedTenantLedgerEntryId == entry.Id)))
                            .Sum(correction => (decimal?)correction.Amount) ?? 0m),
                })
                .SingleOrDefaultAsync(ct);
        if (command.TargetChargeEntryId is not null && target is null)
            throw TenantMoneyCommandSupport.Unauthorized();
        if (target?.EntryType == TenantLedgerEntryType.DepositCharge)
            throw new ArgumentException(
                "Generic tenant credits cannot target a security-deposit charge.");
        if (target is { AvailableTargetedAmount: <= 0m })
            throw new ArgumentException(
                "The selected charge has no remaining amount available for targeted credit.");
        if (target is not null && command.Amount > target.AvailableTargetedAmount)
            throw new ArgumentException(
                "A targeted credit cannot exceed the selected charge's remaining amount.");
        if (target is null)
        {
            resolvedIncomeLedgerAccountId = await TenantMoneyCommandSupport.ResolveIncomeAccountIdAsync(
                _db, command.PortfolioId, command.IncomeLedgerAccountId, ct);
        }
        else
        {
            resolvedIncomeLedgerAccountId = command.IncomeLedgerAccountId;
        }

        var credit = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit,
            command.Amount, account.Currency, command.EffectiveOn, null,
            command.Description, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId,
            relatedTenantLedgerEntryId: command.TargetChargeEntryId);
        _db.Add(credit);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantConcessionAsync(
            _db, context, credit, command.ActorUserId,
            resolvedIncomeLedgerAccountId, ct);

        var allocations = command.TargetChargeEntryId is long targetEntryId
            ? await TenantMoneyCommandSupport.AllocateTargetChargeAsync(
                _db, command.PortfolioId, command.TenantAccountId, credit.Id, targetEntryId,
                command.Amount, command.BusinessKey, command.ActorUserId, times.WallClockUtc,
                context, ct, spillToOtherCharges: false)
            : command.AllocateOldestCharges
            ? await TenantMoneyCommandSupport.AllocateOldestAsync(
                _db, command.PortfolioId, command.TenantAccountId, credit.Id, command.Amount,
                command.BusinessKey, command.ActorUserId, times.WallClockUtc, context, ct)
            : new TenantMoneyAllocationSummary();
        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), credit.Id,
            "Tenant credit posted", new
            {
                credit.Amount,
                credit.EffectiveOn,
                credit.SourceStoredFileId,
                credit.RelatedTenantLedgerEntryId,
                allocations.AllocationCount,
                allocations.AllocatedAmount,
            });
        return new TenantLedgerMutationResult(true, true, account.Id, credit.Id, null,
            credit.EntryType, credit.Direction, credit.Amount, allocations.AllocatedAmount,
            allocations.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(PostTenantCreditCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class PostTenantAdjustmentHandler
    : IAtomicCommandHandler<PostTenantAdjustmentCommand, TenantLedgerMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public PostTenantAdjustmentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantLedgerMutationResult> HandleAsync(
        PostTenantAdjustmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, _db, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Adjustment source provenance must belong to the current portfolio.");

        var adjustment = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Adjustment, command.Direction,
            command.Amount, account.Currency, command.EffectiveOn, null,
            command.Description, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId);
        _db.Add(adjustment);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantConcessionAsync(
            _db, context, adjustment, command.ActorUserId, ct);

        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), adjustment.Id,
            "Tenant adjustment posted", new
            {
                adjustment.Amount,
                adjustment.Direction,
                adjustment.EffectiveOn,
                adjustment.SourceStoredFileId,
            });
        return new TenantLedgerMutationResult(true, true, account.Id, adjustment.Id, null,
            adjustment.EntryType, adjustment.Direction, adjustment.Amount, 0m, 0, null);
    }

    public Task AuthorizeReplayAsync(PostTenantAdjustmentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class ReverseTenantLedgerEntryHandler
    : IAtomicCommandHandler<ReverseTenantLedgerEntryCommand, TenantLedgerMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ReverseTenantLedgerEntryHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantLedgerMutationResult> HandleAsync(
        ReverseTenantLedgerEntryCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, _db, times.WallClockUtc)
            join entry in _db.Set<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
                && entry.EntryType != TenantLedgerEntryType.Reversal
                && entry.EntryType != TenantLedgerEntryType.TransferIn
                && entry.EntryType != TenantLedgerEntryType.TransferOut
                && entry.EntryType != TenantLedgerEntryType.PaymentReceipt
                && entry.EntryType != TenantLedgerEntryType.Refund
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Direction,
                entry.Amount,
                entry.Currency,
                IsSecurityDepositLinked = _db.Set<SecurityDepositEntry>()
                    .Any(depositEntry =>
                        depositEntry.PortfolioId == command.PortfolioId
                        && depositEntry.TenantLedgerEntryId == entry.Id),
                HasSecurityDepositLinkedAllocationCounterpart =
                    _db.Set<TenantLedgerAllocation>().Any(allocation =>
                        allocation.PortfolioId == command.PortfolioId
                        && allocation.TenantAccountId == command.TenantAccountId
                        && ((allocation.DebitEntryId == entry.Id
                                && _db.Set<SecurityDepositEntry>()
                                    .Any(depositEntry =>
                                        depositEntry.PortfolioId == command.PortfolioId
                                        && depositEntry.TenantLedgerEntryId
                                            == allocation.CreditEntryId))
                            || (allocation.CreditEntryId == entry.Id
                                && _db.Set<SecurityDepositEntry>()
                                    .Any(depositEntry =>
                                        depositEntry.PortfolioId == command.PortfolioId
                                        && depositEntry.TenantLedgerEntryId
                                            == allocation.DebitEntryId)))),
                HasReversal = _db.Set<TenantLedgerEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.TenantAccountId == command.TenantAccountId
                    && reversal.EntryType == TenantLedgerEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException("Reversal source provenance must belong to the current portfolio.");
        if (target.IsSecurityDepositLinked
            || target.HasSecurityDepositLinkedAllocationCounterpart)
        {
            return new TenantLedgerMutationResult(true, false, target.Id, 0, target.EntryId,
                TenantLedgerEntryType.Reversal,
                target.Direction == TenantLedgerDirection.Debit
                    ? TenantLedgerDirection.Credit
                    : TenantLedgerDirection.Debit,
                target.Amount, 0m, 0,
                "Security-deposit ledger entries and their allocated counterparts must be " +
                "corrected through the dedicated security-deposit workflow.");
        }
        if (target.HasReversal)
        {
            return new TenantLedgerMutationResult(true, false, target.Id, 0, target.EntryId,
                TenantLedgerEntryType.Reversal,
                target.Direction == TenantLedgerDirection.Debit
                    ? TenantLedgerDirection.Credit
                    : TenantLedgerDirection.Debit,
                target.Amount, 0m, 0, "The ledger entry has already been reversed.");
        }

        var reversalDirection = target.Direction == TenantLedgerDirection.Debit
            ? TenantLedgerDirection.Credit
            : TenantLedgerDirection.Debit;
        var reversal = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Reversal, reversalDirection,
            target.Amount, target.Currency, command.EffectiveOn, null,
            command.Reason, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId);
        reversal.ReversesEntryId = target.EntryId;
        _db.Add(reversal);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantReversalAsync(
            _db, context, reversal, new TenantLedgerEntry
            {
                Id = target.EntryId,
                PortfolioId = command.PortfolioId,
                TenantAccountId = command.TenantAccountId,
                EntryType = target.EntryType,
                Currency = target.Currency,
                Amount = target.Amount,
            }, command.ActorUserId, ct);

        var reversedAllocations = await TenantMoneyPersistence.ReverseEntryAllocationsAsync(_db,
            context,
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId,
            times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), reversal.Id,
            "Tenant ledger entry reversed", new
            {
                originalEntryType = target.EntryType,
                reversal.Amount,
                reversal.Direction,
                reversal.EffectiveOn,
                reversal.ReversesEntryId,
                reason = command.Reason.Trim(),
                reversedAllocations.AllocationCount,
                reversedAllocations.AllocatedAmount,
            });
        return new TenantLedgerMutationResult(true, true, target.Id, reversal.Id,
            target.EntryId, reversal.EntryType, reversal.Direction, reversal.Amount,
            reversedAllocations.AllocatedAmount, reversedAllocations.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(ReverseTenantLedgerEntryCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class RefundTenantPaymentHandler
    : IAtomicCommandHandler<RefundTenantPaymentCommand, TenantPaymentRefundResult>
{
    private readonly RentalCommandDbContext _db;

    public RefundTenantPaymentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TenantPaymentRefundResult> HandleAsync(
        RefundTenantPaymentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, _db, times.WallClockUtc)
            join entry in _db.Set<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.PaymentEntryId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Amount,
                entry.Currency,
                entry.ProviderPaymentAttemptId,
                OriginalAttemptType = entry.ProviderPaymentAttemptId == null
                    ? (TenantPaymentAttemptType?)null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (TenantPaymentAttemptType?)payment.AttemptType)
                        .SingleOrDefault(),
                OriginalAttemptState = entry.ProviderPaymentAttemptId == null
                    ? (TenantPaymentAttemptState?)null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (TenantPaymentAttemptState?)payment.State)
                        .SingleOrDefault(),
                OriginalAttemptAmount = entry.ProviderPaymentAttemptId == null
                    ? (decimal?)null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (decimal?)payment.Amount)
                        .SingleOrDefault(),
                OriginalAttemptCurrency = entry.ProviderPaymentAttemptId == null
                    ? null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => payment.Currency)
                        .SingleOrDefault(),
                Provider = entry.ProviderPaymentAttemptId == null
                    ? null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => payment.Provider)
                        .SingleOrDefault(),
                HasReversal = _db.Set<TenantLedgerEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.TenantAccountId == command.TenantAccountId
                    && reversal.EntryType == TenantLedgerEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                ExistingRefundAttemptId = entry.ProviderPaymentAttemptId == null
                    ? (long?)null
                    : _db.Set<TenantPaymentAttempt>()
                        .Where(refund => refund.PortfolioId == command.PortfolioId
                            && refund.TenantAccountId == command.TenantAccountId
                            && refund.AttemptType == TenantPaymentAttemptType.Refund
                            && refund.RefundsPaymentAttemptId == entry.ProviderPaymentAttemptId.Value)
                        .Select(refund => (long?)refund.Id)
                        .SingleOrDefault(),
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException(
                "Payment refund source provenance must belong to the current portfolio.");
        TenantPaymentRefundResult Result(
            bool applied, TenantPaymentRefundOutcome outcome, long? refundAttemptId,
            string? error) =>
            new(true, applied, outcome, target.Id, target.EntryId, null,
                refundAttemptId, target.Amount, 0m, 0, error);
        if (target.HasReversal)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "A reversed payment receipt cannot also be refunded.");
        var originalAttemptValid = target.ProviderPaymentAttemptId is not null
            && target.OriginalAttemptType is TenantPaymentAttemptType.Charge
                or TenantPaymentAttemptType.UnappliedReceipt
            && target.OriginalAttemptState == TenantPaymentAttemptState.Succeeded
            && target.OriginalAttemptAmount == target.Amount
            && string.Equals(target.OriginalAttemptCurrency, target.Currency,
                StringComparison.Ordinal);
        if (!originalAttemptValid)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "The payment receipt has no matching settled Charge context to refund.");
        if (target.ExistingRefundAttemptId is long existingRefundAttemptId)
            return Result(false, TenantPaymentRefundOutcome.AlreadyRefunded,
                existingRefundAttemptId,
                "The payment receipt has already been refunded.");

        var providerBacked = !string.Equals(
            target.Provider, "manual", StringComparison.OrdinalIgnoreCase);
        if (providerBacked)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "Provider-backed payment refunds are unavailable until a supported provider refund workflow is configured.");
        if (string.IsNullOrWhiteSpace(command.PaymentMethodSummary)
                || string.IsNullOrWhiteSpace(command.ExternalReference))
            throw new ArgumentException(
                "Manual refunds require payment method and external payout provenance.");
        var refundAttempt = new TenantPaymentAttempt
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            Provider = target.Provider!,
            ProviderObjectId = command.ExternalReference!.Trim(),
            RefundsPaymentAttemptId = target.ProviderPaymentAttemptId,
            IdempotencyKey = command.DeliveryIdempotencyKey,
            AttemptType = TenantPaymentAttemptType.Refund,
            State = TenantPaymentAttemptState.Succeeded,
            Amount = target.Amount,
            Currency = target.Currency,
            PaymentMethodSummary = command.PaymentMethodSummary!.Trim(),
            PreparedAtUtc = times.WallClockUtc,
            SubmittedAtUtc = times.WallClockUtc,
            SettledAtUtc = times.WallClockUtc,
            UpdatedAtUtc = times.WallClockUtc,
            NextAttemptAtUtc = null,
            AttemptCount = 1,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(refundAttempt);
        await context.FlushBusinessAsync(ct);

        var refund = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Refund, TenantLedgerDirection.Debit,
            target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
            command.BusinessKey, times.WallClockUtc,
            providerAttemptId: refundAttempt.Id,
            sourceStoredFileId: command.SourceStoredFileId);
        _db.Add(refund);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantRefundAsync(
            _db, context, refund, target.EntryId, command.ActorUserId, ct);
        var compensation = await TenantMoneyPersistence.ReverseEntryAllocationsAsync(_db,
            context,
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId,
            times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(TenantLedgerEntry), refund.Id,
            "Tenant payment refunded", new
            {
                PaymentReceiptEntryId = target.EntryId,
                RefundPaymentAttemptId = refundAttempt.Id,
                refund.Amount,
                refund.EffectiveOn,
                command.PaymentMethodSummary,
                command.ExternalReference,
                reason = command.Reason.Trim(),
                compensation.AllocationCount,
                compensation.AllocatedAmount,
            });
        return new TenantPaymentRefundResult(true, true,
            TenantPaymentRefundOutcome.Refunded, target.Id, target.EntryId, refund.Id,
            refundAttempt.Id, target.Amount, compensation.AllocatedAmount,
            compensation.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(RefundTenantPaymentCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class RecoverHistoricalRentChargeHandler
    : IAtomicCommandHandler<RecoverHistoricalRentChargeCommand, RecoverHistoricalRentChargeResult>
{
    private readonly RentalCommandDbContext _db;

    public RecoverHistoricalRentChargeHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecoverHistoricalRentChargeResult> HandleAsync(
        RecoverHistoricalRentChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        if (!await TenantMoneyCommandSupport
                .AuthorizedAccounts(command, _db, times.WallClockUtc)
                .AnyAsync(ct))
        {
            throw TenantMoneyCommandSupport.Unauthorized();
        }

        var recovery = await TenantMoneyPersistence.RecoverHistoricalRentChargeAsync(_db,
            context,
            command.PortfolioId,
            command.TenantAccountId,
            command.LeaseAgreementId,
            command.ExistingRentChargeEntryId,
            command.ExistingReceiptEntryId,
            command.ExistingAllocationId,
            command.ExpectedCurrentRentTrackingStartOn,
            command.CorrectRentTrackingStartOn,
            command.RentPeriodStartOn,
            command.ExpectedExistingChargeDueOn,
            command.ExpectedExistingChargeAmount,
            command.ExpectedReceiptAmount,
            command.CorrectRentAmount,
            command.FinancialReference,
            command.ActorUserId,
            times.EffectiveNowUtc,
            ct);
        if (!recovery.IsValid)
            throw new InvalidOperationException(recovery.ValidationError);

        var recoveryEntries = await _db.Set<TenantLedgerEntry>()
            .Where(entry => entry.PortfolioId == command.PortfolioId
                && (entry.Id == recovery.ReversalEntryId
                    || entry.Id == recovery.ReplacementRentChargeEntryId))
            .OrderBy(entry => entry.Id)
            .ToListAsync(ct);
        var hasOriginalRentJournal = await _db.JournalEntries.AnyAsync(entry =>
            entry.PortfolioId == command.PortfolioId
            && entry.SourceType == JournalSourceType.TenantCharge
            && entry.SourceId == recovery.ReversedRentChargeEntryId, ct);
        if (hasOriginalRentJournal)
        {
            var reversal = recoveryEntries.Single(entry => entry.Id == recovery.ReversalEntryId);
            var replacement = recoveryEntries.Single(entry =>
                entry.Id == recovery.ReplacementRentChargeEntryId);
            await TenantAccountingPosting.PostTenantReversalAsync(
                _db,
                context,
                reversal,
                new TenantLedgerEntry
                {
                    Id = recovery.ReversedRentChargeEntryId,
                    PortfolioId = command.PortfolioId,
                    TenantAccountId = command.TenantAccountId,
                    EntryType = TenantLedgerEntryType.RentCharge,
                },
                command.ActorUserId,
                ct);
            await TenantAccountingPosting.PostTenantChargeAsync(
                _db,
                context,
                replacement,
                command.ActorUserId,
                incomeSystemKey: "rental-income",
                ct);
        }

        var reference = command.FinancialReference.Trim();
        context.UseDatabaseWallClockForAudit(times.EffectiveNowUtc);
        var recoveryData = new
        {
            TenantAccountId = command.TenantAccountId,
            command.LeaseAgreementId,
            command.RentPeriodStartOn,
            RentTrackingStartOn = recovery.RentTrackingStartOn,
            recovery.ReversedRentChargeEntryId,
            recovery.ReversalEntryId,
            recovery.ReplacementRentChargeEntryId,
            recovery.ReceiptEntryId,
            recovery.ReversedAllocationId,
            recovery.ReplacementAllocationId,
            recovery.ReversedRentAmount,
            recovery.ReplacementRentAmount,
            recovery.ReallocatedAmount,
            FinancialReference = reference,
        };
        context.StageSemanticEvent(
            new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(TenantAccount),
                command.TenantAccountId,
                AuditLogOperation.Updated,
                UserId: command.ActorUserId,
                NewValues: JsonSerializer.Serialize(recoveryData),
                ChangeReason: "Historical rent charge recovered"),
            times.EffectiveNowUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(TenantAccount),
                entityId = command.TenantAccountId,
                operation = "historical-rent-recovery",
                data = recoveryData,
            }),
            IdempotencyKey = OutboxIdempotency.Create(
                "tenant-money", command.DeliveryIdempotencyKey),
            CreatedAtUtc = times.EffectiveNowUtc,
            NextAttemptAtUtc = times.EffectiveNowUtc,
        });

        return new RecoverHistoricalRentChargeResult(
            true,
            command.TenantAccountId,
            command.LeaseAgreementId,
            recovery.ReversedRentChargeEntryId,
            recovery.ReversalEntryId,
            recovery.ReplacementRentChargeEntryId,
            recovery.ReceiptEntryId,
            recovery.ReversedAllocationId,
            recovery.ReplacementAllocationId,
            recovery.RentTrackingStartOn,
            recovery.ReversedRentAmount,
            recovery.ReplacementRentAmount,
            recovery.ReallocatedAmount,
            reference);
    }

    public async Task AuthorizeReplayAsync(
        RecoverHistoricalRentChargeCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        Validate(command);
        await TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
    }

    private static void Validate(RecoverHistoricalRentChargeCommand command)
    {
        if (command.LeaseAgreementId <= 0
            || command.ExistingRentChargeEntryId <= 0
            || command.ExistingReceiptEntryId <= 0
            || command.ExistingAllocationId <= 0
            || command.ExpectedCurrentRentTrackingStartOn == default
            || command.CorrectRentTrackingStartOn == default
            || command.RentPeriodStartOn == default
            || command.ExpectedExistingChargeDueOn == default
            || command.ExpectedExistingChargeAmount <= 0m
            || command.ExpectedReceiptAmount <= 0m
            || command.CorrectRentAmount <= 0m
            || string.IsNullOrWhiteSpace(command.FinancialReference)
            || command.FinancialReference.Trim().Length > 80)
        {
            throw new ArgumentException(
                "Rent recovery requires account, agreement, exact prior rows, dates, amounts, reference, actor, access, and operation key.");
        }
    }
}

public sealed class RecoverLateFeeChargesHandler
    : IAtomicCommandHandler<RecoverLateFeeChargesCommand, RecoverLateFeeChargesResult>
{
    private readonly RentalCommandDbContext _db;

    public RecoverLateFeeChargesHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecoverLateFeeChargesResult> HandleAsync(
        RecoverLateFeeChargesCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Portfolio", command.PortfolioId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        await AuthorizeAsync(command, _db, times.WallClockUtc, ct);

        var reference = command.FinancialReference.Trim();
        var recovery = await TenantMoneyPersistence.RecoverLateFeeChargesAsync(_db,
            context,
            command.PortfolioId,
            JsonSerializer.Serialize(command.Corrections),
            command.ExpectedReviewedChargeCount,
            command.ExpectedReversedChargeCount,
            command.ExpectedReplacementChargeCount,
            command.ExpectedReversedTotal,
            command.ExpectedReplacementTotal,
            reference,
            command.ActorUserId,
            times.EffectiveNowUtc,
            ct);
        if (!recovery.IsValid)
            throw new InvalidOperationException(recovery.ValidationError);

        var referencePrefix = $"late-fee-recovery:{reference}:";
        var recoveryEntries = await _db.Set<TenantLedgerEntry>()
            .Where(entry => entry.PortfolioId == command.PortfolioId
                && entry.BusinessKey.StartsWith(referencePrefix)
                && (entry.EntryType == TenantLedgerEntryType.Reversal
                    || entry.EntryType == TenantLedgerEntryType.LateFeeCharge))
            .OrderBy(entry => entry.Id)
            .ToListAsync(ct);
        var sourceIds = recoveryEntries
            .Where(entry => entry.EntryType == TenantLedgerEntryType.Reversal)
            .Select(entry => entry.ReversesEntryId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var sourceEntries = await _db.Set<TenantLedgerEntry>()
            .Where(entry => entry.PortfolioId == command.PortfolioId && sourceIds.Contains(entry.Id))
            .ToDictionaryAsync(entry => entry.Id, ct);
        var sourceJournalIds = await _db.JournalEntries
            .Where(entry => entry.PortfolioId == command.PortfolioId
                && entry.SourceType == JournalSourceType.TenantCharge
                && sourceIds.Contains(entry.SourceId))
            .Select(entry => entry.SourceId)
            .ToHashSetAsync(ct);
        foreach (var entry in recoveryEntries.Where(entry => entry.EntryType == TenantLedgerEntryType.Reversal))
        {
            if (entry.ReversesEntryId is not { } sourceId
                || !sourceEntries.TryGetValue(sourceId, out var sourceEntry))
            {
                continue;
            }

            if (!sourceJournalIds.Contains(sourceId))
                continue;

            await TenantAccountingPosting.PostTenantReversalAsync(
                _db, context, entry, sourceEntry, command.ActorUserId, ct);
        }

        foreach (var entry in recoveryEntries.Where(entry => entry.EntryType == TenantLedgerEntryType.LateFeeCharge))
        {
            var suffixIndex = entry.BusinessKey.LastIndexOf(':');
            if (suffixIndex < 0
                || !long.TryParse(entry.BusinessKey[(suffixIndex + 1)..], out var sourceId)
                || !sourceJournalIds.Contains(sourceId))
                continue;

            await TenantAccountingPosting.PostTenantChargeAsync(
                _db,
                context,
                entry,
                command.ActorUserId,
                incomeSystemKey: "late-fee-income",
                ct);
        }

        if (recovery.ReversedChargeCount > 0 || recovery.ReplacementChargeCount > 0)
        {
            context.UseDatabaseWallClockForAudit(times.EffectiveNowUtc);
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Portfolio),
                command.PortfolioId,
                AuditLogOperation.Updated,
                command.ActorUserId,
                NewValues: JsonSerializer.Serialize(new
                {
                    recovery.ReversedChargeCount,
                    recovery.ReplacementChargeCount,
                    recovery.ReversedAllocationCount,
                    recovery.ReplacementAllocationCount,
                    recovery.ReversedTotal,
                    recovery.ReplacementTotal,
                    FinancialReference = reference,
                }),
                ChangeReason: "Recovered reviewed January late-fee ledger variance."),
                times.EffectiveNowUtc);
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Portfolio),
                    entityId = command.PortfolioId,
                    operation = "late-fee-recovery",
                    data = new
                    {
                        recovery.ReversedChargeCount,
                        recovery.ReplacementChargeCount,
                        recovery.ReversedTotal,
                        recovery.ReplacementTotal,
                        financialReference = reference,
                    },
                }),
                IdempotencyKey = OutboxIdempotency.Create(
                    "late-fee-recovery",
                    $"{command.PortfolioId}:{reference}"),
                CreatedAtUtc = times.EffectiveNowUtc,
                NextAttemptAtUtc = times.EffectiveNowUtc,
            });
        }

        return new RecoverLateFeeChargesResult(
            recovery.ReversedChargeCount,
            recovery.ReplacementChargeCount,
            recovery.ReversedAllocationCount,
            recovery.ReplacementAllocationCount,
            recovery.ReversedTotal,
            recovery.ReplacementTotal,
            recovery.ReversedAllocationTotal,
            recovery.ReplacementAllocationTotal,
            reference);
    }

    public async Task AuthorizeReplayAsync(
        RecoverLateFeeChargesCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await AuthorizeAsync(
            command,
            _db,
            await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct),
            ct);
    }

    private static async Task AuthorizeAsync(
        RecoverLateFeeChargesCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
            && assignment.SuspendedAtUtc == null
            && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null
                || assignment.EffectiveToUtc > securityNowUtc)
            && assignment.RoleProfile!.Capabilities.Any(capability =>
                capability.CapabilityDefinition!.Key == command.RequiredCapability
                && capability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
        var authorized = await db.Set<WorkspaceMembership>().AnyAsync(membership =>
            membership.PortfolioId == command.PortfolioId
            && membership.AccessContextId == command.AccessContextId
            && membership.Status == WorkspaceMembershipStatus.Active
            && membership.SuspendedAtUtc == null
            && membership.RevokedAtUtc == null
            && membership.EffectiveFromUtc <= securityNowUtc
            && (membership.EffectiveToUtc == null
                || membership.EffectiveToUtc > securityNowUtc)
            && assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && assignment.PortfolioId == command.PortfolioId)
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null),
            ct);
        if (!authorized)
            throw new UnauthorizedAccessException(
                "Late-fee recovery requires active all-property charge-management authority.");
    }

    private static void Validate(RecoverLateFeeChargesCommand command)
    {
        if (command.PortfolioId <= 0
            || command.Corrections.Count == 0
            || command.ExpectedReviewedChargeCount <= 0
            || command.ExpectedReversedChargeCount <= 0
            || command.ExpectedReplacementChargeCount < 0
            || command.ExpectedReversedTotal <= 0m
            || command.ExpectedReplacementTotal < 0m
            || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty
            || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || string.IsNullOrWhiteSpace(command.FinancialReference)
            || command.FinancialReference.Trim().Length > 80
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException(
                "Portfolio, late-fee controls, actor, access, reference, and operation key are required.");
        if (command.RequiredCapability != CapabilityKeys.MoneyChargesManage)
            throw new ArgumentException("Late-fee recovery requires charge-management capability.");
        if (command.Corrections.Any(row => row.TenantAccountId <= 0
                || row.ExistingLateFeeEntryId <= 0
                || row.ExpectedExistingAmount <= 0m
                || row.ReplacementAmount < 0m
                || (row.AlreadyReversed && row.ReplacementAmount <= 0m)))
            throw new ArgumentException("Every late-fee correction row must identify a charge and valid amounts.");
        if (command.Corrections.Count != command.ExpectedReviewedChargeCount)
            throw new ArgumentException("Late-fee correction rows must match the reviewed control count.");
        if (command.Corrections.Select(row => row.ExistingLateFeeEntryId).Distinct().Count()
            != command.Corrections.Count)
            throw new ArgumentException("Late-fee correction rows must not repeat a charge.");
    }
}

public sealed class FundSecurityDepositHandler
    : IAtomicCommandHandler<FundSecurityDepositCommand, SecurityDepositMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public FundSecurityDepositHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SecurityDepositMutationResult> HandleAsync(
        FundSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, _db, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                OpenDepositCharges = _db.Set<TenantChargeBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.TenantAccountId == command.TenantAccountId
                        && balance.EntryType == nameof(TenantLedgerEntryType.DepositCharge)
                        && balance.OpenAmount > 0m)
                    .Sum(balance => (decimal?)balance.OpenAmount) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (target.OpenDepositCharges < command.Amount)
        {
            return TenantMoneyCommandSupport.DepositConflict(
                command,
                "The deposit funding amount exceeds the tenant account's open security-deposit charges.");
        }

        var businessNowUtc = times.EffectiveNowUtc;
        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, target.Currency, command.Amount, command.PaymentMethodSummary,
            command.ExternalReference, null, null, null,
            TenantPaymentAttemptType.DepositReceipt, null, times.WallClockUtc);
        _db.Add(paymentAttempt);
        await context.FlushBusinessAsync(ct);

        var receipt = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.PaymentReceipt, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            command.Description, $"{command.BusinessKey}:tenant-receipt", businessNowUtc,
            providerAttemptId: paymentAttempt.Id, sourceStoredFileId: command.SourceStoredFileId);
        _db.Add(receipt);
        await context.FlushBusinessAsync(ct);

        var allocation = await TenantMoneyCommandSupport.AllocateOldestAsync(
            _db, command.PortfolioId, command.TenantAccountId, receipt.Id, command.Amount,
            $"{command.BusinessKey}:allocation", command.ActorUserId, businessNowUtc,
            context, ct, TenantLedgerEntryType.DepositCharge);
        if (allocation.AllocatedAmount != command.Amount)
        {
            throw new InvalidOperationException(
                "Security-deposit funding must allocate its full amount to open deposit charges.");
        }

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Receipt, SecurityDepositDirection.Increase,
            command.Amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, businessNowUtc, receipt.Id, command.SourceStoredFileId);
        _db.Add(deposit);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantReceiptAsync(
            _db, context, receipt, command.ActorUserId,
            "security-deposit-trust-cash", ct);
        TenantMoneyCommandSupport.StageMutation(context, command, businessNowUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit funded",
            new
            {
                deposit.Amount,
                deposit.EffectiveOn,
                TenantLedgerEntryId = receipt.Id,
                allocation.AllocationCount,
                allocation.AllocatedAmount,
            });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, receipt.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(FundSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, _db, ct);
}

public sealed class DeductSecurityDepositHandler
    : IAtomicCommandHandler<DeductSecurityDepositCommand, SecurityDepositMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public DeductSecurityDepositHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SecurityDepositMutationResult> HandleAsync(
        DeductSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, _db, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = _db.Set<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if ((target.Balance ?? 0m) < command.Amount)
            return TenantMoneyCommandSupport.DepositConflict(command, "The deduction exceeds the held deposit balance.");

        var description = string.IsNullOrWhiteSpace(command.Notes)
            ? command.Reason.Trim()
            : $"{command.Reason.Trim()}: {command.Notes.Trim()}";
        var charge = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit,
            command.Amount, target.Currency, command.EffectiveOn, command.EffectiveOn,
            description, $"{command.BusinessKey}:charge", times.WallClockUtc);
        var credit = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            $"Security deposit applied: {description}", $"{command.BusinessKey}:credit", times.WallClockUtc);
        _db.Add(charge);
        _db.Add(credit);
        await context.FlushBusinessAsync(ct);

        _db.Add(new TenantLedgerAllocation
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            DebitEntryId = charge.Id,
            CreditEntryId = credit.Id,
            Amount = command.Amount,
            AllocatedAtUtc = times.WallClockUtc,
            BusinessKey = $"{command.BusinessKey}:allocation",
            CreatedByUserId = command.ActorUserId,
        });
        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Deduction, SecurityDepositDirection.Decrease,
            command.Amount, target.Currency, command.EffectiveOn, description,
            command.BusinessKey, times.WallClockUtc, credit.Id, command.SourceStoredFileId);
        _db.Add(deposit);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostSecurityDepositApplicationAsync(
            _db, context, deposit, command.ActorUserId, ct);
        TenantMoneyCommandSupport.StageMutation(context, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit deduction posted",
            new { deposit.Amount, command.Reason, ChargeEntryId = charge.Id, CreditEntryId = credit.Id });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, credit.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(DeductSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, _db, ct);
}

public sealed class RefundSecurityDepositHandler
    : IAtomicCommandHandler<RefundSecurityDepositCommand, SecurityDepositMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public RefundSecurityDepositHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SecurityDepositMutationResult> HandleAsync(
        RefundSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, _db, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = _db.Set<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        var balance = target.Balance ?? 0m;
        var amount = command.Amount ?? balance;
        if (amount <= 0m || amount > balance)
            return TenantMoneyCommandSupport.DepositConflict(command, "The refund must be positive and cannot exceed the held deposit balance.");

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Refund, SecurityDepositDirection.Decrease,
            amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, times.WallClockUtc, null, null,
            payoutExternalReference: TenantMoneyCommandSupport.Clean(command.ExternalReference));
        _db.Add(deposit);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostSecurityDepositRefundAsync(
            _db, context, deposit, command.ActorUserId, ct);
        TenantMoneyCommandSupport.StageMutation(context, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit refund posted",
            new { deposit.Amount, deposit.EffectiveOn, deposit.PayoutExternalReference });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, null, amount, null);
    }

    public Task AuthorizeReplayAsync(RefundSecurityDepositCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, _db, ct);
}

public sealed class ReverseSecurityDepositEntryHandler
    : IAtomicCommandHandler<ReverseSecurityDepositEntryCommand, SecurityDepositMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public ReverseSecurityDepositEntryHandler(RentalCommandDbContext db) => _db = db;

    public async Task<SecurityDepositMutationResult> HandleAsync(
        ReverseSecurityDepositEntryCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await context.AcquireLockAsync(
            "TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        var target = await (
            from depositAccount in TenantMoneyCommandSupport.AuthorizedDepositAccounts(
                command, _db, times.WallClockUtc)
            join entry in _db.Set<SecurityDepositEntry>()
                on new { SecurityDepositAccountId = depositAccount.Id, depositAccount.PortfolioId }
                equals new { entry.SecurityDepositAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
            select new
            {
                depositAccount.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Direction,
                entry.Amount,
                entry.Currency,
                entry.TenantLedgerEntryId,
                HasReversal = _db.Set<SecurityDepositEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.SecurityDepositAccountId == command.SecurityDepositAccountId
                    && reversal.EntryType == SecurityDepositEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                LinkedLedgerType = entry.TenantLedgerEntryId == null
                    ? (TenantLedgerEntryType?)null
                    : _db.Set<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (TenantLedgerEntryType?)ledger.EntryType)
                        .SingleOrDefault(),
                LinkedLedgerDirection = entry.TenantLedgerEntryId == null
                    ? (TenantLedgerDirection?)null
                    : _db.Set<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (TenantLedgerDirection?)ledger.Direction)
                        .SingleOrDefault(),
                LinkedLedgerAmount = entry.TenantLedgerEntryId == null
                    ? (decimal?)null
                    : _db.Set<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (decimal?)ledger.Amount)
                        .SingleOrDefault(),
                LinkedLedgerCurrency = entry.TenantLedgerEntryId == null
                    ? null
                    : _db.Set<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => ledger.Currency)
                        .SingleOrDefault(),
                LinkedLedgerHasReversal = entry.TenantLedgerEntryId != null
                    && _db.Set<TenantLedgerEntry>().Any(reversal =>
                        reversal.PortfolioId == command.PortfolioId
                        && reversal.TenantAccountId == command.TenantAccountId
                        && reversal.EntryType == TenantLedgerEntryType.Reversal
                        && reversal.ReversesEntryId == entry.TenantLedgerEntryId.Value),
                SourceAllowed = command.SourceStoredFileId == null
                    || _db.Set<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException(
                "Deposit reversal source provenance must belong to the current portfolio.");
        if (target.HasReversal)
            return TenantMoneyCommandSupport.DepositConflict(
                command, "The security-deposit entry has already been reversed.");
        if (target.EntryType is not (SecurityDepositEntryType.Receipt
                or SecurityDepositEntryType.Adjustment))
            return TenantMoneyCommandSupport.DepositConflict(command,
                "Only deposit receipts and standalone adjustments have complete local reversal provenance. " +
                "Deductions, payouts, and transfers require their dedicated external or paired workflow.");

        TenantLedgerEntry? ledgerReversal = null;
        TenantMoneyAllocationSummary allocationCompensation = new();
        if (target.EntryType == SecurityDepositEntryType.Receipt)
        {
            var validLinkedReceipt = target.TenantLedgerEntryId is not null
                && target.LinkedLedgerType == TenantLedgerEntryType.PaymentReceipt
                && target.LinkedLedgerDirection == TenantLedgerDirection.Credit
                && target.LinkedLedgerAmount == target.Amount
                && string.Equals(target.LinkedLedgerCurrency, target.Currency,
                    StringComparison.Ordinal)
                && !target.LinkedLedgerHasReversal;
            if (!validLinkedReceipt)
                return TenantMoneyCommandSupport.DepositConflict(command,
                    "The deposit receipt's tenant-ledger provenance is missing, mismatched, or already reversed.");

            ledgerReversal = TenantMoneyCommandSupport.Ledger(
                command, TenantLedgerEntryType.Reversal, TenantLedgerDirection.Debit,
                target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
                $"{command.BusinessKey}:tenant-ledger", times.WallClockUtc,
                sourceStoredFileId: command.SourceStoredFileId);
            ledgerReversal.ReversesEntryId = target.TenantLedgerEntryId;
            _db.Add(ledgerReversal);
            await context.FlushBusinessAsync(ct);
            allocationCompensation = await TenantMoneyPersistence.ReverseEntryAllocationsAsync(_db,
                context,
                command.PortfolioId, command.TenantAccountId,
                target.TenantLedgerEntryId!.Value,
                $"{command.BusinessKey}:allocation", command.ActorUserId,
                times.WallClockUtc, ct);
        }

        var reversalDirection = target.Direction == SecurityDepositDirection.Increase
            ? SecurityDepositDirection.Decrease
            : SecurityDepositDirection.Increase;
        var depositReversal = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Reversal, reversalDirection,
            target.Amount, target.Currency, command.EffectiveOn, command.Reason,
            command.BusinessKey, times.WallClockUtc, ledgerReversal?.Id,
            command.SourceStoredFileId);
        depositReversal.ReversesEntryId = target.EntryId;
        _db.Add(depositReversal);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostSecurityDepositReversalAsync(
            _db, context, depositReversal, new SecurityDepositEntry
            {
                Id = target.EntryId,
                PortfolioId = command.PortfolioId,
                SecurityDepositAccountId = command.SecurityDepositAccountId,
                EntryType = target.EntryType,
                Direction = target.Direction,
                Amount = target.Amount,
                Currency = target.Currency,
                TenantLedgerEntryId = target.TenantLedgerEntryId,
            }, command.ActorUserId, ct);
        TenantMoneyCommandSupport.StageMutation(
            context, command, times.WallClockUtc, nameof(SecurityDepositEntry),
            depositReversal.Id, "Security deposit entry reversed", new
            {
                originalEntryType = target.EntryType,
                depositReversal.Amount,
                depositReversal.Direction,
                depositReversal.EffectiveOn,
                depositReversal.ReversesEntryId,
                TenantLedgerReversalId = ledgerReversal?.Id,
                allocationCompensation.AllocationCount,
                allocationCompensation.AllocatedAmount,
                reason = command.Reason.Trim(),
            });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, depositReversal.Id, ledgerReversal?.Id,
            depositReversal.Amount, null);
    }

    public Task AuthorizeReplayAsync(ReverseSecurityDepositEntryCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, _db, ct);
}

internal static class TenantMoneyCommandSupport
{
    internal static IQueryable<TenantAccount> AuthorizedAccounts(
        ITenantMoneyCommand command, RentalCommandDbContext db, DateTime securityNowUtc)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));
        return db.Set<TenantAccount>().Where(account =>
            account.Id == command.TenantAccountId && account.PortfolioId == command.PortfolioId
            && account.ClosedAtUtc == null && account.LeaseManagement != null
            && db.Set<AuthSession>().Any(session => session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && db.Set<WorkspaceAccessContext>().Any(context => context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment => assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope => scope.PortfolioId == command.PortfolioId
                                && scope.PropertyId == account.LeaseManagement.PropertyId)))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == command.RequiredCapability))));
    }

    internal static IQueryable<SecurityDepositAccount> AuthorizedDepositAccounts(
        ISecurityDepositMoneyCommand command, RentalCommandDbContext db, DateTime securityNowUtc) =>
        from deposit in db.Set<SecurityDepositAccount>()
        join account in AuthorizedAccounts(command, db, securityNowUtc)
            on new { deposit.TenantAccountId, deposit.PortfolioId }
            equals new { TenantAccountId = account.Id, account.PortfolioId }
        where deposit.Id == command.SecurityDepositAccountId
        select deposit;

    internal static async Task AuthorizeReplayAsync(ITenantMoneyCommand command,
        RentalCommandDbContext db, CancellationToken ct)
    {
        Validate(command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedAccounts(command, db, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static async Task AuthorizeDepositReplayAsync(ISecurityDepositMoneyCommand command,
        RentalCommandDbContext db, CancellationToken ct)
    {
        Validate(command);
        var now = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedDepositAccounts(command, db, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static void Validate(ITenantMoneyCommand command)
    {
        if (command.PortfolioId <= 0 || command.TenantAccountId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequiredCapability)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new ArgumentException("Portfolio, tenant account, actor, access, capability, and operation key are required.");
        if (command is ISecurityDepositMoneyCommand deposit && deposit.SecurityDepositAccountId <= 0)
            throw new ArgumentException("Security deposit account is required.");
        decimal? amount = command switch
        {
            RecordTenantReceiptCommand receiptCommand => receiptCommand.Amount,
            PostTenantChargeCommand chargeCommand => chargeCommand.Amount,
            ReverseTenantChargeCommand => null,
            PostTenantCreditCommand creditCommand => creditCommand.Amount,
            PostTenantAdjustmentCommand adjustmentCommand => adjustmentCommand.Amount,
            ReverseTenantLedgerEntryCommand => null,
            RefundTenantPaymentCommand => null,
            RecoverHistoricalRentChargeCommand recoveryCommand => recoveryCommand.CorrectRentAmount,
            RecoverRefundedTenantAllocationCommand recoveryCommand =>
                recoveryCommand.ExpectedAllocationAmount,
            FundSecurityDepositCommand fund => fund.Amount,
            DeductSecurityDepositCommand deduction => deduction.Amount,
            RefundSecurityDepositCommand refund => refund.Amount ?? 1m,
            ReverseSecurityDepositEntryCommand => null,
            _ => 0m,
        };
        if (amount is <= 0m) throw new ArgumentException("Amount must be positive.");
        if (command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException("Operation key cannot exceed 200 characters.");
        if ((command is PostTenantChargeCommand or ReverseTenantChargeCommand
                or PostTenantCreditCommand or PostTenantAdjustmentCommand
                or ReverseTenantLedgerEntryCommand or RecoverHistoricalRentChargeCommand
                or RecoverRefundedTenantAllocationCommand)
            && command.RequiredCapability != CapabilityKeys.MoneyChargesManage)
            throw new ArgumentException("Tenant ledger mutations require the charge-management capability.");
        if ((command is RecordTenantReceiptCommand or RefundTenantPaymentCommand)
            && command.RequiredCapability != CapabilityKeys.MoneyPaymentsManage)
            throw new ArgumentException("Payment receipts and refunds require the payment-management capability.");
        if (command is ISecurityDepositMoneyCommand
            && command.RequiredCapability != CapabilityKeys.MoneyDepositsManage)
            throw new ArgumentException("Security-deposit mutations require the deposit-management capability.");
        if (command is RecordTenantReceiptCommand receipt
            && (receipt.EffectiveOn == default || string.IsNullOrWhiteSpace(receipt.Description)
                || receipt.Description.Trim().Length > 500
                || string.IsNullOrWhiteSpace(receipt.PaymentMethodSummary)
                || receipt.PaymentMethodSummary.Trim().Length > 200))
            throw new ArgumentException("Receipt date, description, and payment method are required and must fit their limits.");
        if (command is PostTenantChargeCommand charge
            && (charge.EffectiveOn == default || charge.DueOn == default
                || string.IsNullOrWhiteSpace(charge.Description)
                || charge.Description.Trim().Length > 500
                || charge.SourceStoredFileId is <= 0
                || (charge.ServicePeriodStartOn is DateOnly chargeStart
                    && charge.ServicePeriodEndOn is DateOnly chargeEnd
                    && chargeEnd < chargeStart)
                || charge.IncomeLedgerAccountId is <= 0))
            throw new ArgumentException(
                "Charge dates, description, service period, and valid source provenance are required.");
        if (command is ReverseTenantChargeCommand reversal
            && (reversal.ReversesEntryId <= 0 || reversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(reversal.Reason)
                || reversal.Reason.Trim().Length > 500
                || reversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Charge entry, reversal date, reason, and valid source provenance are required.");
        if (command is PostTenantCreditCommand credit
            && (credit.EffectiveOn == default || string.IsNullOrWhiteSpace(credit.Description)
                || credit.Description.Trim().Length > 500
                || credit.SourceStoredFileId is <= 0
                || credit.TargetChargeEntryId is <= 0
                || credit.IncomeLedgerAccountId is <= 0))
            throw new ArgumentException(
                "Credit date, description, and valid source provenance are required.");
        if (command is PostTenantAdjustmentCommand adjustment
            && (!Enum.IsDefined(adjustment.Direction)
                || adjustment.EffectiveOn == default
                || string.IsNullOrWhiteSpace(adjustment.Description)
                || adjustment.Description.Trim().Length > 500
                || adjustment.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Adjustment direction, date, description, and valid source provenance are required.");
        if (command is ReverseTenantLedgerEntryCommand ledgerReversal
            && (ledgerReversal.ReversesEntryId <= 0 || ledgerReversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(ledgerReversal.Reason)
                || ledgerReversal.Reason.Trim().Length > 500
                || ledgerReversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Ledger entry, reversal date, reason, and valid source provenance are required.");
        if (command is RefundTenantPaymentCommand paymentRefund
            && (paymentRefund.PaymentEntryId <= 0 || paymentRefund.EffectiveOn == default
                || string.IsNullOrWhiteSpace(paymentRefund.Reason)
                || paymentRefund.Reason.Trim().Length > 500
                || paymentRefund.PaymentMethodSummary?.Trim().Length > 200
                || paymentRefund.ExternalReference?.Trim().Length > 200
                || paymentRefund.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Payment entry, refund date, reason, and valid source provenance are required.");
        if (command is ReverseSecurityDepositEntryCommand depositReversal
            && (depositReversal.ReversesEntryId <= 0
                || depositReversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(depositReversal.Reason)
                || depositReversal.Reason.Trim().Length > 500
                || depositReversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Deposit entry, reversal date, reason, and valid source provenance are required.");
        if (command is RefundSecurityDepositCommand payout
            && payout.ExternalReference?.Trim().Length > 200)
            throw new ArgumentException("Payout reference cannot exceed 200 characters.");
    }

    internal static TenantPaymentAttempt ManualAttempt(ITenantMoneyCommand command, string currency,
        decimal amount, string method, string? externalReference, string? payerName,
        string? checkNumber, string? bankName, TenantPaymentAttemptType attemptType,
        long? chargeLedgerEntryId, DateTime now) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        ChargeLedgerEntryId = chargeLedgerEntryId,
        Provider = "manual",
        ProviderObjectId = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim(),
        IdempotencyKey = command.DeliveryIdempotencyKey,
        AttemptType = attemptType,
        State = TenantPaymentAttemptState.Succeeded,
        Amount = amount,
        Currency = currency,
        PaymentMethodSummary = method.Trim(),
        PayerName = payerName?.Trim(),
        CheckNumber = checkNumber?.Trim(),
        BankName = bankName?.Trim(),
        PreparedAtUtc = now,
        SubmittedAtUtc = now,
        SettledAtUtc = now,
        UpdatedAtUtc = now,
        AttemptCount = 1,
        CreatedByUserId = command.ActorUserId,
    };

    internal static TenantLedgerEntry Ledger(ITenantMoneyCommand command,
        TenantLedgerEntryType type, TenantLedgerDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, DateOnly? dueOn, string description,
        string businessKey, DateTime now, long? providerAttemptId = null,
        int? sourceStoredFileId = null, long? relatedTenantLedgerEntryId = null,
        DateOnly? servicePeriodStartOn = null, DateOnly? servicePeriodEndOn = null) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        DueOn = dueOn,
        PostedAtUtc = now,
        Description = description.Trim(),
        BusinessKey = businessKey,
        RelatedTenantLedgerEntryId = relatedTenantLedgerEntryId,
        ServicePeriodStartOn = servicePeriodStartOn,
        ServicePeriodEndOn = servicePeriodEndOn,
        ProviderPaymentAttemptId = providerAttemptId,
        SourceStoredFileId = sourceStoredFileId,
        CreatedByUserId = command.ActorUserId,
    };

    internal static SecurityDepositEntry DepositEntry(ISecurityDepositMoneyCommand command,
        SecurityDepositEntryType type, SecurityDepositDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, string description, string businessKey,
        DateTime now, long? tenantLedgerEntryId, int? sourceStoredFileId,
        string? payoutExternalReference = null) => new()
    {
        PortfolioId = command.PortfolioId,
        SecurityDepositAccountId = command.SecurityDepositAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        PostedAtUtc = now,
        BusinessKey = businessKey,
        Description = description.Trim(),
        TenantLedgerEntryId = tenantLedgerEntryId,
        SourceStoredFileId = sourceStoredFileId,
        PayoutExternalReference = payoutExternalReference,
        CreatedByUserId = command.ActorUserId,
    };

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static DateTime CommandTimestamp(DateTime requestedUtc, DateTime fallbackUtc)
    {
        var timestamp = requestedUtc == default ? fallbackUtc : requestedUtc;
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };
    }

    internal static Task<TenantMoneyAllocationSummary> AllocateOldestAsync(
        RentalCommandDbContext db,
        int portfolioId, int accountId, long receiptId, decimal available, string businessKey,
        int actorUserId, DateTime now, IAtomicCommandContext context, CancellationToken ct,
        TenantLedgerEntryType? onlyType = null)
    {
        return TenantMoneyPersistence.AllocateOldestChargesAsync(db, context,
            portfolioId, accountId, receiptId, available, businessKey,
            actorUserId, now, onlyType?.ToString(), ct);
    }

    internal static Task<TenantMoneyAllocationSummary> AllocateTargetChargeAsync(
        RentalCommandDbContext db,
        int portfolioId, int accountId, long receiptId, long targetChargeEntryId,
        decimal available, string businessKey, int actorUserId, DateTime now,
        IAtomicCommandContext context, CancellationToken ct, bool spillToOtherCharges = true)
    {
        return TenantMoneyPersistence.AllocateTargetChargeAsync(db, context,
            portfolioId, accountId, receiptId, targetChargeEntryId, available,
            businessKey, actorUserId, now, ct, spillToOtherCharges);
    }

    internal static async Task<int> ResolveIncomeAccountIdAsync(
        RentalCommandDbContext db, int portfolioId, int? incomeLedgerAccountId,
        CancellationToken ct)
    {
        if (incomeLedgerAccountId is not int selectedAccountId)
        {
            return await AccountingPostingSupport.RequireSystemAccountIdAsync(
                db, portfolioId, "rental-income", ct);
        }

        var valid = await db.Set<LedgerAccount>().AnyAsync(account =>
            account.Id == selectedAccountId
            && account.PortfolioId == portfolioId
            && account.AccountType == AccountType.Income
            && account.IsActive, ct);
        if (!valid)
            throw new ArgumentException(
                "The selected income ledger account must be an active income account in the current portfolio.");
        return selectedAccountId;
    }

    internal static void StageMutation(IAtomicCommandContext context, ITenantMoneyCommand command,
        DateTime now, string entityType, long entityId, string reason, object values)
    {
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(TenantAccount),
            command.TenantAccountId, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(values), ChangeReason: reason), now);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType, entityId,
                data = new { command.TenantAccountId, mutation = reason } }),
            IdempotencyKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    internal static SecurityDepositMutationResult DepositConflict(
        ISecurityDepositMoneyCommand command, string error) => new(true, false,
            command.TenantAccountId, command.SecurityDepositAccountId, 0, null, 0, error);

    internal static UnauthorizedAccessException Unauthorized() => new(
        "The tenant account is not authorized in the current access context, capability, and property scope.");
}
