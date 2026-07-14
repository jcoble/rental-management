using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Data;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Materializes due lease-lifecycle and rent-balance automations into the canonical tenant-notice work queue.
/// Candidate eligibility, channel/recipient eligibility, deduplication, and insertion are one
/// PostgreSQL statement over authoritative lease projections. This service never sends a message.
/// </summary>
public sealed class TenantNoticeCandidateGenerationService : ITenantNoticeCandidateGenerationService
{
    internal const string CandidateInsertSql = """
        WITH eligible_policy_relationships AS MATERIALIZED (
          SELECT policy."PortfolioId",
                 policy."Id" AS "TenantNoticePolicyId",
                 policy."AutomationKey",
                 policy."LeadDays",
                 policy."SendHourLocal",
                 management."Id" AS "LeaseManagementId",
                 management."EndingDisposition",
                 lifecycle."TenantAccountId",
                 lifecycle."CurrentAgreementId",
                 lifecycle."BusinessDate",
                 lifecycle."EffectiveNowUtc",
                 agreement."TermEndOn",
                 portfolio."TimeZone"
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
          WHERE policy."Mode" <> 'Off'
            AND lifecycle."Lifecycle" IN ('Occupied', 'Ending')
            AND NOT lifecycle."HasReconciliationException"
            AND management."CanceledAtUtc" IS NULL
            AND management."PossessionReturnedAtUtc" IS NULL
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
        ),
        lease_end_candidates AS (
          SELECT relationship."PortfolioId",
                 relationship."TenantNoticePolicyId",
                 relationship."LeaseManagementId",
                 NULL::bigint AS "TenantLedgerEntryId",
                 ((relationship."TermEndOn" - relationship."LeadDays"
                    + relationship."SendHourLocal" * interval '1 hour')
                   AT TIME ZONE relationship."TimeZone") AS "DueAtUtc",
                 concat('tenant-notice:', relationship."PortfolioId", ':',
                        relationship."TenantNoticePolicyId", ':', relationship."LeaseManagementId",
                        ':agreement:', relationship."CurrentAgreementId", ':', relationship."TermEndOn")
                   AS "BusinessKey",
                 relationship."EffectiveNowUtc"
          FROM eligible_policy_relationships AS relationship
          WHERE relationship."TermEndOn" IS NOT NULL
            AND ((relationship."EndingDisposition" = 'OfferRenewal'
                  AND relationship."AutomationKey" = 'lease-renewal-offer')
              OR (relationship."EndingDisposition" = 'OfferMonthToMonth'
                  AND relationship."AutomationKey" = 'month-to-month-offer')
              OR (relationship."EndingDisposition" = 'NonRenewalMoveOut'
                  AND relationship."AutomationKey" = 'lease-non-renewal'))
        ),
        ranked_money_candidates AS (
          SELECT relationship."PortfolioId",
                 relationship."TenantNoticePolicyId",
                 relationship."LeaseManagementId",
                 charge."TenantLedgerEntryId",
                 charge."DueOn",
                 relationship."LeadDays",
                 relationship."SendHourLocal",
                 relationship."TimeZone",
                 relationship."EffectiveNowUtc",
                 relationship."AutomationKey",
                 row_number() OVER (
                   PARTITION BY relationship."TenantNoticePolicyId",
                                relationship."LeaseManagementId",
                                charge."DueOn"
                   ORDER BY CASE WHEN charge."EntryType" = 'RentCharge' THEN 0 ELSE 1 END,
                            charge."OpenAmount" DESC,
                            charge."TenantLedgerEntryId"
                 ) AS candidate_rank
          FROM eligible_policy_relationships AS relationship
          INNER JOIN "vw_tenant_charge_balances" AS charge
            ON charge."PortfolioId" = relationship."PortfolioId"
           AND charge."TenantAccountId" = relationship."TenantAccountId"
          WHERE charge."DueOn" IS NOT NULL
            AND charge."OpenAmount" > 0
            AND ((relationship."AutomationKey" = 'rent-reminder'
                  AND charge."EntryType" = 'RentCharge'
                  AND charge."DueOn" >= relationship."BusinessDate")
              OR (relationship."AutomationKey" = 'late-rent-late-fee'
                  AND charge."EntryType" IN ('RentCharge', 'LateFeeCharge')
                  AND charge."IsPastDue"))
        ),
        money_candidates AS (
          SELECT money."PortfolioId",
                 money."TenantNoticePolicyId",
                 money."LeaseManagementId",
                 money."TenantLedgerEntryId",
                 (((CASE money."AutomationKey"
                       WHEN 'rent-reminder' THEN money."DueOn" - money."LeadDays"
                       ELSE money."DueOn" + money."LeadDays"
                     END) + money."SendHourLocal" * interval '1 hour')
                   AT TIME ZONE money."TimeZone") AS "DueAtUtc",
                 concat('tenant-notice:', money."PortfolioId", ':', money."TenantNoticePolicyId",
                        ':', money."LeaseManagementId", ':ledger:', money."TenantLedgerEntryId")
                   AS "BusinessKey",
                 money."EffectiveNowUtc"
          FROM ranked_money_candidates AS money
          WHERE money.candidate_rank = 1
        ),
        due_candidates AS (
          SELECT * FROM lease_end_candidates
          UNION ALL
          SELECT * FROM money_candidates
        )
        INSERT INTO "TenantNoticeWorkItems"
            ("PortfolioId", "TenantNoticePolicyId", "LeaseManagementId", "TenantLedgerEntryId",
             "DueAtUtc", "Status", "BusinessKey", "AttemptCount", "CreatedAtUtc")
        SELECT candidate."PortfolioId",
               candidate."TenantNoticePolicyId",
               candidate."LeaseManagementId",
               candidate."TenantLedgerEntryId",
               candidate."DueAtUtc",
               'Pending',
               candidate."BusinessKey",
               0,
               clock_timestamp()
        FROM due_candidates AS candidate
        WHERE candidate."DueAtUtc" <= candidate."EffectiveNowUtc"
        ON CONFLICT ("BusinessKey") DO NOTHING;
        """;

    private readonly RentalCommandDbContext _db;
    private readonly IAtomicInfrastructureWriteGate _writeGate;

    public TenantNoticeCandidateGenerationService(
        RentalCommandDbContext db,
        IAtomicInfrastructureWriteGate writeGate)
    {
        _db = db;
        _writeGate = writeGate;
    }

    public async Task<int> GenerateDueAsync(CancellationToken ct = default)
    {
        using var lease = _writeGate.BeginTenantNoticeCandidateGeneration();
        return await _db.Database.ExecuteSqlRawAsync(CandidateInsertSql, ct);
    }
}
