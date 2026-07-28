using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Authorization;

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
        if (command.BusinessNowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Scheduled tenant-charge business time must be UTC.");
        if (command.BatchSize is <= 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(command.BatchSize));
        if (!command.IncludeRentCharges && !command.IncludeLateFeeCharges)
            throw new ArgumentException("At least one scheduled tenant-charge type is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StateLateFeeCapsJson);
        attempt.UseDatabaseWallClockForAudit(command.BusinessNowUtc);

        var rent = command.IncludeRentCharges
            ? await attempt.TenantMoney.PostScheduledRentChargesAsync(
                command.BatchSize,
                command.BusinessNowUtc,
                ct)
            : [];
        var lateFees = command.IncludeLateFeeCharges
            ? await attempt.TenantMoney.PostScheduledLateFeesAsync(
                command.BatchSize,
                command.StateLateFeeCapsJson,
                command.BusinessNowUtc,
                ct)
            : [];

        var now = command.BusinessNowUtc;
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

        await StageRentChargeTenantNotificationsAsync(attempt, rent, now, ct);

        return new ApplyScheduledTenantChargeBatchResult(rent.Count, lateFees.Count);
    }

    private static async Task StageRentChargeTenantNotificationsAsync(
        IAtomicWriteAttempt attempt,
        IReadOnlyCollection<AtomicScheduledTenantCharge> rentCharges,
        DateTime now,
        CancellationToken ct)
    {
        if (rentCharges.Count == 0)
        {
            return;
        }

        var ledgerEntryIds = rentCharges.Select(charge => charge.LedgerEntryId).ToArray();
        var recipientRows = await (
                from entry in attempt.Persistence.Query<TenantLedgerEntry>()
                join account in attempt.Persistence.Query<TenantAccount>()
                    on new { TenantAccountId = entry.TenantAccountId, entry.PortfolioId }
                    equals new { TenantAccountId = account.Id, account.PortfolioId }
                join access in attempt.Persistence.Query<EffectiveTenantAccessProjection>().AsNoTracking()
                    on new { entry.PortfolioId, entry.TenantAccountId }
                    equals new { access.PortfolioId, TenantAccountId = access.TenantAccountId!.Value }
                join user in attempt.Persistence.Query<ApplicationUser>().AsNoTracking()
                    on access.UserId equals user.Id
                join savedPreference in attempt.Persistence.Query<UserAlertPreference>().AsNoTracking()
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
                        from candidate in attempt.Persistence.Query<EffectiveTenantAccessProjection>().AsNoTracking()
                        join candidateParty in attempt.Persistence.Query<LeaseManagementParty>().AsNoTracking()
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
                    && !attempt.Persistence.Query<Notification>().Any(notification =>
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
                        ? attempt.Persistence.Query<DeviceToken>().AsNoTracking()
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
        attempt.Persistence.AddRange(notifications.Select(projection => projection.Notification));
        foreach (var projection in notifications)
        {
            var row = projection.Recipient;
            var notification = projection.Notification;
            attempt.BindSemanticAudit(notification, new AtomicSemanticAudit(
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
        await attempt.FlushBusinessAsync(ct);

        foreach (var projection in notifications)
        {
            var row = projection.Recipient;
            var notification = projection.Notification;
            attempt.StageOutbox(new OutboxMessage
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
            StageDeliveryOutbox(attempt, row, now);
        }
    }

    private static void StageDeliveryOutbox(
        IAtomicWriteAttempt attempt,
        RentChargeTenantRecipient row,
        DateTime now)
    {
        var title = "Rent charge posted";
        var privateBody = $"A rent charge of {row.Amount:C} was posted to your account and is due {row.DueOn:MMM d, yyyy}.";
        var previewBody = "A rent charge was posted to your Rental Command tenant account.";
        if (row.EnableEmail && !string.IsNullOrWhiteSpace(row.Email))
        {
            attempt.StageOutbox(new OutboxMessage
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
            attempt.StageOutbox(new OutboxMessage
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
            attempt.StageOutbox(new OutboxMessage
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
