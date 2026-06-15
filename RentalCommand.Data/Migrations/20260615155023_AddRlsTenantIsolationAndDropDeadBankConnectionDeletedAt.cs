using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <summary>
    /// Adds PostgreSQL Row-Level Security (RLS) as a defense-in-depth tenant-isolation backstop behind
    /// the application-layer <c>.Where(x =&gt; x.PortfolioId == portfolioId)</c> filters (audit M-1),
    /// modeled on EdiPlatform's per-CustomerId RLS. Also drops the dead <c>BankConnection.DeletedAt</c>
    /// column (audit M-8).
    ///
    /// <para>Design (identical contract to EdiPlatform):
    /// <list type="bullet">
    ///   <item>A dedicated non-superuser login role <c>rentalcommand_api</c> that the API/Engine
    ///   connect as at runtime. PostgreSQL exempts SUPERUSER/BYPASSRLS roles even from FORCEd RLS, so
    ///   the app MUST connect as this role for the policies to bite (see deploy compose connection
    ///   strings). Migrations keep running as the table owner/superuser.</item>
    ///   <item>Every portfolio-scoped table gets <c>ENABLE</c> + <c>FORCE ROW LEVEL SECURITY</c> and a
    ///   PUBLIC <c>tenant_isolation</c> policy (USING + WITH CHECK) keyed on two session GUCs the
    ///   connection interceptors set per request: <c>app.current_portfolio_id</c> (int) and
    ///   <c>app.is_admin</c> (bool). The admin branch (background workers, platform admins,
    ///   unauthenticated paths) bypasses the portfolio predicate.</item>
    ///   <item><c>current_setting('app.current_portfolio_id', true)</c> uses the <c>missing_ok</c>
    ///   form so an unset GUC returns NULL (which equals no row) rather than erroring — fail-closed.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddRlsTenantIsolationAndDropDeadBankConnectionDeletedAt : Migration
    {
        // Tables with a non-nullable PortfolioId column → direct policy keyed on "PortfolioId".
        private static readonly string[] DirectPortfolioTables =
        {
            "AdverseActionNotices", "Appointments", "AuditLogs", "AutopayEnrollments",
            "BankConnections", "BankTransactions", "Conversations", "DeviceTokens", "Expenses",
            "Inspections", "InspectionItems", "Leases", "NoticeDrafts", "Notifications",
            "NotificationPreferences", "NotificationSettings", "OpeningBalances", "Owners",
            "OwnerEntities", "Payments", "PaymentTransactions", "PortalMessages", "Properties",
            "QueuedJobs", "RecurringMaintenanceTasks", "RentalApplications", "ScanBatches",
            "ScanDrafts", "ScreeningResults", "SecurityDepositHoldings", "SignatureRequests",
            "StoredFiles", "Tenants", "UserAccounts", "Vendors", "VendorDispatches", "VendorRatings",
            "WorkOrders", "WorkOrderStatusEvents",
        };

        // Tables with a nullable PortfolioId where NULL means a shared / built-in row that every
        // portfolio may read (custom inspection templates carry a portfolio id; built-ins are NULL).
        private static readonly string[] NullablePortfolioTables =
        {
            "InspectionTemplates",
        };

        // Child tables with no PortfolioId column — scoped transitively via an EXISTS subquery on the
        // parent (the parent is itself RLS-protected, so within the rentalcommand_api role the subquery
        // only sees the current portfolio's parents). (child table, parent table, FK column).
        private static readonly (string Child, string Parent, string Fk)[] ChildTables =
        {
            ("Units", "Properties", "PropertyId"),
            ("ConversationMessages", "Conversations", "ConversationId"),
            ("ExpenseLineItems", "Expenses", "ExpenseId"),
            ("InspectionTemplateItems", "InspectionTemplates", "TemplateId"),
            ("SignatureSigners", "SignatureRequests", "SignatureRequestId"),
            ("SignatureAuditEvents", "SignatureRequests", "SignatureRequestId"),
        };

        // ----------------------------------------------------------------------------------------
        // Intentionally NOT RLS-scoped:
        //   Portfolios          → the scope root; gets a self-policy keyed on "Id" (below).
        //   AspNet* (Identity)  → auth tables, app-layer controlled (mirrors EdiPlatform).
        //   RefreshTokens       → auth; looked up by hash, frequently without a portfolio context.
        //   OutboxMessages      → system/queue infra, drained by the Engine (admin); nullable scope.
        //   EngineWorkerHeartbeats, StripeWebhookEvents → global infra, no PortfolioId.
        // ----------------------------------------------------------------------------------------

        private const string PortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        private const string NullablePortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR \"PortfolioId\" IS NULL " +
            "OR current_setting('app.is_admin', true) = 'true')";

        private const string PortfolioSelfPredicate =
            "(\"Id\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Audit M-8: drop the dead BankConnection.DeletedAt column (never written, no filter).
            migrationBuilder.DropColumn(name: "DeletedAt", table: "BankConnections");

            // --- Phase 1: dedicated non-superuser API login role (idempotent). ---
            // The dev password is a placeholder; deploy provisioning rotates it (see deploy compose).
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'rentalcommand_api') THEN
                        CREATE ROLE rentalcommand_api LOGIN PASSWORD 'rentalcommand_api_dev';
                    END IF;
                END
                $$;
            ");

            // --- Phase 2: grant the API role DML on the public schema (no DDL). ---
            migrationBuilder.Sql(@"
                DO $$ BEGIN EXECUTE format('GRANT CONNECT ON DATABASE %I TO rentalcommand_api', current_database()); END $$;
                GRANT USAGE ON SCHEMA public TO rentalcommand_api;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO rentalcommand_api;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO rentalcommand_api;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO rentalcommand_api;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO rentalcommand_api;
            ");

            // --- Phase 3: enable + force RLS and create the tenant_isolation policy per table. ---
            foreach (var table in DirectPortfolioTables)
            {
                EnableForce(migrationBuilder, table);
                CreatePolicy(migrationBuilder, table, PortfolioPredicate);
            }

            foreach (var table in NullablePortfolioTables)
            {
                EnableForce(migrationBuilder, table);
                CreatePolicy(migrationBuilder, table, NullablePortfolioPredicate);
            }

            // Portfolios: the scope root, keyed on its own Id.
            EnableForce(migrationBuilder, "Portfolios");
            CreatePolicy(migrationBuilder, "Portfolios", PortfolioSelfPredicate);

            // Child tables: visible iff the parent row is visible to the current portfolio (or admin).
            foreach (var (child, parent, fk) in ChildTables)
            {
                EnableForce(migrationBuilder, child);
                var predicate =
                    "(current_setting('app.is_admin', true) = 'true' " +
                    $"OR EXISTS (SELECT 1 FROM \"{parent}\" p " +
                    $"WHERE p.\"Id\" = \"{child}\".\"{fk}\"))";
                CreatePolicy(migrationBuilder, child, predicate);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (child, _, _) in ChildTables)
            {
                DropPolicy(migrationBuilder, child);
                DisableRls(migrationBuilder, child);
            }

            DropPolicy(migrationBuilder, "Portfolios");
            DisableRls(migrationBuilder, "Portfolios");

            foreach (var table in NullablePortfolioTables.Concat(DirectPortfolioTables))
            {
                DropPolicy(migrationBuilder, table);
                DisableRls(migrationBuilder, table);
            }

            // Revoke the API role's grants and drop it. Guarded with a DO block so a re-run never errors.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'rentalcommand_api') THEN
                        EXECUTE 'REVOKE ALL ON ALL TABLES IN SCHEMA public FROM rentalcommand_api';
                        EXECUTE 'REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM rentalcommand_api';
                        EXECUTE 'REVOKE ALL ON SCHEMA public FROM rentalcommand_api';
                        EXECUTE format('REVOKE CONNECT ON DATABASE %I FROM rentalcommand_api', current_database());
                        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM rentalcommand_api';
                        EXECUTE 'ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM rentalcommand_api';
                        DROP ROLE rentalcommand_api;
                    END IF;
                END
                $$;
            ");

            // Restore the dead column the Up dropped (audit M-8) so Down is a faithful inverse.
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "BankConnections",
                type: "timestamp with time zone",
                nullable: true);
        }

        private static void EnableForce(MigrationBuilder mb, string table)
        {
            mb.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
            mb.Sql($"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
        }

        private static void DisableRls(MigrationBuilder mb, string table)
        {
            mb.Sql($"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY;");
            mb.Sql($"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY;");
        }

        // DROP + CREATE so a re-applied migration on a partially-migrated DB is idempotent.
        private static void CreatePolicy(MigrationBuilder mb, string table, string predicate)
        {
            mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON \"{table}\";");
            mb.Sql(
                $"CREATE POLICY tenant_isolation ON \"{table}\" " +
                $"USING {predicate} WITH CHECK {predicate};");
        }

        private static void DropPolicy(MigrationBuilder mb, string table)
            => mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON \"{table}\";");
    }
}
