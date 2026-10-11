using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations;

public partial class AddWorkspaceLlmCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkspaceLlmCredentials",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                Provider = table.Column<string>(
                    type: "character varying(32)", maxLength: 32, nullable: false),
                ModelId = table.Column<string>(
                    type: "character varying(128)", maxLength: 128, nullable: false),
                ApiKeyCipherText = table.Column<string>(
                    type: "character varying(4000)", maxLength: 4000, nullable: false),
                LastTestedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: false),
                RotatedAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkspaceLlmCredentials", x => x.Id);
                table.ForeignKey(
                    name: "FK_WorkspaceLlmCredentials_Portfolios_PortfolioId",
                    column: x => x.PortfolioId,
                    principalTable: "Portfolios",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "LlmUsageEvidence",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy",
                        NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                Provider = table.Column<string>(
                    type: "character varying(32)", maxLength: 32, nullable: false),
                ModelId = table.Column<string>(
                    type: "character varying(128)", maxLength: 128, nullable: false),
                Feature = table.Column<string>(
                    type: "character varying(80)", maxLength: 80, nullable: false),
                LatencyMilliseconds = table.Column<int>(type: "integer", nullable: false),
                InputUnits = table.Column<int>(type: "integer", nullable: false),
                OutputUnits = table.Column<int>(type: "integer", nullable: false),
                EstimatedCostUsd = table.Column<decimal>(
                    type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                OccurredAtUtc = table.Column<DateTime>(
                    type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LlmUsageEvidence", x => x.Id);
                table.ForeignKey(
                    name: "FK_LlmUsageEvidence_Portfolios_PortfolioId",
                    column: x => x.PortfolioId,
                    principalTable: "Portfolios",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceLlmCredentials_PortfolioId",
            table: "WorkspaceLlmCredentials",
            column: "PortfolioId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LlmUsageEvidence_PortfolioId_OccurredAtUtc",
            table: "LlmUsageEvidence",
            columns: new[] { "PortfolioId", "OccurredAtUtc" });

        migrationBuilder.Sql("""
            ALTER TABLE "WorkspaceLlmCredentials" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "WorkspaceLlmCredentials" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_select ON "WorkspaceLlmCredentials"
              FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_insert ON "WorkspaceLlmCredentials"
              FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_update ON "WorkspaceLlmCredentials"
              FOR UPDATE USING (rc_api_scope_allows("PortfolioId"))
              WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_delete ON "WorkspaceLlmCredentials"
              FOR DELETE USING (rc_api_scope_allows("PortfolioId"));

            ALTER TABLE "LlmUsageEvidence" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "LlmUsageEvidence" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_select ON "LlmUsageEvidence"
              FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_insert ON "LlmUsageEvidence"
              FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
            CREATE POLICY tenant_delete ON "LlmUsageEvidence"
              FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

            GRANT SELECT, INSERT, UPDATE, DELETE
              ON TABLE "WorkspaceLlmCredentials" TO rentalcommand_api;
            GRANT SELECT
              ON TABLE "WorkspaceLlmCredentials" TO rentalcommand_engine;
            GRANT SELECT, INSERT, DELETE
              ON TABLE "LlmUsageEvidence" TO rentalcommand_api;
            GRANT SELECT, INSERT
              ON TABLE "LlmUsageEvidence" TO rentalcommand_engine;
            GRANT USAGE, SELECT
              ON SEQUENCE "WorkspaceLlmCredentials_Id_seq" TO rentalcommand_api;
            GRANT USAGE, SELECT
              ON SEQUENCE "LlmUsageEvidence_Id_seq" TO rentalcommand_api, rentalcommand_engine;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "LlmUsageEvidence");
        migrationBuilder.DropTable(name: "WorkspaceLlmCredentials");
    }
}
