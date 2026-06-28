using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitIdToAccountingTransactionsView : Migration
    {
        // Adds a "UnitId" column to vw_accounting_transactions so the accounting ledger can deep-link a
        // unit-tied Payment/Expense row into that unit's Command Center tab. The column is the LAST
        // column of each of the three UNION ALL SELECTs so the union order stays aligned:
        //   - Payments: l."UnitId"  (the live-Lease INNER JOIN is already present)
        //   - Expenses: e."UnitId"  (Expense has a nullable UnitId column)
        //   - Bank:     NULL::integer (bank rows have no unit)
        //
        // Everything else is copied VERBATIM from AddAccountingTransactionsView: the view keeps
        // WITH (security_invoker = true) (base-table RLS applies to the querying rentalcommand_api role),
        // the soft-delete predicates inside the SQL (live Lease for payments, own DeletedAt for expenses,
        // live Portfolio for bank; filtered LEFT joins null labels instead of dropping rows), the enum
        // CASE maps, and the explicit GRANT SELECT to rentalcommand_api.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP VIEW IF EXISTS vw_accounting_transactions;");

            migrationBuilder.Sql(@"
CREATE VIEW vw_accounting_transactions WITH (security_invoker = true) AS
    -- Payments (live lease required; tenant/property names via filtered LEFT joins)
    SELECT
        'Payment'::text                                    AS ""Kind"",
        p.""Id""                                           AS ""Id"",
        p.""PortfolioId""                                  AS ""PortfolioId"",
        COALESCE(p.""PaidDate"", p.""DueDate"")            AS ""Date"",
        p.""CreatedAt""                                    AS ""CreatedAt"",
        p.""UpdatedAt""                                    AS ""UpdatedAt"",
        CASE
            WHEN p.""Notes"" IS NOT NULL AND p.""Notes"" <> ''
                THEN p.""Notes""
            ELSE
                CASE p.""PaymentType""
                    WHEN 0 THEN 'Rent' WHEN 1 THEN 'SecurityDeposit' WHEN 2 THEN 'LateFee'
                    WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' ELSE p.""PaymentType""::text
                END
                || ' - ' || COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')
        END                                                AS ""Description"",
        CASE p.""PaymentType""
            WHEN 0 THEN 'Rent' WHEN 1 THEN 'SecurityDeposit' WHEN 2 THEN 'LateFee'
            WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' ELSE p.""PaymentType""::text
        END                                                AS ""Category"",
        CASE p.""Status""
            WHEN 0 THEN 'Scheduled' WHEN 1 THEN 'Paid' WHEN 2 THEN 'Partial' WHEN 3 THEN 'Late'
            WHEN 4 THEN 'Waived' WHEN 5 THEN 'Failed' WHEN 6 THEN 'Refunded' ELSE p.""Status""::text
        END                                                AS ""Status"",
        p.""Amount""                                       AS ""Amount"",
        l.""PropertyId""                                   AS ""PropertyId"",
        prop.""Name""                                      AS ""PropertyName"",
        (COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')) AS ""Counterparty"",
        (COALESCE(l.""LeaseNumber"", '') || ' ' || COALESCE(p.""Method"", '') || ' ' || COALESCE(p.""ExternalReference"", '')) AS ""Reference"",
        p.""Notes""                                        AS ""Notes"",
        l.""UnitId""                                       AS ""UnitId""
    FROM ""Payments"" p
    INNER JOIN ""Leases"" l   ON l.""Id"" = p.""LeaseId"" AND l.""DeletedAt"" IS NULL
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = l.""PropertyId"" AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Tenants"" ten     ON ten.""Id"" = l.""TenantId""   AND ten.""DeletedAt"" IS NULL

    UNION ALL

    -- Expenses (own soft-delete; property/vendor/work-order via filtered LEFT joins)
    SELECT
        'Expense'::text                                    AS ""Kind"",
        e.""Id""                                           AS ""Id"",
        e.""PortfolioId""                                  AS ""PortfolioId"",
        COALESCE(e.""PaidAt"", e.""IncurredAt"")           AS ""Date"",
        e.""CreatedAt""                                    AS ""CreatedAt"",
        e.""UpdatedAt""                                    AS ""UpdatedAt"",
        e.""Description""                                  AS ""Description"",
        CASE e.""Category""
            WHEN 0 THEN 'Advertising' WHEN 1 THEN 'AutoTravel' WHEN 2 THEN 'CleaningMaintenance'
            WHEN 3 THEN 'Commissions' WHEN 4 THEN 'Insurance' WHEN 5 THEN 'LegalProfessional'
            WHEN 6 THEN 'ManagementFees' WHEN 7 THEN 'MortgageInterest' WHEN 8 THEN 'Repairs'
            WHEN 9 THEN 'Supplies' WHEN 10 THEN 'Taxes' WHEN 11 THEN 'Utilities'
            WHEN 12 THEN 'Depreciation' WHEN 13 THEN 'Other' ELSE e.""Category""::text
        END                                                AS ""Category"",
        CASE e.""Status""
            WHEN 0 THEN 'Pending' WHEN 1 THEN 'Approved' WHEN 2 THEN 'Paid'
            WHEN 3 THEN 'Rejected' WHEN 4 THEN 'Draft' ELSE e.""Status""::text
        END                                                AS ""Status"",
        e.""Amount""                                       AS ""Amount"",
        e.""PropertyId""                                   AS ""PropertyId"",
        prop.""Name""                                      AS ""PropertyName"",
        v.""Name""                                         AS ""Counterparty"",
        wo.""Title""                                       AS ""Reference"",
        e.""Notes""                                        AS ""Notes"",
        e.""UnitId""                                       AS ""UnitId""
    FROM ""Expenses"" e
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = e.""PropertyId""  AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Vendors"" v       ON v.""Id"" = e.""VendorId""      AND v.""DeletedAt"" IS NULL
    LEFT JOIN ""WorkOrders"" wo   ON wo.""Id"" = e.""WorkOrderId""  AND wo.""DeletedAt"" IS NULL
    WHERE e.""DeletedAt"" IS NULL

    UNION ALL

    -- Bank transactions (unmatched + not removed; live portfolio; institution via LEFT join)
    SELECT
        'Bank'::text                                       AS ""Kind"",
        t.""Id""                                           AS ""Id"",
        t.""PortfolioId""                                  AS ""PortfolioId"",
        t.""PostedAt""                                     AS ""Date"",
        t.""CreatedAt""                                    AS ""CreatedAt"",
        t.""UpdatedAt""                                    AS ""UpdatedAt"",
        t.""Description""                                  AS ""Description"",
        COALESCE(t.""Category"", CASE WHEN t.""Amount"" >= 0 THEN 'Deposit' ELSE 'Withdrawal' END) AS ""Category"",
        t.""MatchStatus""                                  AS ""Status"",
        t.""Amount""                                       AS ""Amount"",
        NULL::integer                                      AS ""PropertyId"",
        NULL::text                                         AS ""PropertyName"",
        COALESCE(t.""MerchantName"", bc.""InstitutionName"") AS ""Counterparty"",
        (COALESCE(bc.""AccountName"", '') || ' ' || COALESCE(t.""ProviderTransactionId"", '')) AS ""Reference"",
        t.""Notes""                                        AS ""Notes"",
        NULL::integer                                      AS ""UnitId""
    FROM ""BankTransactions"" t
    INNER JOIN ""Portfolios"" po ON po.""Id"" = t.""PortfolioId"" AND po.""DeletedAt"" IS NULL
    LEFT JOIN ""BankConnections"" bc ON bc.""Id"" = t.""BankConnectionId""
    WHERE t.""MatchStatus"" <> 'Removed';
");

            // Re-grant read access to the API role (the view was dropped and recreated by the owner).
            migrationBuilder.Sql(
                "GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate the ORIGINAL view (no UnitId column), identical to AddAccountingTransactionsView.
            migrationBuilder.Sql(@"DROP VIEW IF EXISTS vw_accounting_transactions;");

            migrationBuilder.Sql(@"
CREATE VIEW vw_accounting_transactions WITH (security_invoker = true) AS
    -- Payments (live lease required; tenant/property names via filtered LEFT joins)
    SELECT
        'Payment'::text                                    AS ""Kind"",
        p.""Id""                                           AS ""Id"",
        p.""PortfolioId""                                  AS ""PortfolioId"",
        COALESCE(p.""PaidDate"", p.""DueDate"")            AS ""Date"",
        p.""CreatedAt""                                    AS ""CreatedAt"",
        p.""UpdatedAt""                                    AS ""UpdatedAt"",
        CASE
            WHEN p.""Notes"" IS NOT NULL AND p.""Notes"" <> ''
                THEN p.""Notes""
            ELSE
                CASE p.""PaymentType""
                    WHEN 0 THEN 'Rent' WHEN 1 THEN 'SecurityDeposit' WHEN 2 THEN 'LateFee'
                    WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' ELSE p.""PaymentType""::text
                END
                || ' - ' || COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')
        END                                                AS ""Description"",
        CASE p.""PaymentType""
            WHEN 0 THEN 'Rent' WHEN 1 THEN 'SecurityDeposit' WHEN 2 THEN 'LateFee'
            WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' ELSE p.""PaymentType""::text
        END                                                AS ""Category"",
        CASE p.""Status""
            WHEN 0 THEN 'Scheduled' WHEN 1 THEN 'Paid' WHEN 2 THEN 'Partial' WHEN 3 THEN 'Late'
            WHEN 4 THEN 'Waived' WHEN 5 THEN 'Failed' WHEN 6 THEN 'Refunded' ELSE p.""Status""::text
        END                                                AS ""Status"",
        p.""Amount""                                       AS ""Amount"",
        l.""PropertyId""                                   AS ""PropertyId"",
        prop.""Name""                                      AS ""PropertyName"",
        (COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')) AS ""Counterparty"",
        (COALESCE(l.""LeaseNumber"", '') || ' ' || COALESCE(p.""Method"", '') || ' ' || COALESCE(p.""ExternalReference"", '')) AS ""Reference"",
        p.""Notes""                                        AS ""Notes""
    FROM ""Payments"" p
    INNER JOIN ""Leases"" l   ON l.""Id"" = p.""LeaseId"" AND l.""DeletedAt"" IS NULL
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = l.""PropertyId"" AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Tenants"" ten     ON ten.""Id"" = l.""TenantId""   AND ten.""DeletedAt"" IS NULL

    UNION ALL

    -- Expenses (own soft-delete; property/vendor/work-order via filtered LEFT joins)
    SELECT
        'Expense'::text                                    AS ""Kind"",
        e.""Id""                                           AS ""Id"",
        e.""PortfolioId""                                  AS ""PortfolioId"",
        COALESCE(e.""PaidAt"", e.""IncurredAt"")           AS ""Date"",
        e.""CreatedAt""                                    AS ""CreatedAt"",
        e.""UpdatedAt""                                    AS ""UpdatedAt"",
        e.""Description""                                  AS ""Description"",
        CASE e.""Category""
            WHEN 0 THEN 'Advertising' WHEN 1 THEN 'AutoTravel' WHEN 2 THEN 'CleaningMaintenance'
            WHEN 3 THEN 'Commissions' WHEN 4 THEN 'Insurance' WHEN 5 THEN 'LegalProfessional'
            WHEN 6 THEN 'ManagementFees' WHEN 7 THEN 'MortgageInterest' WHEN 8 THEN 'Repairs'
            WHEN 9 THEN 'Supplies' WHEN 10 THEN 'Taxes' WHEN 11 THEN 'Utilities'
            WHEN 12 THEN 'Depreciation' WHEN 13 THEN 'Other' ELSE e.""Category""::text
        END                                                AS ""Category"",
        CASE e.""Status""
            WHEN 0 THEN 'Pending' WHEN 1 THEN 'Approved' WHEN 2 THEN 'Paid'
            WHEN 3 THEN 'Rejected' WHEN 4 THEN 'Draft' ELSE e.""Status""::text
        END                                                AS ""Status"",
        e.""Amount""                                       AS ""Amount"",
        e.""PropertyId""                                   AS ""PropertyId"",
        prop.""Name""                                      AS ""PropertyName"",
        v.""Name""                                         AS ""Counterparty"",
        wo.""Title""                                       AS ""Reference"",
        e.""Notes""                                        AS ""Notes""
    FROM ""Expenses"" e
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = e.""PropertyId""  AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Vendors"" v       ON v.""Id"" = e.""VendorId""      AND v.""DeletedAt"" IS NULL
    LEFT JOIN ""WorkOrders"" wo   ON wo.""Id"" = e.""WorkOrderId""  AND wo.""DeletedAt"" IS NULL
    WHERE e.""DeletedAt"" IS NULL

    UNION ALL

    -- Bank transactions (unmatched + not removed; live portfolio; institution via LEFT join)
    SELECT
        'Bank'::text                                       AS ""Kind"",
        t.""Id""                                           AS ""Id"",
        t.""PortfolioId""                                  AS ""PortfolioId"",
        t.""PostedAt""                                     AS ""Date"",
        t.""CreatedAt""                                    AS ""CreatedAt"",
        t.""UpdatedAt""                                    AS ""UpdatedAt"",
        t.""Description""                                  AS ""Description"",
        COALESCE(t.""Category"", CASE WHEN t.""Amount"" >= 0 THEN 'Deposit' ELSE 'Withdrawal' END) AS ""Category"",
        t.""MatchStatus""                                  AS ""Status"",
        t.""Amount""                                       AS ""Amount"",
        NULL::integer                                      AS ""PropertyId"",
        NULL::text                                         AS ""PropertyName"",
        COALESCE(t.""MerchantName"", bc.""InstitutionName"") AS ""Counterparty"",
        (COALESCE(bc.""AccountName"", '') || ' ' || COALESCE(t.""ProviderTransactionId"", '')) AS ""Reference"",
        t.""Notes""                                        AS ""Notes""
    FROM ""BankTransactions"" t
    INNER JOIN ""Portfolios"" po ON po.""Id"" = t.""PortfolioId"" AND po.""DeletedAt"" IS NULL
    LEFT JOIN ""BankConnections"" bc ON bc.""Id"" = t.""BankConnectionId""
    WHERE t.""MatchStatus"" <> 'Removed';
");

            migrationBuilder.Sql(
                "GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;");
        }
    }
}
