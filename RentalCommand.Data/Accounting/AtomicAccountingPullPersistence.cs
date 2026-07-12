using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Accounting;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Accounting;

/// <summary>
/// PostgreSQL-only accounting pull cutover. The statement owns deduplication, candidate scoring and
/// ranking, mapping upserts, business inserts, ledger parking, cursor advancement, and claim release.
/// No provider row is materialized back into application code during persistence.
/// </summary>
internal sealed class AtomicAccountingPullPersistence : IAtomicAccountingPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicAccountingPullPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<ApplyAccountingPullResult> ApplyPullAsync(
        ApplyAccountingPullResultCommand command,
        CancellationToken ct = default)
    {
        var parameters = new NpgsqlParameter[]
        {
            Json("customers", command.Customers),
            Json("vendors", command.Vendors),
            Json("accounts", command.Accounts),
            Json("payments", command.Payments),
            Json("expenses", command.Expenses),
            new("portfolioId", NpgsqlDbType.Integer) { Value = command.PortfolioId },
            new("connectionId", NpgsqlDbType.Integer) { Value = command.AccountingConnectionId },
            new("claimToken", NpgsqlDbType.Uuid) { Value = command.PullClaimToken },
            new("batchIdentity", NpgsqlDbType.Text) { Value = command.ProviderBatchIdentity },
            new("nextCursors", NpgsqlDbType.Jsonb) { Value = command.NextCursorsJson },
            new("appliedAt", NpgsqlDbType.TimestampTz) { Value = command.AppliedAtUtc },
        };

        using var lease = _scope.BeginInternalRawDml(
            "AccountingEntityMappings",
            AtomicRawDmlOperation.Insert);
        var rows = await _db.Database.SqlQueryRaw<SummaryRow>(Sql, parameters).ToListAsync(ct);
        if (rows.Count != 1)
            throw new InvalidOperationException($"Accounting pull apply returned {rows.Count} summaries instead of one.");

        var row = rows[0];
        if (!row.ClaimOwned)
            throw new DbUpdateConcurrencyException("The accounting pull claim is stale, expired, or no longer owned.");

        return new ApplyAccountingPullResult(
            row.CustomersMapped,
            row.VendorsMapped,
            row.AccountsMapped,
            row.PaymentsImported,
            row.ExpensesImported,
            row.NeedsReview);
    }

    private static NpgsqlParameter Json<T>(string name, T value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(value) };

    private sealed class SummaryRow
    {
        public bool ClaimOwned { get; set; }
        public int CustomersMapped { get; set; }
        public int VendorsMapped { get; set; }
        public int AccountsMapped { get; set; }
        public int PaymentsImported { get; set; }
        public int ExpensesImported { get; set; }
        public int NeedsReview { get; set; }
    }

    // One SQL statement by design. MATERIALIZED input CTEs validate/deduplicate the bounded JSONB
    // payload before any join. PostgreSQL enum-backed columns use their persisted integer ordinals.
    private const string Sql = """
        WITH
        clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ),
        claim AS MATERIALIZED (
            SELECT c."Id"
            FROM "AccountingConnections" c
            CROSS JOIN clock
            WHERE c."Id" = @connectionId
              AND c."PortfolioId" = @portfolioId
              AND c."PullClaimToken" = @claimToken
              AND c."PullClaimExpiresAtUtc" > clock.now_utc
              AND c."Status" = 1
              AND c."PullEnabled"
            FOR UPDATE
        ),
        customers AS MATERIALIZED (
            SELECT DISTINCT ON (v ->> 'ExternalId')
                v ->> 'ExternalId' AS external_id,
                left(v ->> 'DisplayName', 300) AS display_name,
                nullif(v ->> 'Email', '') AS secondary
            FROM jsonb_array_elements(@customers::jsonb) WITH ORDINALITY input(v, ordinal)
            WHERE v ->> 'ExternalId' IS NOT NULL AND length(v ->> 'ExternalId') BETWEEN 1 AND 200
            ORDER BY v ->> 'ExternalId', ordinal DESC
        ),
        vendors AS MATERIALIZED (
            SELECT DISTINCT ON (v ->> 'ExternalId')
                v ->> 'ExternalId' AS external_id,
                left(v ->> 'DisplayName', 300) AS display_name,
                nullif(v ->> 'TaxId', '') AS secondary
            FROM jsonb_array_elements(@vendors::jsonb) WITH ORDINALITY input(v, ordinal)
            WHERE v ->> 'ExternalId' IS NOT NULL AND length(v ->> 'ExternalId') BETWEEN 1 AND 200
            ORDER BY v ->> 'ExternalId', ordinal DESC
        ),
        accounts AS MATERIALIZED (
            SELECT DISTINCT ON (v ->> 'ExternalId', (v ->> 'Kind')::int)
                v ->> 'ExternalId' AS external_id,
                left(v ->> 'Name', 300) AS display_name,
                (v ->> 'Kind')::int AS kind
            FROM jsonb_array_elements(@accounts::jsonb) WITH ORDINALITY input(v, ordinal)
            WHERE v ->> 'ExternalId' IS NOT NULL
              AND length(v ->> 'ExternalId') BETWEEN 1 AND 200
              AND (v ->> 'Kind')::int IN (0, 1)
            ORDER BY v ->> 'ExternalId', (v ->> 'Kind')::int, ordinal DESC
        ),
        payments AS MATERIALIZED (
            SELECT DISTINCT ON (v ->> 'ExternalId')
                v ->> 'ExternalId' AS external_id,
                nullif(v ->> 'CustomerExternalId', '') AS customer_external_id,
                (v ->> 'Amount')::numeric(18,2) AS amount,
                (v ->> 'TxnDateUtc')::timestamptz AS txn_at,
                left(nullif(v ->> 'PaymentMethod', ''), 100) AS payment_method,
                left(nullif(v ->> 'ReferenceNumber', ''), 200) AS reference_number,
                nullif(v ->> 'DepositAccountExternalId', '') AS deposit_account_external_id,
                v AS payload
            FROM jsonb_array_elements(@payments::jsonb) WITH ORDINALITY input(v, ordinal)
            WHERE v ->> 'ExternalId' IS NOT NULL AND length(v ->> 'ExternalId') BETWEEN 1 AND 200
              AND (v ->> 'Amount')::numeric BETWEEN 0 AND 1000000000
              AND v ? 'TxnDateUtc'
            ORDER BY v ->> 'ExternalId', ordinal DESC
        ),
        expenses AS MATERIALIZED (
            SELECT DISTINCT ON (v ->> 'ExternalId', v ->> 'SourceKind')
                v ->> 'ExternalId' AS external_id,
                nullif(v ->> 'VendorExternalId', '') AS vendor_external_id,
                nullif(v ->> 'AccountExternalId', '') AS account_external_id,
                nullif(v ->> 'ClassExternalId', '') AS class_external_id,
                (v ->> 'Amount')::numeric(18,2) AS amount,
                (v ->> 'TxnDateUtc')::timestamptz AS txn_at,
                left(nullif(v ->> 'ReferenceNumber', ''), 200) AS reference_number,
                CASE WHEN v ->> 'SourceKind' = 'Bill' THEN 'Bill' ELSE 'Purchase' END AS external_type,
                v AS payload
            FROM jsonb_array_elements(@expenses::jsonb) WITH ORDINALITY input(v, ordinal)
            WHERE v ->> 'ExternalId' IS NOT NULL AND length(v ->> 'ExternalId') BETWEEN 1 AND 200
              AND (v ->> 'Amount')::numeric BETWEEN 0 AND 1000000000
              AND v ? 'TxnDateUtc'
            ORDER BY v ->> 'ExternalId', v ->> 'SourceKind', ordinal DESC
        ),
        customer_candidates AS MATERIALIZED (
            SELECT i.external_id, t."Id" local_id, score,
                   row_number() OVER (PARTITION BY i.external_id ORDER BY score DESC, t."Id") rank,
                   lead(score) OVER (PARTITION BY i.external_id ORDER BY score DESC, t."Id") rival
            FROM customers i
            JOIN claim ON true
            CROSS JOIN "Tenants" t
            CROSS JOIN LATERAL (
                SELECT CASE
                    WHEN i.secondary IS NOT NULL AND t."Email" IS NOT NULL
                      AND regexp_replace(lower(i.secondary), '[^[:alnum:]]', '', 'g') = regexp_replace(lower(t."Email"), '[^[:alnum:]]', '', 'g') THEN 1::numeric
                    WHEN normalized.external_name = '' OR normalized.local_name = '' THEN 0::numeric
                    WHEN normalized.external_name LIKE '%' || normalized.local_name || '%'
                      OR normalized.local_name LIKE '%' || normalized.external_name || '%' THEN 1::numeric
                    ELSE coalesce((SELECT count(*)::numeric / nullif(cardinality(local_tokens), 0)
                                   FROM unnest(local_tokens) token WHERE token = ANY(external_tokens)), 0)
                END AS score
                FROM (SELECT
                    trim(regexp_replace(regexp_replace(lower(i.display_name), '[^[:alnum:] ]', '', 'g'), '\s+', ' ', 'g')) external_name,
                    trim(regexp_replace(regexp_replace(lower(concat_ws(' ', t."FirstName", t."LastName")), '[^[:alnum:] ]', '', 'g'), '\s+', ' ', 'g')) local_name) normalized
                CROSS JOIN LATERAL (SELECT ARRAY(SELECT token FROM unnest(regexp_split_to_array(normalized.external_name, '\s+')) token WHERE length(token) >= 2 AND token <> ALL(ARRAY['ach','the','and','llc','inc','co','payment','pmt','deposit','debit','credit','transfer','xfer','online','pos','purchase','rent','from','for','ref','id'])) external_tokens) et
                CROSS JOIN LATERAL (SELECT ARRAY(SELECT token FROM unnest(regexp_split_to_array(normalized.local_name, '\s+')) token WHERE length(token) >= 2 AND token <> ALL(ARRAY['ach','the','and','llc','inc','co','payment','pmt','deposit','debit','credit','transfer','xfer','online','pos','purchase','rent','from','for','ref','id'])) local_tokens) lt
            ) scored
            WHERE t."PortfolioId" = @portfolioId AND t."DeletedAt" IS NULL AND score > 0
        ),
        customer_best AS MATERIALIZED (
            SELECT external_id, local_id, score,
                   score >= .8 AND (rival IS NULL OR score - rival >= .15) auto_confirm
            FROM customer_candidates WHERE rank = 1
        ),
        vendor_candidates AS MATERIALIZED (
            SELECT i.external_id, v."Id" local_id, score,
                   row_number() OVER (PARTITION BY i.external_id ORDER BY score DESC, v."Id") rank,
                   lead(score) OVER (PARTITION BY i.external_id ORDER BY score DESC, v."Id") rival
            FROM vendors i JOIN claim ON true CROSS JOIN "Vendors" v
            CROSS JOIN LATERAL (SELECT CASE
                WHEN i.secondary IS NOT NULL AND v."TaxId" IS NOT NULL AND regexp_replace(lower(i.secondary), '[^[:alnum:]]', '', 'g') = regexp_replace(lower(v."TaxId"), '[^[:alnum:]]', '', 'g') THEN 1::numeric
                WHEN regexp_replace(lower(i.display_name), '[^[:alnum:]]', '', 'g') = regexp_replace(lower(v."Name"), '[^[:alnum:]]', '', 'g') THEN 1::numeric
                WHEN lower(i.display_name) LIKE '%' || lower(v."Name") || '%' OR lower(v."Name") LIKE '%' || lower(i.display_name) || '%' THEN 1::numeric
                ELSE 0::numeric END score) scored
            WHERE v."PortfolioId" = @portfolioId AND v."DeletedAt" IS NULL AND score > 0
        ),
        vendor_best AS MATERIALIZED (
            SELECT external_id, local_id, score,
                   score >= .8 AND (rival IS NULL OR score - rival >= .15) auto_confirm
            FROM vendor_candidates WHERE rank = 1
        ),
        class_candidates AS MATERIALIZED (
            SELECT i.external_id, p."Id" local_id,
                   CASE WHEN regexp_replace(lower(i.display_name), '[^[:alnum:]]', '', 'g') = regexp_replace(lower(p."Name"), '[^[:alnum:]]', '', 'g') THEN 1::numeric
                        WHEN lower(i.display_name) LIKE '%' || lower(p."Name") || '%' OR lower(p."Name") LIKE '%' || lower(i.display_name) || '%' THEN 1::numeric ELSE 0::numeric END score
            FROM accounts i JOIN claim ON true CROSS JOIN "Properties" p
            WHERE i.kind = 1 AND p."PortfolioId" = @portfolioId AND p."DeletedAt" IS NULL
        ),
        class_ranked AS MATERIALIZED (
            SELECT *, row_number() OVER (PARTITION BY external_id ORDER BY score DESC, local_id) rank,
                   lead(score) OVER (PARTITION BY external_id ORDER BY score DESC, local_id) rival
            FROM class_candidates WHERE score > 0
        ),
        class_best AS MATERIALIZED (
            SELECT external_id, local_id, score,
                   score >= .8 AND (rival IS NULL OR score - rival >= .15) auto_confirm
            FROM class_ranked WHERE rank = 1
        ),
        customer_maps AS (
            INSERT INTO "AccountingEntityMappings" ("PortfolioId","AccountingConnectionId","LocalEntityType","LocalEntityId","LocalEnumValue","ExternalType","ExternalId","ExternalDisplayName","ConfirmedAt","ConfirmedByUserId","Confidence","Revision","CreatedAt","UpdatedAt")
            SELECT @portfolioId,@connectionId,'Tenant',b.local_id,NULL,'Customer',i.external_id,i.display_name,
                   CASE WHEN b.auto_confirm AND b.local_id IS NOT NULL THEN @appliedAt END,NULL,coalesce(b.score,0),0,@appliedAt,@appliedAt
            FROM customers i JOIN claim ON true LEFT JOIN customer_best b USING (external_id)
            ON CONFLICT ("PortfolioId","AccountingConnectionId","ExternalType","ExternalId") DO UPDATE SET
                "ExternalDisplayName"=excluded."ExternalDisplayName",
                "LocalEntityType"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityType" ELSE "AccountingEntityMappings"."LocalEntityType" END,
                "LocalEntityId"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityId" ELSE "AccountingEntityMappings"."LocalEntityId" END,
                "LocalEnumValue"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN NULL ELSE "AccountingEntityMappings"."LocalEnumValue" END,
                "Confidence"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."Confidence" ELSE "AccountingEntityMappings"."Confidence" END,
                "ConfirmedAt"=coalesce("AccountingEntityMappings"."ConfirmedAt", excluded."ConfirmedAt"), "UpdatedAt"=@appliedAt
            RETURNING 1
        ),
        vendor_maps AS (
            INSERT INTO "AccountingEntityMappings" ("PortfolioId","AccountingConnectionId","LocalEntityType","LocalEntityId","LocalEnumValue","ExternalType","ExternalId","ExternalDisplayName","ConfirmedAt","ConfirmedByUserId","Confidence","Revision","CreatedAt","UpdatedAt")
            SELECT @portfolioId,@connectionId,'Vendor',b.local_id,NULL,'Vendor',i.external_id,i.display_name,
                   CASE WHEN b.auto_confirm AND b.local_id IS NOT NULL THEN @appliedAt END,NULL,coalesce(b.score,0),0,@appliedAt,@appliedAt
            FROM vendors i JOIN claim ON true LEFT JOIN vendor_best b USING (external_id)
            ON CONFLICT ("PortfolioId","AccountingConnectionId","ExternalType","ExternalId") DO UPDATE SET
                "ExternalDisplayName"=excluded."ExternalDisplayName",
                "LocalEntityType"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityType" ELSE "AccountingEntityMappings"."LocalEntityType" END,
                "LocalEntityId"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityId" ELSE "AccountingEntityMappings"."LocalEntityId" END,
                "LocalEnumValue"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN NULL ELSE "AccountingEntityMappings"."LocalEnumValue" END,
                "Confidence"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."Confidence" ELSE "AccountingEntityMappings"."Confidence" END,
                "ConfirmedAt"=coalesce("AccountingEntityMappings"."ConfirmedAt", excluded."ConfirmedAt"), "UpdatedAt"=@appliedAt
            RETURNING 1
        ),
        account_maps AS (
            INSERT INTO "AccountingEntityMappings" ("PortfolioId","AccountingConnectionId","LocalEntityType","LocalEntityId","LocalEnumValue","ExternalType","ExternalId","ExternalDisplayName","ConfirmedAt","ConfirmedByUserId","Confidence","Revision","CreatedAt","UpdatedAt")
            SELECT @portfolioId,@connectionId,
                   CASE WHEN i.kind=1 THEN 'Property' ELSE 'ScheduleECategory' END,
                   CASE WHEN i.kind=1 THEN b.local_id END,
                   CASE WHEN i.kind=0 THEN CASE
                     WHEN lower(i.display_name) LIKE '%advertis%' THEN 'Advertising'
                     WHEN lower(i.display_name) LIKE '%auto%' OR lower(i.display_name) LIKE '%travel%' THEN 'AutoTravel'
                     WHEN lower(i.display_name) LIKE '%clean%' OR lower(i.display_name) LIKE '%maint%' THEN 'CleaningMaintenance'
                     WHEN lower(i.display_name) LIKE '%commission%' THEN 'Commissions'
                     WHEN lower(i.display_name) LIKE '%insurance%' THEN 'Insurance'
                     WHEN lower(i.display_name) LIKE '%legal%' OR lower(i.display_name) LIKE '%professional%' THEN 'LegalProfessional'
                     WHEN lower(i.display_name) LIKE '%management%' THEN 'ManagementFees'
                     WHEN lower(i.display_name) LIKE '%mortgage%' OR lower(i.display_name) LIKE '%interest%' THEN 'MortgageInterest'
                     WHEN lower(i.display_name) LIKE '%repair%' THEN 'Repairs'
                     WHEN lower(i.display_name) LIKE '%suppl%' THEN 'Supplies'
                     WHEN lower(i.display_name) LIKE '%tax%' THEN 'Taxes'
                     WHEN lower(i.display_name) LIKE '%utilit%' THEN 'Utilities'
                     WHEN lower(i.display_name) LIKE '%depreciat%' THEN 'Depreciation' ELSE 'Other' END END,
                   CASE WHEN i.kind=1 THEN 'Class' ELSE 'Account' END,i.external_id,i.display_name,
                   CASE WHEN (i.kind=1 AND b.auto_confirm AND b.local_id IS NOT NULL) OR (i.kind=0 AND lower(i.display_name) ~ '(advertis|auto|travel|clean|maint|commission|insurance|legal|professional|management|mortgage|interest|repair|suppl|tax|utilit|depreciat)') THEN @appliedAt END,
                   NULL,CASE WHEN i.kind=1 THEN coalesce(b.score,0) WHEN lower(i.display_name) ~ '(advertis|auto|travel|clean|maint|commission|insurance|legal|professional|management|mortgage|interest|repair|suppl|tax|utilit|depreciat)' THEN 1 ELSE 0 END,0,@appliedAt,@appliedAt
            FROM accounts i JOIN claim ON true LEFT JOIN class_best b USING (external_id)
            ON CONFLICT ("PortfolioId","AccountingConnectionId","ExternalType","ExternalId") DO UPDATE SET
                "ExternalDisplayName"=excluded."ExternalDisplayName",
                "LocalEntityType"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityType" ELSE "AccountingEntityMappings"."LocalEntityType" END,
                "LocalEntityId"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEntityId" ELSE "AccountingEntityMappings"."LocalEntityId" END,
                "LocalEnumValue"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."LocalEnumValue" ELSE "AccountingEntityMappings"."LocalEnumValue" END,
                "Confidence"=CASE WHEN "AccountingEntityMappings"."ConfirmedAt" IS NULL THEN excluded."Confidence" ELSE "AccountingEntityMappings"."Confidence" END,
                "ConfirmedAt"=coalesce("AccountingEntityMappings"."ConfirmedAt", excluded."ConfirmedAt"), "UpdatedAt"=@appliedAt
            RETURNING "ExternalId" external_id,"LocalEnumValue" category
        ),
        effective_customer_maps AS MATERIALIZED (
            SELECT m."ExternalId" external_id,m."LocalEntityId" local_id
            FROM "AccountingEntityMappings" m
            WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId
              AND m."ExternalType"='Customer' AND m."LocalEntityType"='Tenant' AND m."ConfirmedAt" IS NOT NULL
            UNION ALL
            SELECT b.external_id,b.local_id FROM customer_best b
            WHERE b.auto_confirm AND b.local_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "AccountingEntityMappings" m
                  WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId
                    AND m."ExternalType"='Customer' AND m."ExternalId"=b.external_id AND m."ConfirmedAt" IS NOT NULL)
        ),
        effective_vendor_maps AS MATERIALIZED (
            SELECT m."ExternalId" external_id,m."LocalEntityId" local_id FROM "AccountingEntityMappings" m
            WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Vendor' AND m."LocalEntityType"='Vendor' AND m."ConfirmedAt" IS NOT NULL
            UNION ALL SELECT b.external_id,b.local_id FROM vendor_best b WHERE b.auto_confirm AND b.local_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "AccountingEntityMappings" m WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Vendor' AND m."ExternalId"=b.external_id AND m."ConfirmedAt" IS NOT NULL)
        ),
        effective_class_maps AS MATERIALIZED (
            SELECT m."ExternalId" external_id,m."LocalEntityId" local_id FROM "AccountingEntityMappings" m
            WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Class' AND m."LocalEntityType"='Property' AND m."ConfirmedAt" IS NOT NULL
            UNION ALL SELECT b.external_id,b.local_id FROM class_best b WHERE b.auto_confirm AND b.local_id IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "AccountingEntityMappings" m WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Class' AND m."ExternalId"=b.external_id AND m."ConfirmedAt" IS NOT NULL)
        ),
        effective_account_maps AS MATERIALIZED (
            SELECT m."ExternalId" external_id,m."LocalEnumValue" category FROM "AccountingEntityMappings" m
            WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Account' AND m."LocalEntityType"='ScheduleECategory' AND m."ConfirmedAt" IS NOT NULL
            UNION ALL SELECT a.external_id,a.category FROM account_maps a WHERE a.category IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM "AccountingEntityMappings" m WHERE m."PortfolioId"=@portfolioId AND m."AccountingConnectionId"=@connectionId AND m."ExternalType"='Account' AND m."ExternalId"=a.external_id AND m."ConfirmedAt" IS NOT NULL)
        ),
        live_leases AS MATERIALIZED (
            SELECT tenant_id, lease_id FROM (
                SELECT tenants.tenant_id, l."Id" lease_id,
                       row_number() OVER (PARTITION BY tenants.tenant_id ORDER BY l."StartDate" DESC,l."Id" DESC) rank
                FROM "Leases" l
                CROSS JOIN LATERAL (SELECT l."TenantId" tenant_id UNION SELECT lt."TenantId" FROM "LeaseTenants" lt WHERE lt."LeaseId"=l."Id") tenants
                WHERE l."PortfolioId"=@portfolioId AND l."DeletedAt" IS NULL AND l."Status" IN (1,2)
            ) ranked WHERE rank=1
        ),
        payment_decisions AS MATERIALIZED (
            SELECT p.*, lease.lease_id,
                   (EXISTS (SELECT 1 FROM "AccountingEntityMappings" a WHERE a."PortfolioId"=@portfolioId AND a."AccountingConnectionId"=@connectionId AND a."ExternalType"='Account' AND a."ExternalId"=p.deposit_account_external_id AND lower(a."ExternalDisplayName") ~ '(security deposit|deposit held|tenant deposit)')
                    OR EXISTS (SELECT 1 FROM accounts a WHERE a.external_id=p.deposit_account_external_id AND lower(a.display_name) ~ '(security deposit|deposit held|tenant deposit)')) is_deposit
            FROM payments p JOIN claim ON true
            LEFT JOIN effective_customer_maps m ON m.external_id=p.customer_external_id
            LEFT JOIN live_leases lease ON lease.tenant_id=m.local_id
            WHERE NOT EXISTS (SELECT 1 FROM "AccountingSyncMaps" s WHERE s."PortfolioId"=@portfolioId AND s."AccountingConnectionId"=@connectionId AND s."Direction"='Import' AND s."ExternalType"='Payment' AND s."ExternalId"=p.external_id)
        ),
        payment_numbered AS MATERIALIZED (
            SELECT d.*, nextval(pg_get_serial_sequence('"Payments"','Id'))::int generated_id
            FROM payment_decisions d WHERE d.lease_id IS NOT NULL
            ORDER BY d.external_id
        ),
        payment_insert AS (
            INSERT INTO "Payments" ("Id","PortfolioId","LeaseId","PaymentType","Status","Amount","DueDate","PaidDate","Method","ExternalReference","CreatedAt","UpdatedAt")
            SELECT generated_id,@portfolioId,lease_id,CASE WHEN is_deposit THEN 1 ELSE 0 END,1,amount,txn_at,txn_at,payment_method,coalesce(reference_number,external_id),@appliedAt,@appliedAt
            FROM payment_numbered
            RETURNING "Id"
        ),
        payment_ledger AS (
            INSERT INTO "AccountingSyncMaps" ("PortfolioId","AccountingConnectionId","Direction","ExternalType","ExternalId","LocalEntityType","LocalEntityId","Status","AttemptCount","LastError","LastAttemptAt","MetadataJson","CreatedAt","UpdatedAt")
            SELECT @portfolioId,@connectionId,'Import','Payment',d.external_id,
                   CASE WHEN d.lease_id IS NOT NULL THEN 'Payment' END,n.generated_id,
                   CASE WHEN d.lease_id IS NOT NULL THEN 'Imported' ELSE 'Unmatched' END,1,
                   CASE WHEN d.lease_id IS NULL THEN 'No confirmed tenant mapping with a live lease' END,@appliedAt,d.payload,@appliedAt,@appliedAt
            FROM payment_decisions d LEFT JOIN payment_numbered n ON n.external_id=d.external_id
            ON CONFLICT ("PortfolioId","AccountingConnectionId","Direction","ExternalType","ExternalId") DO NOTHING
            RETURNING "Status"
        ),
        expense_decisions AS MATERIALIZED (
            SELECT e.*,vm.local_id vendor_id,pm.local_id property_id,
                   CASE coalesce(am.category,'Other')
                     WHEN 'Advertising' THEN 0 WHEN 'AutoTravel' THEN 1 WHEN 'CleaningMaintenance' THEN 2 WHEN 'Commissions' THEN 3 WHEN 'Insurance' THEN 4 WHEN 'LegalProfessional' THEN 5 WHEN 'ManagementFees' THEN 6 WHEN 'MortgageInterest' THEN 7 WHEN 'Repairs' THEN 8 WHEN 'Supplies' THEN 9 WHEN 'Taxes' THEN 10 WHEN 'Utilities' THEN 11 WHEN 'Depreciation' THEN 12 ELSE 13 END category
            FROM expenses e JOIN claim ON true
            LEFT JOIN effective_vendor_maps vm ON vm.external_id=e.vendor_external_id
            LEFT JOIN effective_class_maps pm ON pm.external_id=e.class_external_id
            LEFT JOIN effective_account_maps am ON am.external_id=e.account_external_id
            WHERE NOT EXISTS (SELECT 1 FROM "AccountingSyncMaps" s WHERE s."PortfolioId"=@portfolioId AND s."AccountingConnectionId"=@connectionId AND s."Direction"='Import' AND s."ExternalType"=e.external_type AND s."ExternalId"=e.external_id)
        ),
        expense_numbered AS MATERIALIZED (
            SELECT d.*,nextval(pg_get_serial_sequence('"Expenses"','Id'))::int generated_id
            FROM expense_decisions d WHERE category<>12 AND (vendor_id IS NOT NULL OR property_id IS NOT NULL OR category<>13)
            ORDER BY external_type,external_id
        ),
        expense_insert AS (
            INSERT INTO "Expenses" ("Id","PortfolioId","PropertyId","VendorId","Category","Description","Status","Amount","IncurredAt","PaidAt","BillableToOwner","CreatedAt","UpdatedAt")
            SELECT generated_id,@portfolioId,property_id,vendor_id,category,left(coalesce(reference_number,'Accounting import '||external_id),500),2,amount,txn_at,txn_at,false,@appliedAt,@appliedAt
            FROM expense_numbered
            RETURNING "Id"
        ),
        expense_ledger AS (
            INSERT INTO "AccountingSyncMaps" ("PortfolioId","AccountingConnectionId","Direction","ExternalType","ExternalId","LocalEntityType","LocalEntityId","Status","AttemptCount","LastError","LastAttemptAt","MetadataJson","CreatedAt","UpdatedAt")
            SELECT @portfolioId,@connectionId,'Import',d.external_type,d.external_id,
                   CASE WHEN d.category<>12 AND (d.vendor_id IS NOT NULL OR d.property_id IS NOT NULL OR d.category<>13) THEN 'Expense' END,n.generated_id,
                   CASE WHEN d.category=12 THEN 'NeedsReview' WHEN d.vendor_id IS NULL AND d.property_id IS NULL AND d.category=13 THEN 'Unmatched' ELSE 'Imported' END,1,
                   CASE WHEN d.category=12 THEN 'Depreciation is non-cash; skipped on import' WHEN d.vendor_id IS NULL AND d.property_id IS NULL AND d.category=13 THEN 'No vendor / property / category mapping resolved' END,
                   @appliedAt,d.payload,@appliedAt,@appliedAt
            FROM expense_decisions d LEFT JOIN expense_numbered n ON n.external_id=d.external_id AND n.external_type=d.external_type
            ON CONFLICT ("PortfolioId","AccountingConnectionId","Direction","ExternalType","ExternalId") DO NOTHING
            RETURNING "Status"
        ),
        connection_update AS (
            UPDATE "AccountingConnections" c SET
                "LastPulledAtJson"=@nextCursors,
                "LastSyncedAt"=@appliedAt,
                "NextPullAtUtc"=@appliedAt + interval '15 minutes',
                "LastError"=NULL,
                "Status"=1,
                "PullClaimOwner"=NULL,"PullClaimToken"=NULL,"PullClaimExpiresAtUtc"=NULL,
                "UpdatedAt"=@appliedAt
            FROM claim WHERE c."Id"=claim."Id"
            RETURNING 1
        )
        SELECT EXISTS(SELECT 1 FROM claim) "ClaimOwned",
               (SELECT count(*)::int FROM customer_maps) "CustomersMapped",
               (SELECT count(*)::int FROM vendor_maps) "VendorsMapped",
               (SELECT count(*)::int FROM account_maps) "AccountsMapped",
               (SELECT count(*)::int FROM payment_ledger WHERE "Status"='Imported') "PaymentsImported",
               (SELECT count(*)::int FROM expense_ledger WHERE "Status"='Imported') "ExpensesImported",
               ((SELECT count(*) FROM payment_ledger WHERE "Status"<>'Imported') + (SELECT count(*) FROM expense_ledger WHERE "Status"<>'Imported'))::int "NeedsReview"
        """;
}
