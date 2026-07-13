using Microsoft.EntityFrameworkCore;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due lease-ending automations into the canonical tenant-notice work queue.
/// Candidate eligibility, channel/recipient eligibility, deduplication, and insertion are one
/// PostgreSQL statement over authoritative lease projections. This service never sends a message.
/// </summary>
public sealed class TenantNoticeCandidateGenerationService : ITenantNoticeCandidateGenerationService
{
    internal const string CandidateInsertSql = """
        INSERT INTO "TenantNoticeWorkItems"
            ("PortfolioId", "TenantNoticePolicyId", "LeaseManagementId", "DueAtUtc",
             "Status", "BusinessKey", "AttemptCount", "CreatedAtUtc")
        SELECT policy."PortfolioId",
               policy."Id",
               management."Id",
               due_time."DueAtUtc",
               'Pending',
               concat('tenant-notice:', policy."PortfolioId", ':', policy."Id", ':',
                      management."Id", ':', agreement."Id", ':', agreement."TermEndOn"),
               0,
               clock_timestamp()
        FROM "TenantNoticePolicies" AS policy
        INNER JOIN "LeaseManagements" AS management
          ON management."PortfolioId" = policy."PortfolioId"
        INNER JOIN "vw_lease_management_lifecycle" AS lifecycle
          ON lifecycle."PortfolioId" = management."PortfolioId"
         AND lifecycle."LeaseManagementId" = management."Id"
        INNER JOIN "vw_lease_agreement_status" AS agreement_status
          ON agreement_status."PortfolioId" = lifecycle."PortfolioId"
         AND agreement_status."LeaseManagementId" = lifecycle."LeaseManagementId"
         AND agreement_status."AgreementId" = lifecycle."CurrentAgreementId"
         AND agreement_status."IsGoverning"
        INNER JOIN "LeaseAgreements" AS agreement
          ON agreement."PortfolioId" = agreement_status."PortfolioId"
         AND agreement."LeaseManagementId" = agreement_status."LeaseManagementId"
         AND agreement."Id" = agreement_status."AgreementId"
        INNER JOIN "TenantAccounts" AS account
          ON account."PortfolioId" = lifecycle."PortfolioId"
         AND account."LeaseManagementId" = lifecycle."LeaseManagementId"
         AND account."Id" = lifecycle."TenantAccountId"
         AND account."ClosedAtUtc" IS NULL
        INNER JOIN "Portfolios" AS portfolio
          ON portfolio."Id" = policy."PortfolioId"
         AND portfolio."DeletedAt" IS NULL
        CROSS JOIN LATERAL (
          SELECT ((agreement."TermEndOn" - policy."LeadDays"
                    + policy."SendHourLocal" * interval '1 hour')
                   AT TIME ZONE portfolio."TimeZone") AS "DueAtUtc"
        ) AS due_time
        WHERE policy."Mode" <> 'Off'
          AND agreement."TermEndOn" IS NOT NULL
          AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
          AND NOT lifecycle."HasReconciliationException"
          AND management."CanceledAtUtc" IS NULL
          AND management."PossessionReturnedAtUtc" IS NULL
          AND ((management."EndingDisposition" = 'OfferRenewal'
                AND policy."AutomationKey" = 'lease-renewal-offer')
            OR (management."EndingDisposition" = 'OfferMonthToMonth'
                AND policy."AutomationKey" = 'month-to-month-offer')
            OR (management."EndingDisposition" = 'NonRenewalMoveOut'
                AND policy."AutomationKey" = 'lease-non-renewal'))
          AND due_time."DueAtUtc" <= lifecycle."EffectiveNowUtc"
          AND EXISTS (
            SELECT 1
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
              AND ((party."Role" = 'PrimaryTenant' AND policy."IncludePrimaryTenant")
                OR (party."Role" = 'CoTenant' AND policy."IncludeCoTenant")
                OR (party."Role" = 'Guarantor' AND policy."IncludeEligibleGuarantor"
                    AND party."GuarantorLegalNoticeEligible")
                OR (party."Role" = 'Occupant' AND policy."IncludeOccupant"))
              AND ((policy."SendEmail" AND nullif(btrim(tenant."Email"), '') IS NOT NULL)
                OR (policy."SendSms" AND nullif(btrim(tenant."Phone"), '') IS NOT NULL)
                OR ((policy."SendTenantPortal" OR policy."SendMobilePush") AND EXISTS (
                    SELECT 1
                    FROM "TenantUserAccesses" AS tenant_access
                    INNER JOIN "WorkspaceAccessContexts" AS access_context
                      ON access_context."Id" = tenant_access."AccessContextId"
                     AND access_context."UserId" = tenant_access."ApplicationUserId"
                     AND access_context."PortfolioId" = tenant_access."PortfolioId"
                    WHERE tenant_access."PortfolioId" = party."PortfolioId"
                      AND tenant_access."LeaseManagementPartyId" = party."Id"
                      AND tenant_access."RevokedAtUtc" IS NULL
                      AND access_context."Status" = 'Active'
                      AND access_context."SuspendedAtUtc" IS NULL
                      AND access_context."RevokedAtUtc" IS NULL)))
          )
        ON CONFLICT ("BusinessKey") DO NOTHING;
        """;

    private readonly RentalCommandDbContext _db;

    public TenantNoticeCandidateGenerationService(RentalCommandDbContext db) => _db = db;

    public Task<int> GenerateDueAsync(CancellationToken ct = default) =>
        _db.Database.ExecuteSqlRawAsync(CandidateInsertSql, ct);
}
