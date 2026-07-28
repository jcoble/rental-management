using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Notifications;

public interface ITenantNoticeDraftSetStore
{
    Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateClaimedBatchAsync(
        Guid claimToken,
        CancellationToken ct = default);

    Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default);
}

/// <summary>
/// Generates tenant-notice drafts with one INSERT/SELECT/RETURNING statement. Candidate selection,
/// canonical relationship joins, recipient choice, merge rendering, ordering, and deduplication all
/// remain in PostgreSQL. The filtered unique indexes on NoticeDrafts are the final concurrency gate.
/// </summary>
public sealed class TenantNoticeDraftSetStore : ITenantNoticeDraftSetStore
{
    private readonly RentalCommandDbContext _db;

    public TenantNoticeDraftSetStore(RentalCommandDbContext db) => _db = db;

    public Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateClaimedBatchAsync(
        Guid claimToken,
        CancellationToken ct = default) =>
        ExecuteAsync(
            ClaimedSourceSql,
            AllSourcePropertiesSql,
            [
                Parameter("claim_token", NpgsqlDbType.Uuid, claimToken),
                Parameter("recipient_tenant_id", NpgsqlDbType.Integer, null),
            ],
            ct);

    public Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> GenerateManualAsync(
        WorkspaceReadScope scope,
        int? recipientTenantId,
        int? leaseManagementId,
        int? tenantAccountId,
        long? tenantLedgerEntryId,
        string? noticeType,
        DateTime securityNowUtc,
        CancellationToken ct = default) =>
        ExecuteAsync(
            ManualSourceSql,
            AuthorizedSourcePropertiesSql,
            [
                Parameter("portfolio_id", NpgsqlDbType.Integer, scope.PortfolioId),
                Parameter("user_id", NpgsqlDbType.Integer, scope.UserId),
                Parameter("session_id", NpgsqlDbType.Uuid, scope.SessionId),
                Parameter("access_context_id", NpgsqlDbType.Integer, scope.AccessContextId),
                Parameter("access_revision", NpgsqlDbType.Bigint, scope.AccessRevision),
                Parameter("security_now_utc", NpgsqlDbType.TimestampTz, securityNowUtc),
                Parameter("required_capability", NpgsqlDbType.Text,
                    CapabilityKeys.TenantNoticesManage),
                Parameter("recipient_tenant_id", NpgsqlDbType.Integer, recipientTenantId),
                Parameter("lease_management_id", NpgsqlDbType.Integer, leaseManagementId),
                Parameter("tenant_account_id", NpgsqlDbType.Integer, tenantAccountId),
                Parameter("tenant_ledger_entry_id", NpgsqlDbType.Bigint, tenantLedgerEntryId),
                Parameter("notice_type", NpgsqlDbType.Text,
                    string.IsNullOrWhiteSpace(noticeType) ? null : noticeType.Trim()),
            ],
            ct);

    private async Task<IReadOnlyList<AtomicGeneratedTenantNoticeDraft>> ExecuteAsync(
        string sourceSql,
        string authorizedPropertiesSql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        var rows = await _db.Database
            .SqlQueryRaw<AtomicGeneratedTenantNoticeDraft>(
                BuildSql(sourceSql, authorizedPropertiesSql), parameters)
            .ToListAsync(ct);
        return rows;
    }

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    private const string ClaimedSourceSql = """
        SELECT work."Id" AS "WorkItemId",
               work."PortfolioId",
               work."TenantNoticePolicyId",
               work."LeaseManagementId",
               work."RecipientLeaseManagementPartyId",
               work."TenantLedgerEntryId",
               policy."AutomationKey"
        FROM "TenantNoticeWorkItems" AS work
        INNER JOIN "TenantNoticePolicies" AS policy
          ON policy."Id" = work."TenantNoticePolicyId"
         AND policy."PortfolioId" = work."PortfolioId"
        WHERE work."Status" = 'Claimed'
          AND work."ClaimToken" = @claim_token
        """;

    // Engine batches are already fenced by the claimed work-item token and execute under the
    // engine's workspace RLS context. Interactive generation uses the stricter canonical session,
    // capability, and selected-property CTE below.
    private const string AllSourcePropertiesSql = """
        SELECT property."Id", property."PortfolioId"
        FROM "Properties" AS property
        WHERE property."DeletedAt" IS NULL
        """;

    private const string AuthorizedSourcePropertiesSql = """
        SELECT property."Id", property."PortfolioId"
        FROM "Properties" AS property
        WHERE property."PortfolioId" = @portfolio_id
          AND property."DeletedAt" IS NULL
          AND EXISTS (
            SELECT 1
            FROM "AuthSessions" AS session
            INNER JOIN "WorkspaceAccessContexts" AS context
              ON context."Id" = session."ActiveAccessContextId"
             AND context."UserId" = session."UserId"
            INNER JOIN "WorkspaceMemberships" AS membership
              ON membership."AccessContextId" = context."Id"
             AND membership."PortfolioId" = context."PortfolioId"
            INNER JOIN "MembershipRoleAssignments" AS assignment
              ON assignment."WorkspaceMembershipId" = membership."Id"
             AND assignment."PortfolioId" = membership."PortfolioId"
            INNER JOIN "RoleProfileCapabilities" AS profile_capability
              ON profile_capability."RoleProfileId" = assignment."RoleProfileId"
            INNER JOIN "CapabilityDefinitions" AS capability
              ON capability."Id" = profile_capability."CapabilityDefinitionId"
            WHERE session."Id" = @session_id
              AND session."UserId" = @user_id
              AND session."ActiveAccessContextId" = @access_context_id
              AND session."Status" = 'Active'
              AND session."RevokedAtUtc" IS NULL
              AND session."ExpiresAtUtc" > @security_now_utc
              AND context."Id" = @access_context_id
              AND context."PortfolioId" = property."PortfolioId"
              AND context."AccessRevision" = @access_revision
              AND context."Status" = 'Active'
              AND context."SuspendedAtUtc" IS NULL
              AND context."RevokedAtUtc" IS NULL
              AND membership."Status" = 'Active'
              AND membership."SuspendedAtUtc" IS NULL
              AND membership."RevokedAtUtc" IS NULL
              AND membership."EffectiveFromUtc" <= @security_now_utc
              AND (membership."EffectiveToUtc" IS NULL
                   OR membership."EffectiveToUtc" > @security_now_utc)
              AND assignment."Status" = 'Active'
              AND assignment."SuspendedAtUtc" IS NULL
              AND assignment."RevokedAtUtc" IS NULL
              AND assignment."EffectiveFromUtc" <= @security_now_utc
              AND (assignment."EffectiveToUtc" IS NULL
                   OR assignment."EffectiveToUtc" > @security_now_utc)
              AND capability."Key" = @required_capability
              AND capability."AuthorizationTargetKind" = 'Property'
              AND (
                assignment."ScopeKind" = 'AllProperties'
                OR (
                  assignment."ScopeKind" = 'SelectedProperties'
                  AND EXISTS (
                    SELECT 1
                    FROM "MembershipRoleAssignmentProperties" AS selected
                    WHERE selected."MembershipRoleAssignmentId" = assignment."Id"
                      AND selected."PortfolioId" = assignment."PortfolioId"
                      AND selected."PropertyId" = property."Id"
                  )
                )
              )
          )
        """;

    private const string ManualSourceSql = """
        WITH input AS MATERIALIZED (
          SELECT @portfolio_id::integer AS "PortfolioId",
                 @recipient_tenant_id::integer AS "RecipientTenantId",
                 @lease_management_id::integer AS "LeaseManagementId",
                 @tenant_account_id::integer AS "TenantAccountId",
                 @tenant_ledger_entry_id::bigint AS "TenantLedgerEntryId",
                 nullif(btrim(@notice_type::text), '') AS "NoticeType"
        ),
        relationships AS MATERIALIZED (
          SELECT NULL::bigint AS "WorkItemId",
                 policy."PortfolioId",
                 policy."Id" AS "TenantNoticePolicyId",
                 management."Id" AS "LeaseManagementId",
                 NULL::integer AS "RecipientLeaseManagementPartyId",
                 lifecycle."TenantAccountId",
                 lifecycle."CurrentAgreementId",
                 lifecycle."BusinessDate",
                 policy."AutomationKey",
                 policy."LeadDays",
                 management."EndingDisposition",
                 agreement."TermEndOn"
          FROM input
          INNER JOIN "TenantNoticePolicies" AS policy
            ON policy."PortfolioId" = input."PortfolioId"
           AND policy."Mode" <> 'Off'
           AND (input."NoticeType" IS NULL OR policy."AutomationKey" = input."NoticeType")
          INNER JOIN "LeaseManagements" AS management
            ON management."PortfolioId" = policy."PortfolioId"
           AND (input."LeaseManagementId" IS NULL OR management."Id" = input."LeaseManagementId")
           AND management."CanceledAtUtc" IS NULL
           AND management."PossessionReturnedAtUtc" IS NULL
          INNER JOIN "vw_lease_management_lifecycle" AS lifecycle
            ON lifecycle."PortfolioId" = management."PortfolioId"
           AND lifecycle."LeaseManagementId" = management."Id"
           AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
           AND NOT lifecycle."HasReconciliationException"
           AND lifecycle."TenantAccountId" IS NOT NULL
           AND lifecycle."CurrentAgreementId" IS NOT NULL
           AND (input."TenantAccountId" IS NULL OR lifecycle."TenantAccountId" = input."TenantAccountId")
          INNER JOIN "LeaseAgreements" AS agreement
            ON agreement."PortfolioId" = lifecycle."PortfolioId"
           AND agreement."LeaseManagementId" = lifecycle."LeaseManagementId"
           AND agreement."Id" = lifecycle."CurrentAgreementId"
        ),
        relationship_candidates AS (
          SELECT relationship."WorkItemId",
                 relationship."PortfolioId",
                 relationship."TenantNoticePolicyId",
                 relationship."LeaseManagementId",
                 relationship."RecipientLeaseManagementPartyId",
                 NULL::bigint AS "TenantLedgerEntryId",
                 relationship."AutomationKey"
          FROM relationships AS relationship
          CROSS JOIN input
          WHERE relationship."AutomationKey" IN
              ('lease-renewal-offer', 'month-to-month-offer', 'lease-non-renewal')
            AND input."TenantLedgerEntryId" IS NULL
            AND relationship."TermEndOn" IS NOT NULL
            AND (
              (input."NoticeType" IS NOT NULL
               AND (input."RecipientTenantId" IS NOT NULL
                    OR input."LeaseManagementId" IS NOT NULL
                    OR input."TenantAccountId" IS NOT NULL))
              OR (
                relationship."TermEndOn" - relationship."LeadDays" <= relationship."BusinessDate"
                AND ((relationship."AutomationKey" = 'lease-renewal-offer'
                      AND relationship."EndingDisposition" = 'OfferRenewal')
                  OR (relationship."AutomationKey" = 'month-to-month-offer'
                      AND relationship."EndingDisposition" = 'OfferMonthToMonth')
                  OR (relationship."AutomationKey" = 'lease-non-renewal'
                      AND relationship."EndingDisposition" = 'NonRenewalMoveOut'))
              )
            )
        ),
        ranked_money_candidates AS (
          SELECT relationship."WorkItemId",
                 relationship."PortfolioId",
                 relationship."TenantNoticePolicyId",
                 relationship."LeaseManagementId",
                 relationship."RecipientLeaseManagementPartyId",
                 charge."TenantLedgerEntryId",
                 relationship."AutomationKey",
                 row_number() OVER (
                   PARTITION BY relationship."TenantNoticePolicyId",
                                relationship."LeaseManagementId",
                                relationship."RecipientLeaseManagementPartyId",
                                charge."DueOn"
                   ORDER BY CASE WHEN charge."EntryType" = 'RentCharge' THEN 0 ELSE 1 END,
                            charge."OpenAmount" DESC,
                            charge."TenantLedgerEntryId"
                 ) AS candidate_rank
          FROM relationships AS relationship
          CROSS JOIN input
          INNER JOIN "vw_tenant_charge_balances" AS charge
            ON charge."PortfolioId" = relationship."PortfolioId"
           AND charge."TenantAccountId" = relationship."TenantAccountId"
           AND (input."TenantLedgerEntryId" IS NULL
                OR charge."TenantLedgerEntryId" = input."TenantLedgerEntryId")
          WHERE charge."DueOn" IS NOT NULL
            AND charge."OpenAmount" > 0
            AND ((relationship."AutomationKey" = 'rent-reminder'
                  AND charge."EntryType" = 'RentCharge'
                  AND charge."DueOn" >= relationship."BusinessDate"
                  AND (input."TenantLedgerEntryId" IS NOT NULL
                       OR charge."DueOn" - relationship."LeadDays" <= relationship."BusinessDate"))
              OR (relationship."AutomationKey" = 'late-rent-late-fee'
                  AND charge."EntryType" IN ('RentCharge', 'LateFeeCharge')
                  AND charge."IsPastDue"))
        )
        SELECT * FROM relationship_candidates
        UNION ALL
        SELECT money."WorkItemId",
               money."PortfolioId",
               money."TenantNoticePolicyId",
               money."LeaseManagementId",
               money."RecipientLeaseManagementPartyId",
               money."TenantLedgerEntryId",
               money."AutomationKey"
        FROM ranked_money_candidates AS money
        WHERE money.candidate_rank = 1
        """;

    private const string TenantNameToken = "{{tenant_name}}";
    private const string PropertyAddressToken = "{{property_address}}";
    private const string UnitNumberToken = "{{unit_number}}";
    private const string LeaseStartDateToken = "{{lease_start_date}}";
    private const string LeaseEndDateToken = "{{lease_end_date}}";
    private const string RentAmountToken = "{{rent_amount}}";
    private const string OverdueAmountToken = "{{overdue_amount}}";
    private const string RentDueDateToken = "{{rent_due_date}}";
    private const string LateFeeAmountToken = "{{late_fee_amount}}";
    private const string TodayToken = "{{today}}";
    private const string PortfolioNameToken = "{{portfolio_name}}";
    private const string RenewalStartDateToken = "{{renewal_start_date}}";

    private static string BuildSql(string sourceSql, string authorizedPropertiesSql) => $$"""
        WITH authorized_properties AS MATERIALIZED (
        {{authorizedPropertiesSql}}
        ),
        source_candidates AS MATERIALIZED (
        {{sourceSql}}
        ),
        canonical_candidates AS MATERIALIZED (
          SELECT source."WorkItemId",
                 source."PortfolioId",
                 source."TenantNoticePolicyId",
                 source."LeaseManagementId",
                 source."TenantLedgerEntryId",
                 source."AutomationKey",
                 policy."WorkspaceNoticeTemplateVersionId",
                 lifecycle."TenantAccountId",
                 lifecycle."CurrentAgreementId",
                 lifecycle."BusinessDate",
                 lifecycle."EffectiveNowUtc",
                 management."PropertyId",
                 management."UnitId",
                 portfolio."Name" AS "PortfolioName",
                 portfolio."TimeZone",
                 property."Name" AS "PropertyName",
                 unit."UnitNumber",
                 agreement."TermStartOn",
                 agreement."TermEndOn",
                 agreement."BaseRentAmount",
                 recipient."PartyId" AS "RecipientLeaseManagementPartyId",
                 recipient."TenantId" AS "RecipientTenantId",
                 recipient."TenantName",
                 ledger."LeaseAgreementId" AS "LedgerLeaseAgreementId",
                 ledger."LeaseAddendumId",
                 charge."EntryType",
                 charge."DueOn",
                 charge."OpenAmount",
                 COALESCE(related_fee."LateFeeAmount", 0) AS "LateFeeAmount",
                 template."Subject" AS "TemplateSubject",
                 template."Body" AS "TemplateBody"
          FROM source_candidates AS source
          INNER JOIN "TenantNoticePolicies" AS policy
            ON policy."PortfolioId" = source."PortfolioId"
           AND policy."Id" = source."TenantNoticePolicyId"
           AND policy."AutomationKey" = source."AutomationKey"
           AND policy."Mode" <> 'Off'
          INNER JOIN "WorkspaceNoticeTemplateVersions" AS template
            ON template."PortfolioId" = policy."PortfolioId"
           AND template."Id" = policy."WorkspaceNoticeTemplateVersionId"
           AND template."SystemKey" = policy."AutomationKey"
          INNER JOIN "LeaseManagements" AS management
            ON management."PortfolioId" = source."PortfolioId"
           AND management."Id" = source."LeaseManagementId"
           AND management."CanceledAtUtc" IS NULL
           AND management."PossessionReturnedAtUtc" IS NULL
          INNER JOIN "vw_lease_management_lifecycle" AS lifecycle
            ON lifecycle."PortfolioId" = management."PortfolioId"
           AND lifecycle."LeaseManagementId" = management."Id"
           AND lifecycle."TenantAccountId" IS NOT NULL
           AND lifecycle."CurrentAgreementId" IS NOT NULL
           AND NOT lifecycle."HasReconciliationException"
          INNER JOIN "TenantAccounts" AS account
            ON account."PortfolioId" = lifecycle."PortfolioId"
           AND account."LeaseManagementId" = lifecycle."LeaseManagementId"
           AND account."Id" = lifecycle."TenantAccountId"
           AND account."ClosedAtUtc" IS NULL
          INNER JOIN "LeaseAgreements" AS agreement
            ON agreement."PortfolioId" = lifecycle."PortfolioId"
           AND agreement."LeaseManagementId" = lifecycle."LeaseManagementId"
           AND agreement."Id" = lifecycle."CurrentAgreementId"
          INNER JOIN "Properties" AS property
            ON property."PortfolioId" = management."PortfolioId"
           AND property."Id" = management."PropertyId"
           AND property."DeletedAt" IS NULL
          INNER JOIN authorized_properties AS authorized_property
            ON authorized_property."PortfolioId" = property."PortfolioId"
           AND authorized_property."Id" = property."Id"
          INNER JOIN "Units" AS unit
            ON unit."PortfolioId" = management."PortfolioId"
           AND unit."PropertyId" = management."PropertyId"
           AND unit."Id" = management."UnitId"
           AND unit."DeletedAt" IS NULL
          INNER JOIN "Portfolios" AS portfolio
            ON portfolio."Id" = source."PortfolioId"
           AND portfolio."DeletedAt" IS NULL
          INNER JOIN LATERAL (
            SELECT party."Id" AS "PartyId",
                   tenant."Id" AS "TenantId",
                   btrim(concat_ws(' ', tenant."FirstName", tenant."LastName")) AS "TenantName"
            FROM "LeaseManagementParties" AS party
            INNER JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
             AND tenant."DeletedAt" IS NULL
            WHERE party."PortfolioId" = lifecycle."PortfolioId"
              AND party."LeaseManagementId" = lifecycle."LeaseManagementId"
              AND party."EffectiveFrom" <= lifecycle."BusinessDate"
              AND (party."EffectiveThrough" IS NULL
                   OR party."EffectiveThrough" >= lifecycle."BusinessDate")
              AND (source."RecipientLeaseManagementPartyId" IS NULL
                   OR party."Id" = source."RecipientLeaseManagementPartyId")
              AND ((party."Role" = 'PrimaryTenant' AND policy."IncludePrimaryTenant")
                OR (party."Role" = 'CoTenant' AND policy."IncludeCoTenant")
                OR (party."Role" = 'Guarantor' AND policy."IncludeEligibleGuarantor"
                    AND policy."Classification" = 'Legal'
                    AND party."GuarantorLegalNoticeEligible")
                OR (party."Role" = 'Occupant' AND policy."IncludeOccupant"
                    AND policy."Classification" <> 'Legal'
                    AND policy."AutomationKey" NOT IN ('rent-reminder', 'late-rent-late-fee')))
              AND (@recipient_tenant_id IS NULL OR tenant."Id" = @recipient_tenant_id)
          ) AS recipient ON TRUE
          LEFT JOIN "TenantLedgerEntries" AS ledger
            ON ledger."PortfolioId" = source."PortfolioId"
           AND ledger."TenantAccountId" = lifecycle."TenantAccountId"
           AND ledger."Id" = source."TenantLedgerEntryId"
          LEFT JOIN "vw_tenant_charge_balances" AS charge
            ON charge."PortfolioId" = ledger."PortfolioId"
           AND charge."TenantAccountId" = ledger."TenantAccountId"
           AND charge."TenantLedgerEntryId" = ledger."Id"
          LEFT JOIN LATERAL (
            SELECT COALESCE(sum(fee."OpenAmount"), 0) AS "LateFeeAmount"
            FROM "vw_tenant_charge_balances" AS fee
            WHERE fee."PortfolioId" = charge."PortfolioId"
              AND fee."TenantAccountId" = charge."TenantAccountId"
              AND fee."EntryType" = 'LateFeeCharge'
              AND fee."DueOn" = charge."DueOn"
              AND fee."OpenAmount" > 0
              AND charge."EntryType" = 'RentCharge'
          ) AS related_fee ON TRUE
          WHERE (source."TenantLedgerEntryId" IS NULL
                 AND source."AutomationKey" IN
                     ('lease-renewal-offer', 'month-to-month-offer', 'lease-non-renewal'))
             OR (source."TenantLedgerEntryId" IS NOT NULL
                 AND charge."TenantLedgerEntryId" IS NOT NULL
                 AND charge."OpenAmount" > 0
                 AND ((source."AutomationKey" = 'rent-reminder'
                       AND charge."EntryType" = 'RentCharge')
                   OR (source."AutomationKey" = 'late-rent-late-fee'
                       AND charge."EntryType" IN ('RentCharge', 'LateFeeCharge'))))
        ),
        token_values AS (
          SELECT candidate.*,
                 candidate."TenantName" AS "RenderedTenantName",
                 concat(candidate."PropertyName",
                        CASE WHEN nullif(btrim(candidate."UnitNumber"), '') IS NULL
                             THEN '' ELSE concat(' Unit ', candidate."UnitNumber") END)
                   AS "PropertyLabel",
                 concat('$', to_char(candidate."BaseRentAmount", 'FM999,999,999,990'))
                   AS "RentAmountText",
                 concat('$', to_char(COALESCE(candidate."OpenAmount", 0)
                                      + candidate."LateFeeAmount", 'FM999,999,999,990'))
                   AS "OverdueAmountText",
                 concat('$', to_char(candidate."LateFeeAmount", 'FM999,999,999,990'))
                   AS "LateFeeAmountText",
                 to_char(candidate."TermStartOn", 'FMMonth FMDD, YYYY') AS "LeaseStartText",
                 COALESCE(to_char(candidate."TermEndOn", 'FMMonth FMDD, YYYY'), '') AS "LeaseEndText",
                 COALESCE(to_char(candidate."DueOn", 'FMMonth FMDD, YYYY'), '') AS "DueOnText",
                 to_char(candidate."BusinessDate", 'FMMonth FMDD, YYYY') AS "TodayText",
                 COALESCE(to_char(candidate."TermEndOn" + 1, 'FMMonth FMDD, YYYY'), '') AS "RenewalStartText"
          FROM canonical_candidates AS candidate
        ),
        rendered_candidates AS (
          SELECT token.*,
                 left(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                   token."TemplateSubject",
                   '{{TenantNameToken}}', token."RenderedTenantName"),
                   '{{PropertyAddressToken}}', token."PropertyLabel"),
                   '{{UnitNumberToken}}', COALESCE(token."UnitNumber", '')),
                   '{{LeaseStartDateToken}}', token."LeaseStartText"),
                   '{{LeaseEndDateToken}}', token."LeaseEndText"),
                   '{{RentAmountToken}}', token."RentAmountText"),
                   '{{OverdueAmountToken}}', token."OverdueAmountText"),
                   '{{RentDueDateToken}}', token."DueOnText"),
                   '{{LateFeeAmountToken}}', token."LateFeeAmountText"),
                   '{{TodayToken}}', token."TodayText"),
                   '{{PortfolioNameToken}}', token."PortfolioName"), 200) AS "RenderedSubject",
                 left(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(replace(
                   token."TemplateBody",
                   '{{TenantNameToken}}', token."RenderedTenantName"),
                   '{{PropertyAddressToken}}', token."PropertyLabel"),
                   '{{UnitNumberToken}}', COALESCE(token."UnitNumber", '')),
                   '{{LeaseStartDateToken}}', token."LeaseStartText"),
                   '{{LeaseEndDateToken}}', token."LeaseEndText"),
                   '{{RentAmountToken}}', token."RentAmountText"),
                   '{{OverdueAmountToken}}', token."OverdueAmountText"),
                   '{{RentDueDateToken}}', token."DueOnText"),
                   '{{LateFeeAmountToken}}', token."LateFeeAmountText"),
                   '{{TodayToken}}', token."TodayText"),
                   '{{PortfolioNameToken}}', token."PortfolioName"),
                   '{{RenewalStartDateToken}}', token."RenewalStartText"), 4000) AS "RenderedBody",
                 CASE token."AutomationKey"
                   WHEN 'rent-reminder' THEN concat('Rent due ', token."DueOnText", '.')
                   WHEN 'late-rent-late-fee' THEN concat(
                     CASE WHEN token."EntryType" = 'LateFeeCharge' THEN 'Late fee' ELSE 'Rent' END,
                     ' is ', GREATEST(0, token."BusinessDate" - token."DueOn"), ' days past due.')
                   WHEN 'lease-renewal-offer' THEN concat('Renewal decision for agreement ending ', token."LeaseEndText", '.')
                   WHEN 'month-to-month-offer' THEN concat('Month-to-month decision for agreement ending ', token."LeaseEndText", '.')
                   ELSE concat('Non-renewal decision for agreement ending ', token."LeaseEndText", '.')
                 END AS "RenderedReason",
                 (COALESCE(token."DueOn", token."TermEndOn", token."BusinessDate")::timestamp
                   AT TIME ZONE token."TimeZone") AS "RenderedTriggerDate",
                 concat(token."PortfolioId", ':', token."AutomationKey", ':', token."LeaseManagementId", ':',
                        'party:', token."RecipientLeaseManagementPartyId", ':',
                        COALESCE(concat('ledger:', token."TenantLedgerEntryId"),
                                 concat('agreement:', token."CurrentAgreementId"))) AS "DedupeKey"
          FROM token_values AS token
        ),
        candidates AS MATERIALIZED (
          SELECT DISTINCT ON (rendered."DedupeKey") rendered.*
          FROM rendered_candidates AS rendered
          WHERE nullif(btrim(rendered."RenderedSubject"), '') IS NOT NULL
            AND nullif(btrim(rendered."RenderedBody"), '') IS NOT NULL
          ORDER BY rendered."DedupeKey", rendered."WorkItemId" NULLS LAST
        ),
        inserted_ledger AS (
          INSERT INTO "NoticeDrafts"
            ("PortfolioId", "LeaseManagementId", "TenantAccountId",
             "RecipientLeaseManagementPartyId", "LeaseAgreementId", "LeaseAddendumId",
             "TenantLedgerEntryId", "PropertyId", "NoticeType", "Status", "Subject", "Body",
             "Reason", "GenerationPrompt", "TriggerDate", "TenantNoticePolicyId",
             "WorkspaceNoticeTemplateVersionId", "CreatedAt", "UpdatedAt")
          SELECT candidate."PortfolioId",
                 candidate."LeaseManagementId",
                 candidate."TenantAccountId",
                 candidate."RecipientLeaseManagementPartyId",
                 COALESCE(candidate."LedgerLeaseAgreementId", candidate."CurrentAgreementId"),
                 candidate."LeaseAddendumId",
                 candidate."TenantLedgerEntryId",
                 candidate."PropertyId",
                 candidate."AutomationKey",
                 'Draft',
                 candidate."RenderedSubject",
                 candidate."RenderedBody",
                 candidate."RenderedReason",
                 NULL,
                 candidate."RenderedTriggerDate",
                 candidate."TenantNoticePolicyId",
                 candidate."WorkspaceNoticeTemplateVersionId",
                 candidate."EffectiveNowUtc",
                 candidate."EffectiveNowUtc"
          FROM candidates AS candidate
          WHERE candidate."TenantLedgerEntryId" IS NOT NULL
          ON CONFLICT ("PortfolioId", "TenantLedgerEntryId", "RecipientLeaseManagementPartyId", "NoticeType")
            WHERE "TenantLedgerEntryId" IS NOT NULL AND "Status" IN ('Draft', 'Approved')
          DO UPDATE SET "UpdatedAt" = "NoticeDrafts"."UpdatedAt"
          RETURNING "NoticeDrafts".*, (xmax = 0) AS "WasCreated"
        ),
        inserted_lifecycle AS (
          INSERT INTO "NoticeDrafts"
            ("PortfolioId", "LeaseManagementId", "TenantAccountId",
             "RecipientLeaseManagementPartyId", "LeaseAgreementId", "LeaseAddendumId",
             "TenantLedgerEntryId", "PropertyId", "NoticeType", "Status", "Subject", "Body",
             "Reason", "GenerationPrompt", "TriggerDate", "TenantNoticePolicyId",
             "WorkspaceNoticeTemplateVersionId", "CreatedAt", "UpdatedAt")
          SELECT candidate."PortfolioId",
                 candidate."LeaseManagementId",
                 candidate."TenantAccountId",
                 candidate."RecipientLeaseManagementPartyId",
                 candidate."CurrentAgreementId",
                 NULL,
                 NULL,
                 candidate."PropertyId",
                 candidate."AutomationKey",
                 'Draft',
                 candidate."RenderedSubject",
                 candidate."RenderedBody",
                 candidate."RenderedReason",
                 NULL,
                 candidate."RenderedTriggerDate",
                 candidate."TenantNoticePolicyId",
                 candidate."WorkspaceNoticeTemplateVersionId",
                 candidate."EffectiveNowUtc",
                 candidate."EffectiveNowUtc"
          FROM candidates AS candidate
          WHERE candidate."TenantLedgerEntryId" IS NULL
          ON CONFLICT ("PortfolioId", "LeaseManagementId", "LeaseAgreementId",
                       "RecipientLeaseManagementPartyId", "NoticeType")
            WHERE "TenantLedgerEntryId" IS NULL
              AND "LeaseAgreementId" IS NOT NULL
              AND "Status" IN ('Draft', 'Approved')
          DO UPDATE SET "UpdatedAt" = "NoticeDrafts"."UpdatedAt"
          RETURNING "NoticeDrafts".*, (xmax = 0) AS "WasCreated"
        ),
        upserted AS (
          SELECT * FROM inserted_ledger
          UNION ALL
          SELECT * FROM inserted_lifecycle
        ),
        resolved AS (
          SELECT candidate."WorkItemId",
                 candidate."EffectiveNowUtc" AS "AppliedAtUtc",
                 upserted.*
          FROM candidates AS candidate
          INNER JOIN upserted
            ON upserted."PortfolioId" = candidate."PortfolioId"
           AND upserted."NoticeType" = candidate."AutomationKey"
           AND upserted."LeaseManagementId" = candidate."LeaseManagementId"
           AND upserted."RecipientLeaseManagementPartyId" = candidate."RecipientLeaseManagementPartyId"
           AND upserted."LeaseAgreementId" = COALESCE(candidate."LedgerLeaseAgreementId", candidate."CurrentAgreementId")
           AND upserted."TenantLedgerEntryId" IS NOT DISTINCT FROM candidate."TenantLedgerEntryId"
        )
        SELECT resolved."WorkItemId",
               resolved."WasCreated",
               sum(CASE WHEN resolved."WasCreated" THEN 1 ELSE 0 END) OVER ()::integer AS "CreatedCount",
               resolved."Id" AS "DraftId",
               resolved."PortfolioId",
               resolved."LeaseManagementId",
               resolved."TenantAccountId",
               resolved."RecipientLeaseManagementPartyId",
               resolved."LeaseAgreementId",
               resolved."LeaseAddendumId",
               resolved."TenantLedgerEntryId",
               tenant."Id" AS "RecipientTenantId",
               resolved."PropertyId",
               btrim(concat_ws(' ', tenant."FirstName", tenant."LastName")) AS "TenantName",
               property."Name" AS "PropertyName",
               unit."UnitNumber",
               resolved."NoticeType",
               resolved."Status",
               resolved."Subject",
               resolved."Body",
               resolved."Reason",
               resolved."TriggerDate",
               resolved."AppliedAtUtc",
               resolved."ConversationId",
               resolved."ApprovedChannels",
               resolved."CreatedAt",
               resolved."UpdatedAt",
               resolved."ApprovedAt",
               resolved."DismissedAt"
        FROM resolved
        INNER JOIN "LeaseManagementParties" AS party
          ON party."PortfolioId" = resolved."PortfolioId"
         AND party."LeaseManagementId" = resolved."LeaseManagementId"
         AND party."Id" = resolved."RecipientLeaseManagementPartyId"
        INNER JOIN "Tenants" AS tenant
          ON tenant."PortfolioId" = party."PortfolioId"
         AND tenant."Id" = party."TenantId"
        INNER JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = resolved."PortfolioId"
         AND management."Id" = resolved."LeaseManagementId"
        INNER JOIN "Units" AS unit
          ON unit."PortfolioId" = management."PortfolioId"
         AND unit."Id" = management."UnitId"
        LEFT JOIN "Properties" AS property
          ON property."PortfolioId" = resolved."PortfolioId"
         AND property."Id" = resolved."PropertyId"
        ORDER BY resolved."TriggerDate", resolved."Id";
        """;
}
