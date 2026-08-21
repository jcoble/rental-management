using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Payments;

/// <summary>
/// Atomic, set-based scheduled tenant billing. PostgreSQL owns every eligibility and duplicate
/// decision; these handlers only attach durable audit/outbox companions to rows returned by each
/// bounded insert statement.
/// </summary>
public sealed class ApplyScheduledRentChargeBatchHandler
{
    private readonly RentalCommandDbContext _db;

    public ApplyScheduledRentChargeBatchHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ApplyScheduledRentChargeBatchResult> ExecuteAsync(
        ApplyScheduledRentChargeBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScheduledTenantChargeCompanionStaging.Validate(
            command.RunToken,
            command.BusinessNowUtc,
            command.BatchSize);

        var rent = await TenantMoneyPersistence.PostScheduledRentChargesAsync(_db, context,
                command.BatchSize,
                command.BusinessNowUtc,
                ct);
        await ScheduledTenantChargeCompanionStaging.PostScheduledChargesAsync(_db, context, rent, ct);

        await ScheduledTenantChargeCompanionStaging.StageChargeCompanionsAsync(
            context,
            rent,
            command.BusinessNowUtc,
            ct);
        await ScheduledTenantChargeCompanionStaging.StageRentChargeTenantNotificationsAsync(
            _db,
            context,
            rent,
            command.BusinessNowUtc,
            ct);

        return new ApplyScheduledRentChargeBatchResult(rent.Count);
    }

    public async Task AuthorizeAsync(
        ApplyScheduledRentChargeBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScheduledTenantChargeCompanionStaging.Validate(
            command.RunToken,
            command.BusinessNowUtc,
            command.BatchSize);

        // Scheduled rent is a system-owned global batch with no user/session envelope. Replay
        // authority is the command-owned run token plus the same bounded ledger surface.
        await _db.Set<TenantLedgerEntry>()
            .AsNoTracking()
            .Where(entry => entry.EntryType == TenantLedgerEntryType.RentCharge)
            .Select(entry => entry.Id)
            .Take(1)
            .ToListAsync(ct);
    }
}

public sealed class ApplyScheduledLateFeeChargeBatchHandler
{
    private readonly RentalCommandDbContext _db;

    public ApplyScheduledLateFeeChargeBatchHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ApplyScheduledLateFeeChargeBatchResult> ExecuteAsync(
        ApplyScheduledLateFeeChargeBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScheduledTenantChargeCompanionStaging.Validate(
            command.RunToken,
            command.BusinessNowUtc,
            command.BatchSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StateLateFeeCapsJson);

        var lateFees = await TenantMoneyPersistence.PostScheduledLateFeesAsync(_db, context,
                command.BatchSize,
                command.StateLateFeeCapsJson,
                command.BusinessNowUtc,
                ct);
        await ScheduledTenantChargeCompanionStaging.PostScheduledChargesAsync(_db, context, lateFees, ct);

        await ScheduledTenantChargeCompanionStaging.StageChargeCompanionsAsync(
            context,
            lateFees,
            command.BusinessNowUtc,
            ct);

        return new ApplyScheduledLateFeeChargeBatchResult(lateFees.Count);
    }

    public async Task AuthorizeAsync(
        ApplyScheduledLateFeeChargeBatchCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ScheduledTenantChargeCompanionStaging.Validate(
            command.RunToken,
            command.BusinessNowUtc,
            command.BatchSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StateLateFeeCapsJson);

        // Scheduled late fees are a system-owned global batch with no user/session envelope.
        // Replay authority is the command-owned run token plus the same bounded ledger surface.
        await _db.Set<TenantLedgerEntry>()
            .AsNoTracking()
            .Where(entry => entry.EntryType == TenantLedgerEntryType.LateFeeCharge)
            .Select(entry => entry.Id)
            .Take(1)
            .ToListAsync(ct);
    }

}

internal static class ScheduledTenantChargeCompanionStaging
{
    public static async Task PostScheduledChargesAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        IReadOnlyCollection<TenantMoneyScheduledCharge> scheduledCharges,
        CancellationToken ct)
    {
        if (scheduledCharges.Count == 0)
            return;

        var portfolioId = scheduledCharges.First().PortfolioId;
        if (scheduledCharges.Any(charge => charge.PortfolioId != portfolioId))
            throw new InvalidOperationException("Scheduled tenant charges must belong to one portfolio.");
        var ids = scheduledCharges.Select(charge => charge.LedgerEntryId).ToArray();
        var entries = await db.Set<TenantLedgerEntry>()
            .Where(entry => entry.PortfolioId == portfolioId && ids.Contains(entry.Id))
            .OrderBy(entry => entry.Id)
            .ToListAsync(ct);
        await TenantAccountingPosting.PostScheduledTenantChargesAsync(db, context, entries, ct);
    }

    public static async Task StageChargeCompanionsAsync(
        IAtomicCommandContext context,
        IReadOnlyCollection<TenantMoneyScheduledCharge> charges,
        DateTime now,
        CancellationToken ct)
    {
        context.UseDatabaseWallClockForAudit(now);

        foreach (var charge in charges)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
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
            context.StageOutbox(new OutboxMessage
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

    }

    public static void Validate(Guid runToken, DateTime businessNowUtc, int batchSize)
    {
        if (runToken == Guid.Empty)
            throw new ArgumentException("A scheduled tenant-charge run token is required.");
        if (businessNowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled tenant-charge business time must be UTC.");
        if (batchSize is <= 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
    }

    public static async Task StageRentChargeTenantNotificationsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        IReadOnlyCollection<TenantMoneyScheduledCharge> rentCharges,
        DateTime now,
        CancellationToken ct)
    {
        if (rentCharges.Count == 0)
        {
            return;
        }

        var ledgerEntryIds = rentCharges.Select(charge => charge.LedgerEntryId).ToArray();
        var recipientRows = await (
                from entry in db.Set<TenantLedgerEntry>()
                join account in db.Set<TenantAccount>()
                    on new { TenantAccountId = entry.TenantAccountId, entry.PortfolioId }
                    equals new { TenantAccountId = account.Id, account.PortfolioId }
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
                            EnableMobilePush = (bool?)preference.EnableMobilePush,
                            EnableEmail = (bool?)preference.EnableEmail,
                            EnableSms = (bool?)preference.EnableSms,
                        })
                    on new { access.PortfolioId, access.UserId }
                    equals new { savedPreference.PortfolioId, savedPreference.UserId } into preferences
                from preference in preferences.DefaultIfEmpty()
                where ledgerEntryIds.Contains(entry.Id)
                    && entry.EntryType == TenantLedgerEntryType.RentCharge
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
                        && notification.Type == ScheduledRentNotificationType
                        && notification.UserId == access.UserId
                        && notification.RelatedEntityType == nameof(TenantLedgerEntry)
                        && notification.RelatedEntityId == entry.Id)
                orderby entry.Id
                select new RentChargeTenantRecipient(
                    entry.Id,
                    entry.PortfolioId,
                    entry.TenantAccountId,
                    entry.BusinessKey,
                    entry.Amount,
                    entry.DueOn ?? entry.EffectiveOn,
                    access.UserId,
                    access.AccessContextId,
                    access.AccessRevision,
                    user.Email,
                    user.PhoneNumber,
                    preference.EnableMobilePush ?? true,
                    preference.EnableEmail ?? true,
                    preference.EnableSms ?? false,
                    (preference.EnableMobilePush ?? true)
                        ? db.Set<DeviceToken>().AsNoTracking()
                            .Where(token => token.PortfolioId == entry.PortfolioId
                                && token.UserId == access.UserId)
                            .OrderByDescending(token => token.LastSeenAt)
                            .ThenByDescending(token => token.Id)
                            .Select(token => token.Token)
                            .FirstOrDefault()
                        : null))
            .TagWith("YS-163 scheduled rent charge tenant notification recipients")
            .ToListAsync(ct);

        if (recipientRows.Count == 0)
        {
            return;
        }

        var notifications = recipientRows.Select(row => new RentChargeNotificationProjection(
            row,
            new Notification
            {
                PortfolioId = row.PortfolioId,
                UserId = row.UserId,
                Type = ScheduledRentNotificationType,
                Title = "Rent charge posted",
                Message = $"A rent charge of {row.Amount:C} was posted to your account and is due {row.DueOn:MMM d, yyyy}.",
                Severity = "Info",
                NavigationExperience = NavigationExperience.Tenant,
                NavigationDestination = NavigationDestination.TenantLedgerEntry,
                NavigationAccessContextId = row.AccessContextId,
                NavigationAccessRevision = row.AccessRevision,
                NavigationResourceKind = nameof(TenantLedgerEntry),
                NavigationResourceId = checked((int)row.LedgerEntryId),
                NavigationParentResourceKind = nameof(TenantAccount),
                NavigationParentResourceId = row.TenantAccountId,
                NavigationAction = NavigationAction.Open,
                NavigationExpiresAtUtc = now.AddDays(30),
                NavigationFallbackDestination = NavigationDestination.Home,
                RelatedEntityType = nameof(TenantLedgerEntry),
                RelatedEntityId = checked((int)row.LedgerEntryId),
                CreatedAt = now,
            }))
            .ToList();
        db.AddRange(notifications.Select(projection => projection.Notification));
        foreach (var projection in notifications)
        {
            var row = projection.Recipient;
            var notification = projection.Notification;
            context.BindSemanticAudit(notification, new AtomicSemanticAudit(
                row.PortfolioId,
                nameof(Notification),
                0,
                AuditLogOperation.Created,
                ActorLabel: "system:scheduled-tenant-billing",
                NewValues: JsonSerializer.Serialize(new
                {
                    row.LedgerEntryId,
                    row.TenantAccountId,
                    row.BusinessKey,
                    row.UserId,
                }),
                ChangeReason: "Posted scheduled rent charge tenant notification."));
        }
        await context.FlushBusinessAsync(ct);

        foreach (var projection in notifications)
        {
            var row = projection.Recipient;
            var notification = projection.Notification;
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = row.PortfolioId,
                MessageType = "data-update",
                Payload = JsonSerializer.Serialize(new
                {
                    entityType = nameof(Notification),
                    entityId = notification.Id,
                    operation = "create",
                    data = new
                    {
                        row.LedgerEntryId,
                        row.TenantAccountId,
                        row.UserId,
                    },
                }),
                IdempotencyKey = NotificationKey(row, "data-update", notification.Id.ToString()),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
            StageDeliveryOutbox(context, row, now);
        }
    }

    private static void StageDeliveryOutbox(
        IAtomicCommandContext context,
        RentChargeTenantRecipient row,
        DateTime now)
    {
        var title = "Rent charge posted";
        var privateBody = $"A rent charge of {row.Amount:C} was posted to your account and is due {row.DueOn:MMM d, yyyy}.";
        var previewBody = "A rent charge was posted to your Rental Command tenant account.";
        if (row.EnableEmail && !string.IsNullOrWhiteSpace(row.Email))
        {
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = row.PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    to = row.Email,
                    subject = title,
                    body = privateBody + " Sign in to Rental Command to review your balance.",
                }),
                IdempotencyKey = NotificationKey(row, "email", row.Email),
                CreatedAtUtc = now,
                NextAttemptAtUtc = now,
            });
        }

        if (row.EnableSms && !string.IsNullOrWhiteSpace(row.PhoneNumber))
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

        if (row.EnableMobilePush && !string.IsNullOrWhiteSpace(row.PushToken))
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
                        expiresAtUtc = now.AddDays(30),
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
        RentChargeTenantRecipient row,
        string channel,
        string destination) =>
        OutboxIdempotency.Create(
            "scheduled-rent-charge-notification",
            $"{row.TenantAccountId}:{row.BusinessKey}:{row.UserId}:{channel}:{DestinationHash(destination)}");

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())))
            .ToLowerInvariant();

    private const string ScheduledRentNotificationType = "ScheduledRentCharge";

    private sealed record RentChargeTenantRecipient(
        long LedgerEntryId,
        int PortfolioId,
        int TenantAccountId,
        string BusinessKey,
        decimal Amount,
        DateOnly DueOn,
        int UserId,
        int AccessContextId,
        long AccessRevision,
        string? Email,
        string? PhoneNumber,
        bool EnableMobilePush,
        bool EnableEmail,
        bool EnableSms,
        string? PushToken);

    private sealed record RentChargeNotificationProjection(
        RentChargeTenantRecipient Recipient,
        Notification Notification);
}
