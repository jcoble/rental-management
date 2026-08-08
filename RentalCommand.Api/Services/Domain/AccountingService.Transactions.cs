using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;

namespace RentalCommand.Api.Services.Domain;

public partial class AccountingService
{
    private async Task<AccountingTransactionsResponse> GetTransactionsPlanSafeAsync(
        WorkspaceReadScope scope,
        AccountingTransactionsQuery query,
        CancellationToken ct)
    {
        var countCommand = BuildTransactionSeedCommand(scope, query, includePageOrder: false);
        var totalCount = (await _db.Database
            .SqlQueryRaw<TransactionCountRow>(
                $"{countCommand.Sql}\nSELECT count(*)::integer AS \"Value\" FROM filtered_seed",
                countCommand.Parameters.ToArray())
            .SingleAsync(ct)).Value;

        var pageCommand = BuildTransactionSeedCommand(scope, query, includePageOrder: true);
        var pageKeys = await _db.Database
            .SqlQueryRaw<TransactionPageKeyRow>(
                $"""
                {pageCommand.Sql}
                SELECT
                    ordered_page."Kind" AS "Kind",
                    ordered_page."Id" AS "Id",
                    ordered_page."Ordinal" AS "Ordinal"
                FROM ordered_page
                ORDER BY ordered_page."Ordinal"
                """,
                pageCommand.Parameters.ToArray())
            .ToListAsync(ct);

        var facts = await LoadTransactionPageFactsAsync(scope, pageKeys, ct);
        var factsByKey = facts.ToDictionary(fact => (fact.Kind, fact.Id));
        var items = new List<AccountingTransactionResponse>(pageKeys.Count);
        foreach (var key in pageKeys)
        {
            if (!factsByKey.TryGetValue((key.Kind, key.Id), out var fact))
            {
                throw new InvalidOperationException(
                    $"Accounting transaction {key.Kind}/{key.Id} disappeared during page hydration.");
            }

            items.Add(fact.ToResponse());
        }

        return new AccountingTransactionsResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static TransactionSqlCommand BuildTransactionSeedCommand(
        WorkspaceReadScope scope,
        AccountingTransactionsQuery query,
        bool includePageOrder)
    {
        const string LedgerSearchJoins = """
            INNER JOIN "Properties" AS search_property
                ON search_property."PortfolioId" = authorized_accounts."PortfolioId"
               AND search_property."Id" = authorized_accounts."PropertyId"
               AND search_property."DeletedAt" IS NULL
            LEFT JOIN vw_lease_management_lifecycle AS search_lifecycle
                ON search_lifecycle."PortfolioId" = authorized_accounts."PortfolioId"
               AND search_lifecycle."LeaseManagementId" = authorized_accounts."LeaseManagementId"
            """;
        const string ExpenseSearchJoins = """
            INNER JOIN "Properties" AS search_property
                ON search_property."PortfolioId" = expense."PortfolioId"
               AND search_property."Id" = expense."PropertyId"
               AND search_property."DeletedAt" IS NULL
            LEFT JOIN "Vendors" AS search_vendor
                ON search_vendor."PortfolioId" = expense."PortfolioId"
               AND search_vendor."Id" = expense."VendorId"
            LEFT JOIN "WorkOrders" AS search_work_order
                ON search_work_order."PortfolioId" = expense."PortfolioId"
               AND search_work_order."Id" = expense."WorkOrderId"
            """;
        const string ApplicationSearchJoins = """
            INNER JOIN "Properties" AS search_property
                ON search_property."PortfolioId" = application_entry."PortfolioId"
               AND search_property."Id" = application_entry."PropertyId"
               AND search_property."DeletedAt" IS NULL
            """;

        var hasSearch = !string.IsNullOrWhiteSpace(query.Search);
        var needsDescription = hasSearch || query.SortField == "description";
        var ledgerSearchJoins = hasSearch ? LedgerSearchJoins : string.Empty;
        var expenseSearchJoins = hasSearch ? ExpenseSearchJoins : string.Empty;
        var applicationSearchJoins = hasSearch ? ApplicationSearchJoins : string.Empty;
        var ledgerPropertyName = hasSearch ? "search_property.\"Name\"" : "NULL::text";
        var ledgerCounterparty = hasSearch
            ? "search_lifecycle.\"CurrentPrimaryTenantName\""
            : "NULL::text";
        var ledgerReference = hasSearch ? "authorized_accounts.\"AccountNumber\"" : "NULL::text";
        var accountNumberProjection = hasSearch ? "account.\"AccountNumber\"" : "NULL::text";
        var ledgerDescription = needsDescription ? "entry.\"Description\"" : "NULL::text";
        var expensePropertyName = hasSearch ? "search_property.\"Name\"" : "NULL::text";
        var expenseCounterparty = hasSearch ? "search_vendor.\"Name\"" : "NULL::text";
        var expenseReference = hasSearch ? "search_work_order.\"Title\"" : "NULL::text";
        var expenseNotes = hasSearch ? "expense.\"Notes\"" : "NULL::text";
        var expenseDescription = needsDescription ? "expense.\"Description\"" : "NULL::text";
        var applicationPropertyName = hasSearch ? "search_property.\"Name\"" : "NULL::text";
        var applicationCounterparty = hasSearch ? "'Rental application'" : "NULL::text";
        var applicationDescription = needsDescription
            ? "application_entry.\"Description\""
            : "NULL::text";
        var applicationReference = hasSearch
            ? "COALESCE(application_entry.\"ProviderReference\", application_entry.\"SourceReference\", application_entry.\"Method\")"
            : "NULL::text";
        var searchPredicate = hasSearch
            ? """
                  AND (
                         seed."Description" ILIKE @searchLike
                      OR seed."PropertyName" ILIKE @searchLike
                      OR seed."Counterparty" ILIKE @searchLike
                      OR seed."Reference" ILIKE @searchLike
                      OR seed."Notes" ILIKE @searchLike
                  )
                """
            : string.Empty;

        var sql = $$"""
            WITH effective_scopes AS MATERIALIZED (
                SELECT effective_scope."ScopeKind", effective_scope."PropertyId"
                FROM public.rc_api_effective_capability_scopes(
                    @portfolioId,
                    @sessionId,
                    @userId,
                    @accessContextId,
                    @accessRevision,
                    @capabilityKeys,
                    @targetKind) AS effective_scope
            ),
            authorized_properties AS MATERIALIZED (
                SELECT property_row."Id" AS "PropertyId"
                FROM "Properties" AS property_row
                INNER JOIN "Portfolios" AS portfolio
                    ON portfolio."Id" = property_row."PortfolioId"
                   AND portfolio."DeletedAt" IS NULL
                WHERE property_row."PortfolioId" = @portfolioId
                  AND property_row."DeletedAt" IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM effective_scopes
                      WHERE effective_scopes."ScopeKind" = 'AllProperties'
                         OR (effective_scopes."ScopeKind" = 'SelectedProperties'
                             AND effective_scopes."PropertyId" = property_row."Id")
                  )
            ),
            authorized_accounts AS MATERIALIZED (
                SELECT
                    account."PortfolioId" AS "PortfolioId",
                    account."Id" AS "TenantAccountId",
                    account."LeaseManagementId" AS "LeaseManagementId",
                    management."PropertyId" AS "PropertyId",
                    management."UnitId" AS "UnitId",
                    {{accountNumberProjection}} AS "AccountNumber"
                FROM "TenantAccounts" AS account
                INNER JOIN "LeaseManagements" AS management
                    ON management."PortfolioId" = account."PortfolioId"
                   AND management."Id" = account."LeaseManagementId"
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = management."PropertyId"
                WHERE account."PortfolioId" = @portfolioId
            ),
            transaction_seed AS (
                SELECT
                    CASE
                        WHEN entry."EntryType" = 'PaymentReceipt' THEN 'Payment'
                        ELSE 'TenantLedger'
                    END AS "Kind",
                    entry."Id" AS "Id",
                    entry."PostedAtUtc" AS "Date",
                    entry."PostedAtUtc" AS "CreatedAt",
                    entry."PostedAtUtc" AS "UpdatedAt",
                    {{ledgerDescription}} AS "Description",
                    entry."EntryType" AS "Category",
                    entry."Direction" AS "Status",
                    CASE WHEN entry."Direction" = 'Credit'
                        THEN entry."Amount" ELSE -entry."Amount" END AS "Amount",
                    authorized_accounts."TenantAccountId" AS "TenantAccountId",
                    authorized_accounts."PropertyId" AS "PropertyId",
                    authorized_accounts."UnitId" AS "UnitId",
                    {{ledgerPropertyName}} AS "PropertyName",
                    {{ledgerCounterparty}} AS "Counterparty",
                    {{ledgerReference}} AS "Reference",
                    NULL::text AS "Notes"
                FROM authorized_accounts
                INNER JOIN "TenantLedgerEntries" AS entry
                    ON entry."PortfolioId" = authorized_accounts."PortfolioId"
                   AND entry."TenantAccountId" = authorized_accounts."TenantAccountId"
                {{ledgerSearchJoins}}
                WHERE entry."PortfolioId" = @portfolioId

                UNION ALL

                SELECT
                    'Expense' AS "Kind",
                    expense."Id"::bigint AS "Id",
                    COALESCE(expense."PaidAt", expense."IncurredAt") AS "Date",
                    expense."CreatedAt" AS "CreatedAt",
                    expense."UpdatedAt" AS "UpdatedAt",
                    {{expenseDescription}} AS "Description",
                    CASE expense."Category"
                        WHEN 0 THEN 'Advertising'
                        WHEN 1 THEN 'AutoTravel'
                        WHEN 2 THEN 'CleaningMaintenance'
                        WHEN 3 THEN 'Commissions'
                        WHEN 4 THEN 'Insurance'
                        WHEN 5 THEN 'LegalProfessional'
                        WHEN 6 THEN 'ManagementFees'
                        WHEN 7 THEN 'MortgageInterest'
                        WHEN 8 THEN 'Repairs'
                        WHEN 9 THEN 'Supplies'
                        WHEN 10 THEN 'Taxes'
                        WHEN 11 THEN 'Utilities'
                        WHEN 12 THEN 'Depreciation'
                        WHEN 13 THEN 'Other'
                        ELSE expense."Category"::text
                    END AS "Category",
                    CASE expense."Status"
                        WHEN 0 THEN 'Pending'
                        WHEN 1 THEN 'Approved'
                        WHEN 2 THEN 'Paid'
                        WHEN 3 THEN 'Rejected'
                        WHEN 4 THEN 'Draft'
                        ELSE expense."Status"::text
                    END AS "Status",
                    -expense."Amount" AS "Amount",
                    NULL::integer AS "TenantAccountId",
                    expense."PropertyId" AS "PropertyId",
                    expense."UnitId" AS "UnitId",
                    {{expensePropertyName}} AS "PropertyName",
                    {{expenseCounterparty}} AS "Counterparty",
                    {{expenseReference}} AS "Reference",
                    {{expenseNotes}} AS "Notes"
                FROM "Expenses" AS expense
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = expense."PropertyId"
                {{expenseSearchJoins}}
                WHERE expense."PortfolioId" = @portfolioId
                  AND expense."PropertyId" IS NOT NULL
                  AND expense."DeletedAt" IS NULL

                UNION ALL

                SELECT
                    'ApplicationFee' AS "Kind",
                    application_entry."Id"::bigint AS "Id",
                    application_entry."OccurredAtUtc" AS "Date",
                    application_entry."OccurredAtUtc" AS "CreatedAt",
                    application_entry."OccurredAtUtc" AS "UpdatedAt",
                    {{applicationDescription}} AS "Description",
                    CASE
                        WHEN application_entry."EntryType" = 'FeeCollection' THEN 'ApplicationFee'
                        WHEN application_entry."EntryType" = 'Refund' THEN 'ApplicationFeeRefund'
                        ELSE 'ApplicationFeeAdjustment'
                    END AS "Category",
                    'Posted' AS "Status",
                    CASE WHEN application_entry."Direction" = 'Increase'
                        THEN application_entry."Amount" ELSE -application_entry."Amount" END AS "Amount",
                    NULL::integer AS "TenantAccountId",
                    application_entry."PropertyId" AS "PropertyId",
                    application_entry."UnitId" AS "UnitId",
                    {{applicationPropertyName}} AS "PropertyName",
                    {{applicationCounterparty}} AS "Counterparty",
                    {{applicationReference}} AS "Reference",
                    NULL::text AS "Notes"
                FROM "ApplicationFinancialEntries" AS application_entry
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = application_entry."PropertyId"
                {{applicationSearchJoins}}
                WHERE application_entry."PortfolioId" = @portfolioId
                  AND application_entry."PropertyId" IS NOT NULL
            ),
            filtered_seed AS (
                SELECT seed.*
                FROM transaction_seed AS seed
                WHERE (@kind IS NULL OR seed."Kind" = @kind)
                  AND (@status IS NULL OR seed."Status" ILIKE @status)
                  AND (
                         @category IS NULL
                      OR (@tenantChargeCategory IS NULL AND seed."Category" ILIKE @category)
                      OR (@tenantChargeCategory IS NOT NULL AND (
                             seed."Category" = @tenantChargeCategory
                          OR (
                                 seed."Kind" = 'Payment'
                             AND seed."TenantAccountId" IS NOT NULL
                             AND EXISTS (
                                 SELECT 1
                                 FROM "TenantLedgerAllocations" AS allocation
                                 INNER JOIN "TenantLedgerEntries" AS debit_entry
                                     ON debit_entry."PortfolioId" = allocation."PortfolioId"
                                    AND debit_entry."TenantAccountId" = allocation."TenantAccountId"
                                    AND debit_entry."Id" = allocation."DebitEntryId"
                                 WHERE allocation."PortfolioId" = @portfolioId
                                   AND allocation."TenantAccountId" = seed."TenantAccountId"
                                   AND allocation."CreditEntryId" = seed."Id"
                                   AND debit_entry."EntryType" = @tenantChargeCategory
                             )
                          )
                      ))
                  )
                  AND (@propertyId IS NULL OR seed."PropertyId" = @propertyId)
                  AND (@fromUtc IS NULL OR seed."Date" >= @fromUtc)
                  AND (@throughExclusiveUtc IS NULL OR seed."Date" < @throughExclusiveUtc)
                {{searchPredicate}}
            )
            """;

        var parameters = new List<object>
        {
            new NpgsqlParameter<int>("portfolioId", scope.PortfolioId),
            new NpgsqlParameter<Guid>("sessionId", scope.SessionId),
            new NpgsqlParameter<int>("userId", scope.UserId),
            new NpgsqlParameter<int>("accessContextId", scope.AccessContextId),
            new NpgsqlParameter<long>("accessRevision", scope.AccessRevision),
            new NpgsqlParameter("capabilityKeys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { CapabilityKeys.MoneyBalancesRead },
            },
            new NpgsqlParameter<string>(
                "targetKind",
                CapabilityAuthorizationTargetKind.Property.ToString()),
            TransactionNullableParameter(
                "kind",
                NpgsqlDbType.Text,
                NormalizeTransactionKind(query.Kind)),
            TransactionNullableParameter(
                "status",
                NpgsqlDbType.Text,
                string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim()),
        };

        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim();
        var tenantChargeCategory = category != null &&
            TryResolveTenantChargeCategory(category, out var resolvedChargeCategory)
                ? resolvedChargeCategory.ToString()
                : null;
        parameters.Add(TransactionNullableParameter("category", NpgsqlDbType.Text, category));
        parameters.Add(TransactionNullableParameter(
            "tenantChargeCategory",
            NpgsqlDbType.Text,
            tenantChargeCategory));
        parameters.Add(TransactionNullableParameter(
            "propertyId",
            NpgsqlDbType.Integer,
            query.PropertyId));
        parameters.Add(TransactionNullableParameter(
            "fromUtc",
            NpgsqlDbType.TimestampTz,
            query.From?.ToUtc()));
        parameters.Add(TransactionNullableParameter(
            "throughExclusiveUtc",
            NpgsqlDbType.TimestampTz,
            query.To?.ToUtc().AddDays(1)));
        if (hasSearch)
        {
            parameters.Add(new NpgsqlParameter<string>("searchLike", $"%{query.Search!.Trim()}%"));
        }

        if (includePageOrder)
        {
            var filteredSeedOrder = TransactionOrderSql("filtered_seed");
            var pageSeedOrder = TransactionOrderSql("page_seed");
            sql += $"""
                ,
                page_seed AS MATERIALIZED (
                    SELECT
                        filtered_seed.*
                    FROM filtered_seed
                    ORDER BY
                        {filteredSeedOrder}
                    OFFSET @skip
                    LIMIT @take
                ),
                ordered_page AS (
                    SELECT
                        page_seed."Kind",
                        page_seed."Id",
                        row_number() OVER (
                            ORDER BY
                                {pageSeedOrder}
                        ) AS "Ordinal"
                    FROM page_seed
                )
                """;
            parameters.Add(new NpgsqlParameter<string>(
                "sortField",
                NormalizeTransactionSortField(query.SortField)));
            parameters.Add(new NpgsqlParameter<bool>("sortDescending", query.SortDescending));
            parameters.Add(new NpgsqlParameter<int>("skip", query.NormalizedSkip));
            parameters.Add(new NpgsqlParameter<int>("take", query.NormalizedTake));
        }

        return new TransactionSqlCommand(sql, parameters);
    }

    private static string TransactionOrderSql(string alias)
    {
        if (alias is not ("filtered_seed" or "page_seed"))
        {
            throw new ArgumentOutOfRangeException(nameof(alias));
        }

        return $"""
            CASE WHEN @sortField = 'amount' AND NOT @sortDescending
                THEN {alias}."Amount" END ASC,
            CASE WHEN @sortField = 'amount' AND @sortDescending
                THEN {alias}."Amount" END DESC,
            CASE WHEN @sortField = 'description' AND NOT @sortDescending
                THEN {alias}."Description" END ASC,
            CASE WHEN @sortField = 'description' AND @sortDescending
                THEN {alias}."Description" END DESC,
            CASE WHEN @sortField = 'category' AND NOT @sortDescending
                THEN {alias}."Category" END ASC,
            CASE WHEN @sortField = 'category' AND @sortDescending
                THEN {alias}."Category" END DESC,
            CASE WHEN @sortField = 'status' AND NOT @sortDescending
                THEN {alias}."Status" END ASC,
            CASE WHEN @sortField = 'status' AND @sortDescending
                THEN {alias}."Status" END DESC,
            CASE WHEN @sortField = 'kind' AND NOT @sortDescending
                THEN {alias}."Kind" END ASC,
            CASE WHEN @sortField = 'kind' AND @sortDescending
                THEN {alias}."Kind" END DESC,
            CASE WHEN @sortField IN ('amount', 'description', 'category', 'status', 'kind')
                THEN {alias}."Date" END DESC,
            CASE WHEN @sortField IN ('amount', 'description', 'category', 'status', 'kind')
                THEN {alias}."Id" END DESC,
            CASE WHEN @sortField = 'date' AND NOT @sortDescending
                THEN {alias}."Date" END ASC,
            CASE WHEN @sortField = 'date' AND NOT @sortDescending
                THEN {alias}."Id" END ASC,
            CASE WHEN @sortField = 'date' AND @sortDescending
                THEN {alias}."Date" END DESC,
            CASE WHEN @sortField = 'date' AND @sortDescending
                THEN {alias}."Id" END DESC,
            CASE WHEN @sortField = 'createdat' AND NOT @sortDescending
                THEN {alias}."CreatedAt" END ASC,
            CASE WHEN @sortField = 'createdat' AND NOT @sortDescending
                THEN {alias}."Id" END ASC,
            CASE WHEN @sortField = 'createdat' AND @sortDescending
                THEN {alias}."CreatedAt" END DESC,
            CASE WHEN @sortField = 'createdat' AND @sortDescending
                THEN {alias}."Id" END DESC,
            CASE WHEN @sortField = 'updatedat' AND NOT @sortDescending
                THEN {alias}."UpdatedAt" END ASC,
            CASE WHEN @sortField = 'updatedat' AND NOT @sortDescending
                THEN {alias}."Id" END ASC,
            CASE WHEN @sortField = 'updatedat' AND @sortDescending
                THEN {alias}."UpdatedAt" END DESC,
            CASE WHEN @sortField = 'updatedat' AND @sortDescending
                THEN {alias}."Id" END DESC,
            CASE WHEN @sortField = 'default'
                THEN {alias}."CreatedAt" END DESC,
            CASE WHEN @sortField = 'default'
                THEN {alias}."Id" END DESC
            """;
    }

    private async Task<IReadOnlyList<TransactionPageFactRow>> LoadTransactionPageFactsAsync(
        WorkspaceReadScope scope,
        IReadOnlyList<TransactionPageKeyRow> pageKeys,
        CancellationToken ct)
    {
        if (pageKeys.Count == 0)
        {
            return await _db.Database.SqlQueryRaw<TransactionPageFactRow>("""
                SELECT
                    NULL::text AS "Kind",
                    NULL::bigint AS "Id",
                    NULL::timestamptz AS "Date",
                    NULL::timestamptz AS "CreatedAt",
                    NULL::timestamptz AS "UpdatedAt",
                    NULL::text AS "Description",
                    NULL::text AS "Category",
                    NULL::text AS "Status",
                    NULL::numeric AS "Amount",
                    NULL::integer AS "TenantAccountId",
                    NULL::integer AS "PropertyId",
                    NULL::integer AS "UnitId",
                    NULL::text AS "PropertyName",
                    NULL::text AS "Counterparty",
                    FALSE AS "HasReceipt",
                    FALSE AS "ReceiptIsImage",
                    FALSE AS "Reconciled",
                    NULL::text AS "ClearedBankName",
                    NULL::timestamptz AS "ClearedAt",
                    NULL::bigint AS "Ordinal"
                WHERE FALSE
                """).ToListAsync(ct);
        }

        var ledgerIds = pageKeys
            .Where(key => key.Kind is KindTenantLedger or KindPayment)
            .Select(key => key.Id)
            .ToArray();
        var expenseIds = pageKeys
            .Where(key => key.Kind == KindExpense)
            .Select(key => checked((int)key.Id))
            .ToArray();
        var applicationIds = pageKeys
            .Where(key => key.Kind == KindApplicationFee)
            .Select(key => checked((int)key.Id))
            .ToArray();

        var ctes = new List<string>
        {
            """
            effective_scopes AS MATERIALIZED (
                SELECT effective_scope."ScopeKind", effective_scope."PropertyId"
                FROM public.rc_api_effective_capability_scopes(
                    @portfolioId,
                    @sessionId,
                    @userId,
                    @accessContextId,
                    @accessRevision,
                    @capabilityKeys,
                    @targetKind) AS effective_scope
            )
            """,
            """
            authorized_properties AS MATERIALIZED (
                SELECT property_row."Id" AS "PropertyId"
                FROM "Properties" AS property_row
                INNER JOIN "Portfolios" AS portfolio
                    ON portfolio."Id" = property_row."PortfolioId"
                   AND portfolio."DeletedAt" IS NULL
                WHERE property_row."PortfolioId" = @portfolioId
                  AND property_row."DeletedAt" IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM effective_scopes
                      WHERE effective_scopes."ScopeKind" = 'AllProperties'
                         OR (effective_scopes."ScopeKind" = 'SelectedProperties'
                             AND effective_scopes."PropertyId" = property_row."Id")
                  )
            )
            """,
        };
        var branches = new List<string>();

        if (ledgerIds.Length > 0)
        {
            ctes.Add("""
                authorized_accounts AS MATERIALIZED (
                    SELECT
                        account."PortfolioId" AS "PortfolioId",
                        account."Id" AS "TenantAccountId",
                        account."LeaseManagementId" AS "LeaseManagementId",
                        management."PropertyId" AS "PropertyId",
                        management."UnitId" AS "UnitId",
                        NULL::text AS "AccountNumber"
                    FROM "TenantAccounts" AS account
                    INNER JOIN "LeaseManagements" AS management
                        ON management."PortfolioId" = account."PortfolioId"
                       AND management."Id" = account."LeaseManagementId"
                    INNER JOIN authorized_properties
                        ON authorized_properties."PropertyId" = management."PropertyId"
                    WHERE account."PortfolioId" = @portfolioId
                )
                """);
            ctes.Add("""
                page_ledger_entries AS MATERIALIZED (
                    SELECT entry.*
                    FROM "TenantLedgerEntries" AS entry
                    WHERE entry."PortfolioId" = @portfolioId
                      AND entry."Id" = ANY(@ledgerIds::bigint[])
                )
                """);
            branches.Add("""
                SELECT
                    CASE WHEN entry."EntryType" = 'PaymentReceipt'
                        THEN 'Payment' ELSE 'TenantLedger' END AS "Kind",
                    entry."Id" AS "Id",
                    entry."PostedAtUtc" AS "Date",
                    entry."PostedAtUtc" AS "CreatedAt",
                    entry."PostedAtUtc" AS "UpdatedAt",
                    entry."Description" AS "Description",
                    entry."EntryType" AS "Category",
                    entry."Direction" AS "Status",
                    CASE WHEN entry."Direction" = 'Credit'
                        THEN entry."Amount" ELSE -entry."Amount" END AS "Amount",
                    authorized_accounts."TenantAccountId" AS "TenantAccountId",
                    authorized_accounts."PropertyId" AS "PropertyId",
                    authorized_accounts."UnitId" AS "UnitId",
                    property_row."Name" AS "PropertyName",
                    lifecycle."CurrentPrimaryTenantName" AS "Counterparty",
                    FALSE AS "HasReceipt",
                    FALSE AS "ReceiptIsImage",
                    FALSE AS "Reconciled",
                    NULL::text AS "ClearedBankName",
                    NULL::timestamptz AS "ClearedAt"
                FROM page_ledger_entries AS entry
                INNER JOIN authorized_accounts
                    ON authorized_accounts."PortfolioId" = entry."PortfolioId"
                   AND authorized_accounts."TenantAccountId" = entry."TenantAccountId"
                INNER JOIN "Properties" AS property_row
                    ON property_row."PortfolioId" = authorized_accounts."PortfolioId"
                   AND property_row."Id" = authorized_accounts."PropertyId"
                   AND property_row."DeletedAt" IS NULL
                LEFT JOIN vw_lease_management_lifecycle AS lifecycle
                    ON lifecycle."PortfolioId" = authorized_accounts."PortfolioId"
                   AND lifecycle."LeaseManagementId" = authorized_accounts."LeaseManagementId"
                """);
        }

        if (expenseIds.Length > 0)
        {
            ctes.Add("""
                page_expenses AS MATERIALIZED (
                    SELECT expense.*
                    FROM "Expenses" AS expense
                    WHERE expense."PortfolioId" = @portfolioId
                      AND expense."PropertyId" IS NOT NULL
                      AND expense."DeletedAt" IS NULL
                      AND expense."Id" = ANY(@expenseIds::integer[])
                )
                """);
            ctes.Add("""
                page_receipts AS MATERIALIZED (
                    SELECT
                        file."EntityId" AS "ExpenseId",
                        file."Id" AS "Id",
                        file."UploadedAt" AS "UploadedAt",
                        file."ContentType" AS "ContentType"
                    FROM "StoredFiles" AS file
                    WHERE file."PortfolioId" = @portfolioId
                      AND file."EntityType" = 'Expense'
                      AND file."EntityId" IS NOT NULL
                      AND file."DeletedAt" IS NULL
                      AND file."EntityId" = ANY(@expenseIds::bigint[])
                )
                """);
            ctes.Add("""
                receipt_facts AS (
                    SELECT
                        file."ExpenseId" AS "ExpenseId",
                        TRUE AS "HasReceipt",
                        (array_agg(
                            file."ContentType" LIKE 'image/%'
                            ORDER BY file."UploadedAt" DESC, file."Id" DESC))[1]
                            AS "ReceiptIsImage"
                    FROM page_receipts AS file
                    GROUP BY file."ExpenseId"
                )
                """);
            ctes.Add("""
                page_bank_matches AS MATERIALIZED (
                    SELECT
                        bank."MatchedExpenseId" AS "ExpenseId",
                        bank."Id" AS "Id",
                        bank."BankConnectionId" AS "BankConnectionId",
                        bank."PostedAt" AS "PostedAt"
                    FROM "BankTransactions" AS bank
                    WHERE bank."PortfolioId" = @portfolioId
                      AND bank."MatchStatus" = 'Matched'
                      AND bank."MatchedExpenseId" IS NOT NULL
                      AND bank."MatchedExpenseId" = ANY(@expenseIds::integer[])
                )
                """);
            ctes.Add("""
                bank_facts AS (
                    SELECT
                        bank."ExpenseId" AS "ExpenseId",
                        TRUE AS "Reconciled",
                        (array_agg(
                            connection."InstitutionName"
                            ORDER BY bank."PostedAt" DESC, bank."Id" DESC))[1]
                            AS "ClearedBankName",
                        (array_agg(
                            bank."PostedAt"
                            ORDER BY bank."PostedAt" DESC, bank."Id" DESC))[1]
                            AS "ClearedAt"
                    FROM page_bank_matches AS bank
                    INNER JOIN "BankConnections" AS connection
                        ON connection."PortfolioId" = @portfolioId
                       AND connection."Id" = bank."BankConnectionId"
                    GROUP BY bank."ExpenseId"
                )
                """);
            branches.Add("""
                SELECT
                    'Expense' AS "Kind",
                    expense."Id"::bigint AS "Id",
                    COALESCE(expense."PaidAt", expense."IncurredAt") AS "Date",
                    expense."CreatedAt" AS "CreatedAt",
                    expense."UpdatedAt" AS "UpdatedAt",
                    expense."Description" AS "Description",
                    CASE expense."Category"
                        WHEN 0 THEN 'Advertising'
                        WHEN 1 THEN 'AutoTravel'
                        WHEN 2 THEN 'CleaningMaintenance'
                        WHEN 3 THEN 'Commissions'
                        WHEN 4 THEN 'Insurance'
                        WHEN 5 THEN 'LegalProfessional'
                        WHEN 6 THEN 'ManagementFees'
                        WHEN 7 THEN 'MortgageInterest'
                        WHEN 8 THEN 'Repairs'
                        WHEN 9 THEN 'Supplies'
                        WHEN 10 THEN 'Taxes'
                        WHEN 11 THEN 'Utilities'
                        WHEN 12 THEN 'Depreciation'
                        WHEN 13 THEN 'Other'
                        ELSE expense."Category"::text
                    END AS "Category",
                    CASE expense."Status"
                        WHEN 0 THEN 'Pending'
                        WHEN 1 THEN 'Approved'
                        WHEN 2 THEN 'Paid'
                        WHEN 3 THEN 'Rejected'
                        WHEN 4 THEN 'Draft'
                        ELSE expense."Status"::text
                    END AS "Status",
                    -expense."Amount" AS "Amount",
                    NULL::integer AS "TenantAccountId",
                    expense."PropertyId" AS "PropertyId",
                    expense."UnitId" AS "UnitId",
                    property_row."Name" AS "PropertyName",
                    vendor."Name" AS "Counterparty",
                    COALESCE(receipt."HasReceipt", FALSE) AS "HasReceipt",
                    COALESCE(receipt."ReceiptIsImage", FALSE) AS "ReceiptIsImage",
                    COALESCE(bank."Reconciled", FALSE) AS "Reconciled",
                    bank."ClearedBankName" AS "ClearedBankName",
                    bank."ClearedAt" AS "ClearedAt"
                FROM page_expenses AS expense
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = expense."PropertyId"
                INNER JOIN "Properties" AS property_row
                    ON property_row."PortfolioId" = expense."PortfolioId"
                   AND property_row."Id" = expense."PropertyId"
                   AND property_row."DeletedAt" IS NULL
                LEFT JOIN "Vendors" AS vendor
                    ON vendor."PortfolioId" = expense."PortfolioId"
                   AND vendor."Id" = expense."VendorId"
                LEFT JOIN receipt_facts AS receipt
                    ON receipt."ExpenseId" = expense."Id"::bigint
                LEFT JOIN bank_facts AS bank
                    ON bank."ExpenseId" = expense."Id"
                """);
        }

        if (applicationIds.Length > 0)
        {
            ctes.Add("""
                page_application_entries AS MATERIALIZED (
                    SELECT application_entry.*
                    FROM "ApplicationFinancialEntries" AS application_entry
                    WHERE application_entry."PortfolioId" = @portfolioId
                      AND application_entry."PropertyId" IS NOT NULL
                      AND application_entry."Id" = ANY(@applicationIds::integer[])
                )
                """);
            branches.Add("""
                SELECT
                    'ApplicationFee' AS "Kind",
                    application_entry."Id"::bigint AS "Id",
                    application_entry."OccurredAtUtc" AS "Date",
                    application_entry."OccurredAtUtc" AS "CreatedAt",
                    application_entry."OccurredAtUtc" AS "UpdatedAt",
                    application_entry."Description" AS "Description",
                    CASE
                        WHEN application_entry."EntryType" = 'FeeCollection' THEN 'ApplicationFee'
                        WHEN application_entry."EntryType" = 'Refund' THEN 'ApplicationFeeRefund'
                        ELSE 'ApplicationFeeAdjustment'
                    END AS "Category",
                    'Posted' AS "Status",
                    CASE WHEN application_entry."Direction" = 'Increase'
                        THEN application_entry."Amount" ELSE -application_entry."Amount" END AS "Amount",
                    NULL::integer AS "TenantAccountId",
                    application_entry."PropertyId" AS "PropertyId",
                    application_entry."UnitId" AS "UnitId",
                    property_row."Name" AS "PropertyName",
                    'Rental application' AS "Counterparty",
                    FALSE AS "HasReceipt",
                    FALSE AS "ReceiptIsImage",
                    FALSE AS "Reconciled",
                    NULL::text AS "ClearedBankName",
                    NULL::timestamptz AS "ClearedAt"
                FROM page_application_entries AS application_entry
                INNER JOIN authorized_properties
                    ON authorized_properties."PropertyId" = application_entry."PropertyId"
                INNER JOIN "Properties" AS property_row
                    ON property_row."PortfolioId" = application_entry."PortfolioId"
                   AND property_row."Id" = application_entry."PropertyId"
                   AND property_row."DeletedAt" IS NULL
                """);
        }

        ctes.Add("""
            page_keys AS MATERIALIZED (
                SELECT page_key."Kind", page_key."Id", page_key."Ordinal"
                FROM unnest(@pageKinds::text[], @pageIds::bigint[])
                    WITH ORDINALITY AS page_key("Kind", "Id", "Ordinal")
            )
            """);
        ctes.Add($"""
            page_facts AS (
                {string.Join("\nUNION ALL\n", branches)}
            )
            """);

        var sql = $"""
            WITH {string.Join(",\n", ctes)}
            SELECT
                page_facts."Kind" AS "Kind",
                page_facts."Id" AS "Id",
                page_facts."Date" AS "Date",
                page_facts."CreatedAt" AS "CreatedAt",
                page_facts."UpdatedAt" AS "UpdatedAt",
                page_facts."Description" AS "Description",
                page_facts."Category" AS "Category",
                page_facts."Status" AS "Status",
                page_facts."Amount" AS "Amount",
                page_facts."TenantAccountId" AS "TenantAccountId",
                page_facts."PropertyId" AS "PropertyId",
                page_facts."UnitId" AS "UnitId",
                page_facts."PropertyName" AS "PropertyName",
                page_facts."Counterparty" AS "Counterparty",
                page_facts."HasReceipt" AS "HasReceipt",
                page_facts."ReceiptIsImage" AS "ReceiptIsImage",
                page_facts."Reconciled" AS "Reconciled",
                page_facts."ClearedBankName" AS "ClearedBankName",
                page_facts."ClearedAt" AS "ClearedAt",
                page_keys."Ordinal" AS "Ordinal"
            FROM page_facts
            INNER JOIN page_keys
                ON page_keys."Kind" = page_facts."Kind"
               AND page_keys."Id" = page_facts."Id"
            ORDER BY page_keys."Ordinal"
            """;

        var parameters = new List<object>
        {
            new NpgsqlParameter<int>("portfolioId", scope.PortfolioId),
            new NpgsqlParameter<Guid>("sessionId", scope.SessionId),
            new NpgsqlParameter<int>("userId", scope.UserId),
            new NpgsqlParameter<int>("accessContextId", scope.AccessContextId),
            new NpgsqlParameter<long>("accessRevision", scope.AccessRevision),
            new NpgsqlParameter("capabilityKeys", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = new[] { CapabilityKeys.MoneyBalancesRead },
            },
            new NpgsqlParameter<string>(
                "targetKind",
                CapabilityAuthorizationTargetKind.Property.ToString()),
            new NpgsqlParameter("pageKinds", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = pageKeys.Select(key => key.Kind).ToArray(),
            },
            new NpgsqlParameter("pageIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = pageKeys.Select(key => key.Id).ToArray(),
            },
        };
        if (ledgerIds.Length > 0)
        {
            parameters.Add(new NpgsqlParameter("ledgerIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = ledgerIds,
            });
        }
        if (expenseIds.Length > 0)
        {
            parameters.Add(new NpgsqlParameter("expenseIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = expenseIds,
            });
        }
        if (applicationIds.Length > 0)
        {
            parameters.Add(new NpgsqlParameter("applicationIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = applicationIds,
            });
        }

        return await _db.Database
            .SqlQueryRaw<TransactionPageFactRow>(sql, parameters.ToArray())
            .ToListAsync(ct);
    }

    private static string? NormalizeTransactionKind(string? requestedKind)
    {
        if (string.IsNullOrWhiteSpace(requestedKind))
        {
            return null;
        }

        var kind = requestedKind.Trim();
        if (kind.Equals(KindExpense, StringComparison.OrdinalIgnoreCase)) return KindExpense;
        if (kind.Equals(KindTenantLedger, StringComparison.OrdinalIgnoreCase)) return KindTenantLedger;
        if (kind.Equals(KindPayment, StringComparison.OrdinalIgnoreCase)) return KindPayment;
        if (kind.Equals(KindBank, StringComparison.OrdinalIgnoreCase)) return KindBank;
        if (kind.Equals(KindApplicationFee, StringComparison.OrdinalIgnoreCase)) return KindApplicationFee;
        return null;
    }

    private static string NormalizeTransactionSortField(string? sortField) => sortField switch
    {
        "amount" or "description" or "category" or "status" or "kind" or
        "date" or "createdat" or "updatedat" => sortField,
        _ => "default",
    };

    private static NpgsqlParameter TransactionNullableParameter(
        string name,
        NpgsqlDbType type,
        object? value) => new(name, type)
        {
            Value = value ?? DBNull.Value,
        };

    private sealed record TransactionSqlCommand(string Sql, List<object> Parameters);

    private sealed class TransactionCountRow
    {
        public int Value { get; init; }
    }

    private sealed class TransactionPageKeyRow
    {
        public string Kind { get; init; } = string.Empty;
        public long Id { get; init; }
        public long Ordinal { get; init; }
    }

    private sealed class TransactionPageFactRow
    {
        public string Kind { get; init; } = string.Empty;
        public long Id { get; init; }
        public DateTime Date { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string Description { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public int? TenantAccountId { get; init; }
        public int? PropertyId { get; init; }
        public int? UnitId { get; init; }
        public string? PropertyName { get; init; }
        public string? Counterparty { get; init; }
        public bool HasReceipt { get; init; }
        public bool ReceiptIsImage { get; init; }
        public bool Reconciled { get; init; }
        public string? ClearedBankName { get; init; }
        public DateTime? ClearedAt { get; init; }
        public long Ordinal { get; init; }

        public AccountingTransactionResponse ToResponse() => new()
        {
            Kind = Kind,
            Id = Id,
            Date = Date,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Description = Description,
            Category = Category,
            Status = Status,
            Amount = Amount,
            TenantAccountId = TenantAccountId,
            PropertyId = PropertyId,
            UnitId = UnitId,
            PropertyName = PropertyName,
            Counterparty = Counterparty,
            DetailHref = DetailHrefFor(Kind, Id, TenantAccountId),
            HasReceipt = HasReceipt,
            ReceiptIsImage = ReceiptIsImage,
            Reconciled = Reconciled,
            ClearedBankName = ClearedBankName,
            ClearedAt = ClearedAt,
        };
    }
}
