using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Banking;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Banking;

/// <summary>
/// PostgreSQL-only, kernel-owned banking merge boundary. Each method executes one statement that
/// deduplicates, joins, filters, mutates, counts, and shapes its returned audit/result payload in SQL.
/// </summary>
internal sealed class AtomicBankingPersistence : IAtomicBankingPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicBankingPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public Task<AtomicBankTransactionMergeResult> ApplyPlaidSyncAsync(
        int portfolioId,
        int connectionId,
        IReadOnlyList<BankTransactionInput> added,
        int addedInputCount,
        IReadOnlyList<BankTransactionInput> modified,
        int modifiedInputCount,
        IReadOnlyList<string> removedProviderTransactionIds,
        DateTime appliedAtUtc,
        CancellationToken ct = default) => ExecuteAsync(
            PlaidSyncSql,
            [
                JsonParameter("added", added),
                new NpgsqlParameter("addedInputCount", NpgsqlDbType.Integer) { Value = addedInputCount },
                JsonParameter("modified", modified),
                new NpgsqlParameter("modifiedInputCount", NpgsqlDbType.Integer) { Value = modifiedInputCount },
                JsonParameter("removed", removedProviderTransactionIds),
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
                new NpgsqlParameter("connectionId", NpgsqlDbType.Integer) { Value = connectionId },
                new NpgsqlParameter("appliedAt", NpgsqlDbType.TimestampTz) { Value = appliedAtUtc },
            ],
            ct);

    public Task<AtomicBankTransactionMergeResult> ImportAsync(
        int portfolioId,
        int connectionId,
        IReadOnlyList<BankTransactionInput> transactions,
        int inputCount,
        DateTime importedAtUtc,
        CancellationToken ct = default) => ExecuteAsync(
            ImportSql,
            [
                JsonParameter("transactions", transactions),
                new NpgsqlParameter("inputCount", NpgsqlDbType.Integer) { Value = inputCount },
                new NpgsqlParameter("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
                new NpgsqlParameter("connectionId", NpgsqlDbType.Integer) { Value = connectionId },
                new NpgsqlParameter("importedAt", NpgsqlDbType.TimestampTz) { Value = importedAtUtc },
            ],
            ct);

    private async Task<AtomicBankTransactionMergeResult> ExecuteAsync(
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        using var lease = _scope.BeginInternalRawDml("BankTransactions", AtomicRawDmlOperation.Insert);
        var rows = await _db.Database.SqlQueryRaw<MergeSummaryRow>(sql, parameters).ToListAsync(ct);
        if (rows.Count != 1)
        {
            throw new InvalidOperationException($"The banking merge returned {rows.Count} summary rows instead of one.");
        }
        var row = rows[0];
        return new AtomicBankTransactionMergeResult(
            row.ImportedCount,
            row.ModifiedCount,
            row.RemovedCount,
            row.ChangedEventCount,
            row.SkippedCount,
            Deserialize<int>(row.AffectedTransactionIdsJson),
            Deserialize<AtomicBankTransactionMutation>(row.MutationsJson));
    }

    private static NpgsqlParameter JsonParameter<T>(string name, T value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(value) };

    private static IReadOnlyList<T> Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T[]>(json)
        ?? throw new InvalidOperationException("The banking merge returned an invalid JSON aggregate.");

    private sealed class MergeSummaryRow
    {
        public int ImportedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int RemovedCount { get; set; }
        public int ChangedEventCount { get; set; }
        public int SkippedCount { get; set; }
        public string AffectedTransactionIdsJson { get; set; } = "[]";
        public string MutationsJson { get; set; } = "[]";
    }

    private const string PlaidSyncSql = """
        WITH
        added_raw AS MATERIALIZED (
            SELECT value, ordinal
            FROM jsonb_array_elements(@added::jsonb) WITH ORDINALITY AS input(value, ordinal)
        ),
        added AS MATERIALIZED (
            SELECT DISTINCT ON (value ->> 'ProviderTransactionId')
                value ->> 'ProviderTransactionId' AS provider_id,
                (value ->> 'PostedAtUtc')::timestamptz AS posted_at,
                (value ->> 'AuthorizedAtUtc')::timestamptz AS authorized_at,
                value ->> 'Description' AS description,
                value ->> 'MerchantName' AS merchant_name,
                (value ->> 'Amount')::numeric AS amount,
                value ->> 'IsoCurrencyCode' AS currency,
                value ->> 'Category' AS category,
                (value ->> 'RawData')::jsonb AS raw_data
            FROM added_raw
            ORDER BY value ->> 'ProviderTransactionId', ordinal
        ),
        modified_raw AS MATERIALIZED (
            SELECT value, ordinal
            FROM jsonb_array_elements(@modified::jsonb) WITH ORDINALITY AS input(value, ordinal)
        ),
        modified AS MATERIALIZED (
            SELECT DISTINCT ON (value ->> 'ProviderTransactionId')
                value ->> 'ProviderTransactionId' AS provider_id,
                (value ->> 'PostedAtUtc')::timestamptz AS posted_at,
                (value ->> 'AuthorizedAtUtc')::timestamptz AS authorized_at,
                value ->> 'Description' AS description,
                value ->> 'MerchantName' AS merchant_name,
                (value ->> 'Amount')::numeric AS amount,
                value ->> 'IsoCurrencyCode' AS currency,
                value ->> 'Category' AS category,
                (value ->> 'RawData')::jsonb AS raw_data
            FROM modified_raw
            ORDER BY value ->> 'ProviderTransactionId', ordinal
        ),
        removed AS MATERIALIZED (
            SELECT DISTINCT value AS provider_id
            FROM jsonb_array_elements_text(@removed::jsonb) AS input(value)
        ),
        incoming_ids AS MATERIALIZED (
            SELECT provider_id FROM added
            UNION
            SELECT provider_id FROM modified
            UNION
            SELECT provider_id FROM removed
        ),
        existing AS MATERIALIZED (
            SELECT transaction.*
            FROM "BankTransactions" AS transaction
            INNER JOIN incoming_ids AS incoming ON incoming.provider_id = transaction."ProviderTransactionId"
            WHERE transaction."PortfolioId" = @portfolioId
              AND transaction."BankConnectionId" = @connectionId
        ),
        source AS MATERIALIZED (
            SELECT
                ids.provider_id,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.posted_at
                     WHEN added.provider_id IS NOT NULL THEN added.posted_at ELSE existing."PostedAt" END AS posted_at,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.authorized_at
                     WHEN added.provider_id IS NOT NULL THEN added.authorized_at ELSE existing."AuthorizedAt" END AS authorized_at,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.description
                     WHEN added.provider_id IS NOT NULL THEN added.description ELSE existing."Description" END AS description,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.merchant_name
                     WHEN added.provider_id IS NOT NULL THEN added.merchant_name ELSE existing."MerchantName" END AS merchant_name,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.amount
                     WHEN added.provider_id IS NOT NULL THEN added.amount ELSE existing."Amount" END AS amount,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.currency
                     WHEN added.provider_id IS NOT NULL THEN added.currency ELSE existing."IsoCurrencyCode" END AS currency,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.category
                     WHEN added.provider_id IS NOT NULL THEN added.category ELSE existing."Category" END AS category,
                CASE WHEN modified.provider_id IS NOT NULL THEN modified.raw_data
                     WHEN added.provider_id IS NOT NULL THEN added.raw_data ELSE existing."RawData" END AS raw_data,
                existing."Id" AS existing_id,
                modified.provider_id IS NOT NULL AS is_modified,
                removed.provider_id IS NOT NULL AND existing."Id" IS NOT NULL AS is_removed,
                existing."MatchedPaymentId" AS old_payment_id,
                existing."MatchedExpenseId" AS old_expense_id,
                existing."MatchStatus" AS old_match_status,
                existing."MatchConfidence" AS old_match_confidence,
                existing."Notes" AS old_notes,
                CASE WHEN existing."Id" IS NULL THEN NULL ELSE jsonb_build_object(
                    'providerTransactionId', existing."ProviderTransactionId", 'postedAt', existing."PostedAt",
                    'authorizedAt', existing."AuthorizedAt", 'description', existing."Description",
                    'merchantName', existing."MerchantName", 'amount', existing."Amount",
                    'isoCurrencyCode', existing."IsoCurrencyCode", 'category', existing."Category",
                    'matchedPaymentId', existing."MatchedPaymentId", 'matchedExpenseId', existing."MatchedExpenseId",
                    'matchStatus', existing."MatchStatus", 'matchConfidence', existing."MatchConfidence",
                    'notes', existing."Notes") END AS old_values
            FROM incoming_ids AS ids
            LEFT JOIN added ON added.provider_id = ids.provider_id
            LEFT JOIN modified ON modified.provider_id = ids.provider_id
            LEFT JOIN removed ON removed.provider_id = ids.provider_id
            LEFT JOIN existing ON existing."ProviderTransactionId" = ids.provider_id
            WHERE (added.provider_id IS NOT NULL AND existing."Id" IS NULL)
               OR (existing."Id" IS NOT NULL AND (modified.provider_id IS NOT NULL OR removed.provider_id IS NOT NULL))
        ),
        merged AS (
            INSERT INTO "BankTransactions"
                ("PortfolioId", "BankConnectionId", "ProviderTransactionId", "PostedAt", "AuthorizedAt",
                 "Description", "MerchantName", "Amount", "IsoCurrencyCode", "Category", "RawData",
                 "MatchedPaymentId", "MatchedExpenseId", "MatchStatus", "MatchConfidence", "Notes",
                 "CreatedAt", "UpdatedAt")
            SELECT @portfolioId, @connectionId, source.provider_id, source.posted_at, source.authorized_at,
                   source.description, source.merchant_name, source.amount, source.currency, source.category, source.raw_data,
                   CASE WHEN source.is_removed OR source.old_match_status = 'Matched' THEN NULL ELSE source.old_payment_id END,
                   CASE WHEN source.is_removed OR source.old_match_status = 'Matched' THEN NULL ELSE source.old_expense_id END,
                   CASE WHEN source.is_removed THEN 'Removed'
                        WHEN source.existing_id IS NULL OR source.old_match_status = 'Matched' THEN 'Unmatched'
                        ELSE source.old_match_status END,
                   CASE WHEN source.is_removed OR source.old_match_status = 'Matched' THEN NULL ELSE source.old_match_confidence END,
                   CASE WHEN source.is_removed THEN 'Removed by Plaid sync.'
                        WHEN source.old_match_status = 'Matched'
                            THEN 'Plaid modified this transaction after it was matched; review the match again.'
                        ELSE source.old_notes END,
                   @appliedAt, @appliedAt
            FROM source
            ON CONFLICT ("BankConnectionId", "ProviderTransactionId") DO UPDATE SET
                "PostedAt" = EXCLUDED."PostedAt", "AuthorizedAt" = EXCLUDED."AuthorizedAt",
                "Description" = EXCLUDED."Description", "MerchantName" = EXCLUDED."MerchantName",
                "Amount" = EXCLUDED."Amount", "IsoCurrencyCode" = EXCLUDED."IsoCurrencyCode",
                "Category" = EXCLUDED."Category", "RawData" = EXCLUDED."RawData",
                "MatchedPaymentId" = EXCLUDED."MatchedPaymentId", "MatchedExpenseId" = EXCLUDED."MatchedExpenseId",
                "MatchStatus" = EXCLUDED."MatchStatus", "MatchConfidence" = EXCLUDED."MatchConfidence",
                "Notes" = EXCLUDED."Notes", "UpdatedAt" = EXCLUDED."UpdatedAt"
            RETURNING *
        ),
        mutation_rows AS MATERIALIZED (
            SELECT merged."Id" AS transaction_id,
                   CASE WHEN source.existing_id IS NULL THEN 'Created' ELSE 'Updated' END AS operation,
                   source.old_values,
                   jsonb_build_object(
                       'providerTransactionId', merged."ProviderTransactionId", 'postedAt', merged."PostedAt",
                       'authorizedAt', merged."AuthorizedAt", 'description', merged."Description",
                       'merchantName', merged."MerchantName", 'amount', merged."Amount",
                       'isoCurrencyCode', merged."IsoCurrencyCode", 'category', merged."Category",
                       'matchedPaymentId', merged."MatchedPaymentId", 'matchedExpenseId', merged."MatchedExpenseId",
                       'matchStatus', merged."MatchStatus", 'matchConfidence', merged."MatchConfidence",
                       'notes', merged."Notes") AS new_values,
                   CASE WHEN source.existing_id IS NULL THEN 'Bank transaction imported from Plaid.'
                        WHEN source.is_removed THEN 'Bank transaction removed by Plaid sync.'
                        ELSE 'Bank transaction updated from Plaid.' END AS reason,
                   source.existing_id IS NULL AS is_created,
                   source.is_modified AND source.existing_id IS NOT NULL AS is_modified,
                   source.is_removed AS is_removed
            FROM merged
            INNER JOIN source ON source.provider_id = merged."ProviderTransactionId"
        ),
        stats AS (
            SELECT
                count(*) FILTER (WHERE is_created)::int AS imported_count,
                count(*) FILTER (WHERE is_modified)::int AS modified_count,
                count(*) FILTER (WHERE is_removed)::int AS removed_count,
                ((SELECT count(*) FROM existing INNER JOIN modified ON modified.provider_id = existing."ProviderTransactionId")
                 + (SELECT count(*) FROM existing INNER JOIN removed ON removed.provider_id = existing."ProviderTransactionId"))::int AS changed_event_count,
                (@addedInputCount - (SELECT count(*) FROM added)
                 + @modifiedInputCount - (SELECT count(*) FROM modified)
                 + (SELECT count(*) FROM added INNER JOIN existing ON existing."ProviderTransactionId" = added.provider_id)
                 + (SELECT count(*) FROM modified
                    LEFT JOIN existing ON existing."ProviderTransactionId" = modified.provider_id
                    LEFT JOIN added ON added.provider_id = modified.provider_id
                    WHERE existing."Id" IS NULL AND added.provider_id IS NULL))::int AS skipped_count
            FROM mutation_rows
        )
        SELECT stats.imported_count AS "ImportedCount", stats.modified_count AS "ModifiedCount",
               stats.removed_count AS "RemovedCount", stats.changed_event_count AS "ChangedEventCount",
               stats.skipped_count AS "SkippedCount",
               COALESCE((SELECT jsonb_agg(transaction_id ORDER BY transaction_id)
                         FROM mutation_rows WHERE is_created OR is_modified), '[]'::jsonb)::text AS "AffectedTransactionIdsJson",
               COALESCE((SELECT jsonb_agg(jsonb_build_object(
                           'TransactionId', transaction_id, 'Operation', operation,
                           'OldValues', old_values::text, 'NewValues', new_values::text, 'Reason', reason)
                         ORDER BY transaction_id) FROM mutation_rows), '[]'::jsonb)::text AS "MutationsJson"
        FROM stats
        """;

    private const string ImportSql = """
        WITH
        input_raw AS MATERIALIZED (
            SELECT value, ordinal
            FROM jsonb_array_elements(@transactions::jsonb) WITH ORDINALITY AS input(value, ordinal)
        ),
        input AS MATERIALIZED (
            SELECT DISTINCT ON (value ->> 'ProviderTransactionId')
                value ->> 'ProviderTransactionId' AS provider_id,
                (value ->> 'PostedAtUtc')::timestamptz AS posted_at,
                (value ->> 'AuthorizedAtUtc')::timestamptz AS authorized_at,
                value ->> 'Description' AS description,
                value ->> 'MerchantName' AS merchant_name,
                (value ->> 'Amount')::numeric AS amount,
                value ->> 'IsoCurrencyCode' AS currency,
                value ->> 'Category' AS category,
                (value ->> 'RawData')::jsonb AS raw_data
            FROM input_raw
            ORDER BY value ->> 'ProviderTransactionId', ordinal
        ),
        inserted AS (
            INSERT INTO "BankTransactions"
                ("PortfolioId", "BankConnectionId", "ProviderTransactionId", "PostedAt", "AuthorizedAt",
                 "Description", "MerchantName", "Amount", "IsoCurrencyCode", "Category", "RawData",
                 "MatchStatus", "CreatedAt", "UpdatedAt")
            SELECT @portfolioId, @connectionId, input.provider_id, input.posted_at, input.authorized_at,
                   input.description, input.merchant_name, input.amount, input.currency, input.category,
                   input.raw_data, 'Unmatched', @importedAt, @importedAt
            FROM input
            ON CONFLICT ("BankConnectionId", "ProviderTransactionId") DO NOTHING
            RETURNING *
        ),
        shaped AS MATERIALIZED (
            SELECT inserted."Id" AS transaction_id,
                   jsonb_build_object(
                       'providerTransactionId', inserted."ProviderTransactionId", 'postedAt', inserted."PostedAt",
                       'authorizedAt', inserted."AuthorizedAt", 'description', inserted."Description",
                       'merchantName', inserted."MerchantName", 'amount', inserted."Amount",
                       'isoCurrencyCode', inserted."IsoCurrencyCode", 'category', inserted."Category",
                       'matchedPaymentId', inserted."MatchedPaymentId", 'matchedExpenseId', inserted."MatchedExpenseId",
                       'matchStatus', inserted."MatchStatus", 'matchConfidence', inserted."MatchConfidence",
                       'notes', inserted."Notes") AS new_values
            FROM inserted
        )
        SELECT (SELECT count(*) FROM shaped)::int AS "ImportedCount", 0 AS "ModifiedCount",
               0 AS "RemovedCount", 0 AS "ChangedEventCount",
               (@inputCount - (SELECT count(*) FROM shaped))::int AS "SkippedCount",
               COALESCE((SELECT jsonb_agg(transaction_id ORDER BY transaction_id) FROM shaped), '[]'::jsonb)::text
                   AS "AffectedTransactionIdsJson",
               COALESCE((SELECT jsonb_agg(jsonb_build_object(
                           'TransactionId', transaction_id, 'Operation', 'Created', 'OldValues', NULL,
                           'NewValues', new_values::text, 'Reason', 'Bank transaction imported.')
                         ORDER BY transaction_id) FROM shaped), '[]'::jsonb)::text AS "MutationsJson"
        """;
}
