using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerContributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerContributions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    OwnerEntityId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBusinessDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectedByUserId = table.Column<int>(type: "integer", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BankReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExportReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerContributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerContributions_AspNetUsers_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OwnerContributions_AspNetUsers_RejectedByUserId",
                        column: x => x.RejectedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OwnerContributions_OwnerEntities_OwnerEntityId",
                        column: x => x.OwnerEntityId,
                        principalTable: "OwnerEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerContributions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerContributions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_ApprovedByUserId",
                table: "OwnerContributions",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_OwnerEntityId",
                table: "OwnerContributions",
                column: "OwnerEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_PortfolioId_OwnerEntityId_Date",
                table: "OwnerContributions",
                columns: new[] { "PortfolioId", "OwnerEntityId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_PortfolioId_Status_Date",
                table: "OwnerContributions",
                columns: new[] { "PortfolioId", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_PropertyId",
                table: "OwnerContributions",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerContributions_RejectedByUserId",
                table: "OwnerContributions",
                column: "RejectedByUserId");

            migrationBuilder.Sql("""
                ALTER TABLE "OwnerContributions" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "OwnerContributions" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_select ON "OwnerContributions"
                  FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_insert ON "OwnerContributions"
                  FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_update ON "OwnerContributions"
                  FOR UPDATE USING (rc_api_scope_allows("PortfolioId"))
                  WITH CHECK (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_delete ON "OwnerContributions"
                  FOR DELETE USING (rc_sandbox_graduation_allows("PortfolioId"));

                GRANT SELECT, INSERT, UPDATE, DELETE
                  ON TABLE "OwnerContributions" TO rentalcommand_api;
                GRANT SELECT
                  ON TABLE "OwnerContributions" TO rentalcommand_engine;
                GRANT USAGE, SELECT
                  ON SEQUENCE "OwnerContributions_Id_seq" TO rentalcommand_api;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerContributions");
        }
    }
}
