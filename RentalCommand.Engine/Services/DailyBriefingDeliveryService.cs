using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Sends the landlord/team morning briefing without a free-form recipient list. PostgreSQL resolves
/// due workspaces, current routed recipients, preferences, destinations, and each recipient's
/// current authorized action list from the canonical briefing projection in one set-based query.
/// </summary>
public sealed class DailyBriefingDeliveryService : IDailyBriefingDeliveryService
{
    private const string Purpose = "morning-briefing";
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DailyBriefingDeliveryService> _logger;

    public DailyBriefingDeliveryService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        ILogger<DailyBriefingDeliveryService> logger)
    {
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> EnqueueDueAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = DateTime.SpecifyKind(utcNow ?? _timeProvider.UtcNow(), DateTimeKind.Utc);
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var digests = await _db.Database.SqlQuery<MorningBriefingDigestRow>($"""
            WITH due_rules AS MATERIALIZED (
                SELECT rule."Id" AS rule_id,
                       rule."PortfolioId" AS portfolio_id,
                       portfolio."Name" AS portfolio_name,
                       settings."MorningBriefingIncludeEmpty" AS include_empty,
                       COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York') AS time_zone,
                       to_char(
                           {now} AT TIME ZONE COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York'),
                           'YYYY-MM-DD') AS local_date
                FROM "AutomationSettings" AS settings
                JOIN "Portfolios" AS portfolio
                  ON portfolio."Id" = settings."PortfolioId"
                 AND portfolio."DeletedAt" IS NULL
                JOIN "TeamRoutingRules" AS rule
                  ON rule."PortfolioId" = settings."PortfolioId"
                 AND rule."Topic" = 'MorningBriefing'
                 AND rule."PropertyId" IS NULL
                WHERE settings."EnableMorningBriefing"
                  AND EXTRACT(hour FROM
                      {now} AT TIME ZONE COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York'))::integer
                      >= settings."MorningBriefingSendHourLocal"
            ), active_members AS MATERIALIZED (
                SELECT DISTINCT context."PortfolioId" AS portfolio_id,
                       context."UserId" AS user_id,
                       assignment."Id" AS assignment_id,
                       assignment."RoleProfileId" AS role_profile_id,
                       assignment."ScopeKind" AS scope_kind
                FROM "WorkspaceAccessContexts" AS context
                JOIN "WorkspaceMemberships" AS membership
                  ON membership."AccessContextId" = context."Id"
                 AND membership."PortfolioId" = context."PortfolioId"
                JOIN "MembershipRoleAssignments" AS assignment
                  ON assignment."WorkspaceMembershipId" = membership."Id"
                 AND assignment."PortfolioId" = membership."PortfolioId"
                WHERE context."Status" = 'Active'
                  AND context."SuspendedAtUtc" IS NULL
                  AND context."RevokedAtUtc" IS NULL
                  AND membership."Status" = 'Active'
                  AND membership."SuspendedAtUtc" IS NULL
                  AND membership."RevokedAtUtc" IS NULL
                  AND membership."EffectiveFromUtc" <= {now}
                  AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > {now})
                  AND assignment."Status" = 'Active'
                  AND assignment."SuspendedAtUtc" IS NULL
                  AND assignment."RevokedAtUtc" IS NULL
                  AND assignment."EffectiveFromUtc" <= {now}
                  AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > {now})
            ), eligible_properties AS MATERIALIZED (
                SELECT DISTINCT member.portfolio_id, member.user_id, property."Id" AS property_id,
                       capability."Key" AS capability_key
                FROM active_members AS member
                JOIN "RoleProfileCapabilities" AS profile_capability
                  ON profile_capability."RoleProfileId" = member.role_profile_id
                JOIN "CapabilityDefinitions" AS capability
                  ON capability."Id" = profile_capability."CapabilityDefinitionId"
                 AND capability."AuthorizationTargetKind" = 'Property'
                 AND capability."Key" IN ('rentals.read', 'work.read', 'money.balances.read',
                                            'leasing.showings.manage', 'leasing.onboarding.manage')
                JOIN "Properties" AS property
                  ON property."PortfolioId" = member.portfolio_id
                 AND property."DeletedAt" IS NULL
                 AND (member.scope_kind = 'AllProperties'
                      OR (member.scope_kind = 'SelectedProperties' AND EXISTS (
                          SELECT 1 FROM "MembershipRoleAssignmentProperties" AS selected
                          WHERE selected."MembershipRoleAssignmentId" = member.assignment_id
                            AND selected."PortfolioId" = member.portfolio_id
                            AND selected."PropertyId" = property."Id")))
            ), explicit_recipients AS MATERIALIZED (
                SELECT DISTINCT due.rule_id, due.portfolio_id, due.portfolio_name,
                       due.include_empty, due.time_zone, due.local_date, recipient."UserId" AS user_id
                FROM due_rules AS due
                JOIN "TeamRoutingRuleRecipients" AS recipient
                  ON recipient."TeamRoutingRuleId" = due.rule_id
                 AND recipient."PortfolioId" = due.portfolio_id
                JOIN active_members AS member
                  ON member.portfolio_id = due.portfolio_id
                 AND member.user_id = recipient."UserId"
                WHERE EXISTS (SELECT 1 FROM eligible_properties AS eligible
                              WHERE eligible.portfolio_id = due.portfolio_id
                                AND eligible.user_id = recipient."UserId")
            ), routed_recipients AS MATERIALIZED (
                SELECT * FROM explicit_recipients
                UNION
                SELECT DISTINCT due.rule_id, due.portfolio_id, due.portfolio_name,
                       due.include_empty, due.time_zone, due.local_date, member.user_id
                FROM due_rules AS due
                JOIN "TeamRoutingRules" AS rule ON rule."Id" = due.rule_id
                JOIN active_members AS member ON member.portfolio_id = due.portfolio_id
                JOIN "RoleProfiles" AS profile
                  ON profile."Id" = member.role_profile_id
                 AND profile."Key" = 'workspace-administrator'
                WHERE rule."UseWorkspaceAdministratorFallback"
                  AND NOT EXISTS (
                      SELECT 1 FROM explicit_recipients AS explicit
                      WHERE explicit.rule_id = due.rule_id)
            ), digests AS MATERIALIZED (
                SELECT routed.portfolio_id,
                       routed.portfolio_name,
                       routed.user_id,
                       routed.local_date,
                       routed.include_empty,
                       user_row."Email" AS email,
                       user_row."PhoneNumber" AS phone_number,
                       COALESCE(preference."EnableEmail", TRUE) AS enable_email,
                       COALESCE(preference."EnableSms", FALSE) AS enable_sms,
                       COALESCE(preference."EnableMobilePush", TRUE) AS enable_push,
                       COALESCE(candidate_rows.item_count, 0)::integer AS item_count,
                       COALESCE(candidate_rows.items_json, '[]'::jsonb)::text AS items_json,
                       COALESCE(device_rows.tokens_json, '[]'::jsonb)::text AS device_tokens_json
                FROM routed_recipients AS routed
                JOIN "AspNetUsers" AS user_row ON user_row."Id" = routed.user_id
                LEFT JOIN "UserAlertPreferences" AS preference
                  ON preference."PortfolioId" = routed.portfolio_id
                 AND preference."UserId" = routed.user_id
                LEFT JOIN LATERAL (
                    SELECT COUNT(*)::integer AS item_count,
                           jsonb_agg(jsonb_build_object(
                               'category', item."Category", 'severityOrder', item."EffectiveSeverityOrder",
                               'entityType', item."EntityType", 'entityId', item."EntityId", 'unitId', item."UnitId",
                               'titleText', item."TitleText", 'detailText', item."DetailText",
                               'leaseNumber', item."LeaseNumber", 'unitNumber', item."UnitNumber",
                               'tenantName', item."TenantName", 'propertyName', item."PropertyName",
                               'amount', item."Amount", 'eventDateTime', item."EventDateTime",
                               'eventDateOnly', item."EventDateOnly", 'typeValue', item."TypeValue")
                               ORDER BY item."EffectiveSeverityOrder", item."SortOrder",
                                        item."EventDateOnly", item."EventDateTime", item."EntityId") AS items_json
                    FROM (
                        SELECT candidate.*,
                               CASE
                                 WHEN candidate."Category" = 'RentLate'
                                      AND candidate."EventDateOnly" <= routed.local_date::date - 5 THEN 0
                                 WHEN candidate."Category" = 'Inspection'
                                      AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date
                                          < routed.local_date::date + 2 THEN 1
                                 ELSE candidate."SeverityOrder"
                               END AS "EffectiveSeverityOrder"
                        FROM "vw_morning_briefing_candidates" AS candidate
                        JOIN eligible_properties AS eligible
                          ON eligible.portfolio_id = candidate."PortfolioId"
                         AND eligible.user_id = routed.user_id
                         AND eligible.property_id = candidate."PropertyId"
                         AND eligible.capability_key = candidate."RequiredCapability"
                        WHERE candidate."PortfolioId" = routed.portfolio_id
                          AND (candidate."Category" = 'Maintenance'
                            OR (candidate."Category" = 'RentLate'
                                AND candidate."EventDateOnly" < routed.local_date::date)
                            OR (candidate."Category" = 'RentDue'
                                AND candidate."EventDateOnly" = routed.local_date::date)
                            OR (candidate."Category" = 'Appointment'
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date = routed.local_date::date)
                            OR (candidate."Category" = 'Inspection'
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date >= routed.local_date::date
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date < routed.local_date::date + 8)
                            OR (candidate."Category" = 'LeaseExpiring'
                                AND candidate."EventDateOnly" > routed.local_date::date
                                AND candidate."EventDateOnly" <= routed.local_date::date + 60))
                        ORDER BY "EffectiveSeverityOrder", candidate."SortOrder",
                                 candidate."EventDateOnly", candidate."EventDateTime", candidate."EntityId"
                        LIMIT 25
                    ) AS item
                ) AS candidate_rows ON TRUE
                LEFT JOIN LATERAL (
                    SELECT jsonb_agg(DISTINCT token."Token") AS tokens_json
                    FROM "DeviceTokens" AS token
                    WHERE token."PortfolioId" = routed.portfolio_id
                      AND token."UserId" = routed.user_id
                ) AS device_rows ON TRUE
            )
            SELECT portfolio_id AS "PortfolioId",
                   portfolio_name AS "PortfolioName",
                   user_id AS "UserId",
                   local_date AS "LocalDate",
                   email AS "Email",
                   phone_number AS "PhoneNumber",
                   enable_email AS "EnableEmail",
                   enable_sms AS "EnableSms",
                   enable_push AS "EnablePush",
                   item_count AS "ItemCount",
                   items_json AS "ItemsJson",
                   device_tokens_json AS "DeviceTokensJson"
            FROM digests
            WHERE (include_empty OR item_count > 0)
              AND NOT EXISTS (
                  SELECT 1
                  FROM "OutboxMessages" AS sent
                  WHERE sent."IdempotencyKey" LIKE
                      'morning-briefing:' || portfolio_id::text || ':' || user_id::text || ':' || local_date || ':%')
            ORDER BY portfolio_id, user_id
            """).ToListAsync(ct);

        var queued = 0;
        foreach (var digest in digests)
        {
            var items = JsonSerializer.Deserialize<List<BriefingDigestItem>>(
                digest.ItemsJson,
                JsonOptions) ?? [];
            var body = ComposeBody(digest.PortfolioName, digest.LocalDate, items);
            var subject = $"Rental Command morning briefing for {digest.LocalDate}";

            if (digest.EnableEmail && !string.IsNullOrWhiteSpace(digest.Email))
            {
                AddOutbox(digest.PortfolioId, "email", digest.UserId, digest.LocalDate,
                    digest.Email, new { to = digest.Email, subject, body }, now);
                queued++;
            }

            if (digest.EnableSms && !string.IsNullOrWhiteSpace(digest.PhoneNumber))
            {
                AddOutbox(digest.PortfolioId, "sms", digest.UserId, digest.LocalDate,
                    digest.PhoneNumber, new { to = digest.PhoneNumber, message = body }, now);
                queued++;
            }

            if (digest.EnablePush)
            {
                var tokens = JsonSerializer.Deserialize<List<string>>(digest.DeviceTokensJson) ?? [];
                foreach (var token in tokens)
                {
                    AddOutbox(digest.PortfolioId, "push", digest.UserId, digest.LocalDate,
                        token, new
                        {
                            deviceToken = token,
                            title = subject,
                            body,
                            actionUrl = "/today",
                            type = "MorningBriefing",
                        }, now);
                    queued++;
                }
            }
        }

        if (queued > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (queued > 0)
        {
            _logger.LogInformation("Queued {Count} morning briefing delivery or deliveries.", queued);
        }

        return queued;
    }

    private void AddOutbox(
        int portfolioId,
        string channel,
        int userId,
        string localDate,
        string destination,
        object payload,
        DateTime now)
    {
        _db.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = channel,
            Payload = JsonSerializer.Serialize(payload),
            IdempotencyKey = $"{Purpose}:{portfolioId}:{userId}:{localDate}:{channel}:{DestinationHash(destination)}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static string DestinationHash(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Trim())))
            .ToLowerInvariant()[..24];

    private static string ComposeBody(
        string portfolioName,
        string localDate,
        IReadOnlyList<BriefingDigestItem> items)
    {
        var body = new StringBuilder()
            .Append("Rental Command - ")
            .Append(portfolioName)
            .Append(" briefing for ")
            .Append(localDate)
            .Append(':');

        if (items.Count == 0)
        {
            return body.Append(" Nothing currently needs your attention.").ToString();
        }

        foreach (var item in items)
        {
            var (title, detail) = FormatItem(item, DateOnly.Parse(localDate));
            body.AppendLine().Append("- ").Append(title);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                body.Append(": ").Append(detail);
            }
        }

        return body.ToString();
    }

    private static (string Title, string Detail) FormatItem(BriefingDigestItem item, DateOnly today)
    {
        var date = item.EventDateOnly ?? (item.EventDateTime is DateTime timestamp
            ? DateOnly.FromDateTime(timestamp)
            : today);
        var rental = string.IsNullOrWhiteSpace(item.TenantName)
            ? $"{item.PropertyName}, Unit {item.UnitNumber}"
            : $"{item.TenantName} - {item.PropertyName}, Unit {item.UnitNumber}";
        return item.Category switch
        {
            "Maintenance" => ($"Emergency: {item.TitleText}", item.DetailText ?? string.Empty),
            "RentLate" => ($"Rent overdue - {rental}",
                $"${item.Amount:N0} was due {today.DayNumber - date.DayNumber} day(s) ago"),
            "RentDue" => ($"Rent due today - {rental}", $"${item.Amount:N0} due"),
            "Appointment" => ($"Appointment today: {item.TitleText}",
                item.EventDateTime is DateTime at
                    ? $"{(AppointmentType)item.TypeValue} at {at:h:mm tt}"
                    : ((AppointmentType)item.TypeValue).ToString()),
            "Inspection" => ($"Inspection - {(InspectionType)item.TypeValue}", $"Scheduled for {date:MMM d}"),
            "LeaseExpiring" => ($"Lease expiring - {rental}",
                $"Ends {date:MMM d} ({date.DayNumber - today.DayNumber} day(s) left)"),
            _ => (item.TitleText ?? item.Category, item.DetailText ?? string.Empty),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class MorningBriefingDigestRow
    {
        public int PortfolioId { get; set; }
        public string PortfolioName { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string LocalDate { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? PhoneNumber { get; set; }
        public bool EnableEmail { get; set; }
        public bool EnableSms { get; set; }
        public bool EnablePush { get; set; }
        public int ItemCount { get; set; }
        public string ItemsJson { get; set; } = "[]";
        public string DeviceTokensJson { get; set; } = "[]";
    }

    private sealed record BriefingDigestItem(
        string Category,
        int SeverityOrder,
        string EntityType,
        int EntityId,
        int? UnitId,
        string? TitleText,
        string? DetailText,
        string? LeaseNumber,
        string? UnitNumber,
        string? TenantName,
        string? PropertyName,
        decimal Amount,
        DateTime? EventDateTime,
        DateOnly? EventDateOnly,
        int TypeValue);
}
