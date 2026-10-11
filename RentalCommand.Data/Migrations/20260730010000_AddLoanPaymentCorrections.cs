using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoanPaymentCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoanPaymentCorrections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LoanPaymentId = table.Column<int>(type: "integer", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceScanDraftId = table.Column<int>(type: "integer", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InterestAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PrincipalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EscrowAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PaymentDoesNotCoverInterest = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanPaymentCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanPaymentCorrections_LoanPayments_LoanPaymentId",
                        column: x => x.LoanPaymentId,
                        principalTable: "LoanPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoanPaymentCorrections_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoanPaymentCorrections_LoanPaymentId_AttemptId",
                table: "LoanPaymentCorrections",
                columns: new[] { "LoanPaymentId", "AttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanPaymentCorrections_LoanPaymentId_Id",
                table: "LoanPaymentCorrections",
                columns: new[] { "LoanPaymentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LoanPaymentCorrections_PortfolioId",
                table: "LoanPaymentCorrections",
                column: "PortfolioId");

            migrationBuilder.Sql("""
                ALTER TABLE "LoanPaymentCorrections" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "LoanPaymentCorrections" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_select ON "LoanPaymentCorrections"
                  FOR SELECT USING (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_insert ON "LoanPaymentCorrections"
                  FOR INSERT WITH CHECK (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_update ON "LoanPaymentCorrections"
                  FOR UPDATE USING (rc_api_scope_allows("PortfolioId"))
                  WITH CHECK (rc_api_scope_allows("PortfolioId"));
                CREATE POLICY tenant_delete ON "LoanPaymentCorrections"
                  FOR DELETE USING (rc_api_scope_allows("PortfolioId"));

                GRANT SELECT, INSERT ON TABLE "LoanPaymentCorrections" TO rentalcommand_api;
                GRANT USAGE, SELECT ON SEQUENCE "LoanPaymentCorrections_Id_seq" TO rentalcommand_api;
                GRANT SELECT ON TABLE "LoanPaymentCorrections" TO rentalcommand_engine;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanPaymentCorrections");
        }
    }
}
