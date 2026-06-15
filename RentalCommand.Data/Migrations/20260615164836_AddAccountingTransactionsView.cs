using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingTransactionsView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // vw_accounting_transactions — UNION ALL of Payments + Expenses + unmatched, non-removed
            // BankTransactions, exposing the filterable/sortable/pageable core columns of the
            // accounting transactions grid as real SQL columns. Backs the keyless
            // AccountingTransactionView entity so GetTransactionsAsync runs ONE SQL statement instead
            // of materializing all three sources and merging/filtering/sorting in memory.
            //
            // SECURITY: created WITH (security_invoker = true) so the base-table RLS policies
            // (tenant_isolation on Payments / Expenses / BankTransactions / BankConnections, keyed off
            // app.current_portfolio_id / app.is_admin) apply to the role *querying* the view
            // (rentalcommand_api), NOT the view owner. Without this the view would run with the
            // owner's privileges and bypass tenant isolation → cross-tenant leak.
            //
            // SOFT-DELETE: the entity HasQueryFilter predicates do NOT apply to a raw view, so they are
            // reproduced here: payments require a live Lease (Payment filter Lease.DeletedAt IS NULL);
            // expenses require their own DeletedAt IS NULL; bank rows require a live Portfolio. The
            // Property / Tenant / Vendor / WorkOrder labels come from LEFT JOINs that also require
            // DeletedAt IS NULL, so a soft-deleted parent nulls the label rather than dropping the row
            // (mirroring the EF projection's `x.Nav != null ? x.Nav.Name : null`).
            //
            // ENUMS: PaymentType / PaymentStatus / Expense.Category (ScheduleECategory) / Expense.Status
            // are stored as ints (HasConversion<int>). The CASE maps render the enum NAME so the grid
            // filters/sorts on a stable string. Values are coupled to the enum member order in
            // RentalCommand.Core.Enums.
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

            // The view is created by the owner; grant the API role read access explicitly (the base
            // RLS migration's GRANT SELECT ON ALL TABLES ran before this view existed).
            migrationBuilder.Sql(
                "GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP VIEW IF EXISTS vw_accounting_transactions;");
        }
    }
}
