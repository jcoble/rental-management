using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseTenantMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeaseTenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeaseTenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeaseTenants_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseTenants_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LeaseTenants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO "LeaseTenants" ("PortfolioId", "LeaseId", "TenantId", "IsPrimary", "CreatedAt", "UpdatedAt")
                SELECT "PortfolioId", "Id", "TenantId", TRUE, "CreatedAt", "UpdatedAt"
                FROM "Leases"
                """);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseTenants_LeaseId",
                table: "LeaseTenants",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseTenants_LeaseId_TenantId",
                table: "LeaseTenants",
                columns: new[] { "LeaseId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeaseTenants_PortfolioId",
                table: "LeaseTenants",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_LeaseTenants_TenantId",
                table: "LeaseTenants",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeaseTenants");
        }
    }
}
