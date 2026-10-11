using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Authorization;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Forward-only destructive cutover from the duplicate Owner/direct-property columns to the
/// canonical effective-dated OwnerEntity/PropertyOwnership relationship.
/// </summary>
public partial class RemoveLegacyOwnerAndRouteAliases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // FoundationBaselinePostgreSql is mutable clean-install authority, so fresh databases
        // already have this table before ownership-dependent views are installed. Existing
        // databases reach this migration without it. The shared idempotent DDL supports both paths.
        migrationBuilder.Sql(FoundationBaselinePostgreSql.CreatePropertyOwnershipInfrastructureSql);

        migrationBuilder.Sql("""
            ALTER TABLE "PropertyOwnerships" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "PropertyOwnerships" FORCE ROW LEVEL SECURITY;
            DROP POLICY IF EXISTS tenant_isolation ON "PropertyOwnerships";
            DROP POLICY IF EXISTS tenant_select ON "PropertyOwnerships";
            DROP POLICY IF EXISTS tenant_insert ON "PropertyOwnerships";
            DROP POLICY IF EXISTS tenant_update ON "PropertyOwnerships";
            DROP POLICY IF EXISTS tenant_delete ON "PropertyOwnerships";
            CREATE POLICY tenant_select ON "PropertyOwnerships"
              FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_insert ON "PropertyOwnerships"
              FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_update ON "PropertyOwnerships"
              FOR UPDATE USING (rc_api_scope_allows("PortfolioId"))
              WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_delete ON "PropertyOwnerships"
              FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

            GRANT SELECT, INSERT, UPDATE, DELETE
              ON TABLE "PropertyOwnerships" TO rentalcommand_api;
            GRANT SELECT
              ON TABLE "PropertyOwnerships" TO rentalcommand_engine, rentalcommand_rls_authority;
            GRANT USAGE, SELECT
              ON SEQUENCE "PropertyOwnerships_Id_seq" TO rentalcommand_api;
            """);

        migrationBuilder.Sql(AccessEnvelopeViewSql.Drop);
        migrationBuilder.Sql(RelationshipAccessProjectionSql.Drop);

        migrationBuilder.DropForeignKey(
            name: "FK_Properties_OwnerEntities_OwnerEntityId",
            table: "Properties");
        migrationBuilder.DropForeignKey(
            name: "FK_Properties_Owners_OwnerId",
            table: "Properties");
        migrationBuilder.DropIndex(
            name: "IX_Properties_OwnerEntityId",
            table: "Properties");
        migrationBuilder.DropIndex(
            name: "IX_Properties_OwnerId",
            table: "Properties");
        migrationBuilder.DropColumn(
            name: "OwnerEntityId",
            table: "Properties");
        migrationBuilder.DropColumn(
            name: "OwnerId",
            table: "Properties");
        migrationBuilder.DropColumn(
            name: "Address",
            table: "OwnerEntities");
        migrationBuilder.DropTable(name: "Owners");

        migrationBuilder.Sql(RelationshipAccessProjectionSql.Create);
        migrationBuilder.Sql(AccessEnvelopeViewSql.Create);
        migrationBuilder.Sql("""
            GRANT SELECT ON TABLE
              "vw_effective_owner_access",
              "vw_effective_tenant_access",
              "vw_access_envelopes"
              TO rentalcommand_api;
            GRANT SELECT ON TABLE "vw_effective_tenant_access" TO rentalcommand_engine;
            GRANT SELECT ON TABLE
              "vw_effective_tenant_access",
              "vw_access_envelopes"
              TO rentalcommand_rls_authority;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "The legacy Owner/direct-property ownership cutover is destructive and forward-only.");
    }
}
