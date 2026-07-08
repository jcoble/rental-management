using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCapitalAssets : Migration
    {
        private const string PortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapitalizedAssetId",
                table: "Expenses",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CapitalAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: true),
                    SourceExpenseId = table.Column<int>(type: "integer", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CostBasis = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    InServiceDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    RecoveryYears = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    Convention = table.Column<int>(type: "integer", nullable: false),
                    AccumulatedDepreciation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DisposedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapitalAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Expenses_SourceExpenseId",
                        column: x => x.SourceExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapitalAssets_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CapitalizedAssetId",
                table: "Expenses",
                column: "CapitalizedAssetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_Portfolio_Property_InServiceDate",
                table: "CapitalAssets",
                columns: new[] { "PortfolioId", "PropertyId", "InServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_PortfolioId",
                table: "CapitalAssets",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_PropertyId",
                table: "CapitalAssets",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_SourceExpenseId",
                table: "CapitalAssets",
                column: "SourceExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_CapitalAssets_UnitId",
                table: "CapitalAssets",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_CapitalAssets_CapitalizedAssetId",
                table: "Expenses",
                column: "CapitalizedAssetId",
                principalTable: "CapitalAssets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            EnableForce(migrationBuilder, "CapitalAssets");
            CreatePolicy(migrationBuilder, "CapitalAssets", PortfolioPredicate);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_CapitalAssets_CapitalizedAssetId",
                table: "Expenses");

            DropPolicy(migrationBuilder, "CapitalAssets");
            DisableRls(migrationBuilder, "CapitalAssets");

            migrationBuilder.DropTable(
                name: "CapitalAssets");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_CapitalizedAssetId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "CapitalizedAssetId",
                table: "Expenses");
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
