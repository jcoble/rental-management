using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationFeeToPayment : Migration
    {
        // gap5 (application/screening-fee income): add Payment.ApplicationId + PropertyId (both nullable,
        // SetNull FKs) for lease-less income, and recreate vw_accounting_transactions to (1) map the new
        // PaymentType 5 -> 'ApplicationFee' in the CASE maps and (2) COALESCE the payment's own PropertyId
        // when there is no lease, so an application fee shows on its property in the ledger. Otherwise the
        // view is identical to AddNullableLeaseToPaymentAndView (LEFT JOIN + the lease-less WHERE guard).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApplicationId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PropertyId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ApplicationId",
                table: "Payments",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PropertyId",
                table: "Payments",
                column: "PropertyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Properties_PropertyId",
                table: "Payments",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_RentalApplications_ApplicationId",
                table: "Payments",
                column: "ApplicationId",
                principalTable: "RentalApplications",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(@"DROP VIEW IF EXISTS vw_accounting_transactions;");

            migrationBuilder.Sql(@"
CREATE VIEW vw_accounting_transactions WITH (security_invoker = true) AS
    -- Payments (lease optional; a lease-less application fee is retained and attributed to its own PropertyId)
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
                    WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' WHEN 5 THEN 'ApplicationFee'
                    ELSE p.""PaymentType""::text
                END
                || ' - ' || COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')
        END                                                AS ""Description"",
        CASE p.""PaymentType""
            WHEN 0 THEN 'Rent' WHEN 1 THEN 'SecurityDeposit' WHEN 2 THEN 'LateFee'
            WHEN 3 THEN 'Utility' WHEN 4 THEN 'Other' WHEN 5 THEN 'ApplicationFee'
            ELSE p.""PaymentType""::text
        END                                                AS ""Category"",
        CASE p.""Status""
            WHEN 0 THEN 'Scheduled' WHEN 1 THEN 'Paid' WHEN 2 THEN 'Partial' WHEN 3 THEN 'Late'
            WHEN 4 THEN 'Waived' WHEN 5 THEN 'Failed' WHEN 6 THEN 'Refunded' ELSE p.""Status""::text
        END                                                AS ""Status"",
        p.""Amount""                                       AS ""Amount"",
        COALESCE(l.""PropertyId"", p.""PropertyId"")       AS ""PropertyId"",
        prop.""Name""                                      AS ""PropertyName"",
        (COALESCE(ten.""FirstName"", '') || ' ' || COALESCE(ten.""LastName"", '')) AS ""Counterparty"",
        (COALESCE(l.""LeaseNumber"", '') || ' ' || COALESCE(p.""Method"", '') || ' ' || COALESCE(p.""ExternalReference"", '')) AS ""Reference"",
        p.""Notes""                                        AS ""Notes"",
        l.""UnitId""                                       AS ""UnitId""
    FROM ""Payments"" p
    LEFT JOIN ""Leases"" l   ON l.""Id"" = p.""LeaseId"" AND l.""DeletedAt"" IS NULL
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = COALESCE(l.""PropertyId"", p.""PropertyId"") AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Tenants"" ten     ON ten.""Id"" = l.""TenantId""   AND ten.""DeletedAt"" IS NULL
    WHERE p.""LeaseId"" IS NULL OR l.""Id"" IS NOT NULL

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

            migrationBuilder.Sql(
                "GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recreate the pre-gap5 view FIRST (identical to AddNullableLeaseToPaymentAndView.Up): no
            // ApplicationFee label and no reference to p."PropertyId" — required before dropping that column.
            migrationBuilder.Sql(@"DROP VIEW IF EXISTS vw_accounting_transactions;");

            migrationBuilder.Sql(@"
CREATE VIEW vw_accounting_transactions WITH (security_invoker = true) AS
    -- Payments (lease optional; a lease-less row, e.g. an application fee, is retained via LEFT JOIN)
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
    LEFT JOIN ""Leases"" l   ON l.""Id"" = p.""LeaseId"" AND l.""DeletedAt"" IS NULL
    LEFT JOIN ""Properties"" prop ON prop.""Id"" = l.""PropertyId"" AND prop.""DeletedAt"" IS NULL
    LEFT JOIN ""Tenants"" ten     ON ten.""Id"" = l.""TenantId""   AND ten.""DeletedAt"" IS NULL
    WHERE p.""LeaseId"" IS NULL OR l.""Id"" IS NOT NULL

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

            migrationBuilder.Sql(
                "GRANT SELECT ON vw_accounting_transactions TO rentalcommand_api;");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Properties_PropertyId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_RentalApplications_ApplicationId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_ApplicationId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_PropertyId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ApplicationId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PropertyId",
                table: "Payments");
        }
    }
}
