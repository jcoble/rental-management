using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerDistributions : Migration
    {
        private const string PortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerDistributions",
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
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDistributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_OwnerEntities_OwnerEntityId",
                        column: x => x.OwnerEntityId,
                        principalTable: "OwnerEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OwnerDistributions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_OwnerEntityId",
                table: "OwnerDistributions",
                column: "OwnerEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_PortfolioId_OwnerEntityId_Date",
                table: "OwnerDistributions",
                columns: new[] { "PortfolioId", "OwnerEntityId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_PropertyId",
                table: "OwnerDistributions",
                column: "PropertyId");

            EnableForce(migrationBuilder, "OwnerDistributions");
            CreatePolicy(migrationBuilder, "OwnerDistributions", PortfolioPredicate);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropPolicy(migrationBuilder, "OwnerDistributions");
            DisableRls(migrationBuilder, "OwnerDistributions");

            migrationBuilder.DropTable(
                name: "OwnerDistributions");
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
