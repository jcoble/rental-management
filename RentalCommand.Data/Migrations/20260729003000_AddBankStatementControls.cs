using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729003000_AddBankStatementControls")]
public partial class AddBankStatementControls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Fresh databases receive this table from the mutable clean-install foundation before its
        // grant/RLS catalog runs. Existing databases reach this migration without the table.
        migrationBuilder.Sql(FoundationBaselinePostgreSql.CreateBankStatementControlsInfrastructureSql);
        migrationBuilder.Sql(
            """
            ALTER TABLE "BankStatements" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "BankStatements" FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_select ON "BankStatements";
            DROP POLICY IF EXISTS tenant_insert ON "BankStatements";
            DROP POLICY IF EXISTS tenant_update ON "BankStatements";
            DROP POLICY IF EXISTS tenant_delete ON "BankStatements";
            CREATE POLICY tenant_select ON "BankStatements"
              FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_insert ON "BankStatements"
              FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_update ON "BankStatements"
              FOR UPDATE USING (rc_api_scope_allows("PortfolioId"))
              WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_delete ON "BankStatements"
              FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

            GRANT SELECT, INSERT, UPDATE, DELETE
              ON TABLE "BankStatements" TO rentalcommand_api;
            GRANT SELECT
              ON TABLE "BankStatements" TO rentalcommand_engine;
            GRANT USAGE, SELECT
              ON SEQUENCE "BankStatements_Id_seq" TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The mutable initial foundation owns clean uninstall ordering. This forward-only migration
        // deliberately leaves the durable statement table in place on a partial rollback.
    }
}
