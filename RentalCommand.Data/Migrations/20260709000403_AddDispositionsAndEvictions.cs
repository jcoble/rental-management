using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDispositionsAndEvictions : Migration
    {
        private const string PortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EvictionCases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FiledOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HearingDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CourtName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CaseNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Resolution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvictionCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvictionCases_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PropertyDispositions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    ClosedOnDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SellingCosts = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BuyerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropertyDispositions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropertyDispositions_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropertyDispositions_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvictionCaseEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    EvictionCaseId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvictionCaseEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvictionCaseEvents_EvictionCases_EvictionCaseId",
                        column: x => x.EvictionCaseId,
                        principalTable: "EvictionCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EvictionCaseEvents_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseEvents_EvictionCaseId_EventDate",
                table: "EvictionCaseEvents",
                columns: new[] { "EvictionCaseId", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCaseEvents_PortfolioId",
                table: "EvictionCaseEvents",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_LeaseId",
                table: "EvictionCases",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_Portfolio_Lease_Status",
                table: "EvictionCases",
                columns: new[] { "PortfolioId", "LeaseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_Portfolio_Property_Status",
                table: "EvictionCases",
                columns: new[] { "PortfolioId", "PropertyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_Portfolio_Tenant_Status",
                table: "EvictionCases",
                columns: new[] { "PortfolioId", "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_PortfolioId",
                table: "EvictionCases",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_PropertyId",
                table: "EvictionCases",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_TenantId",
                table: "EvictionCases",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EvictionCases_UnitId",
                table: "EvictionCases",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_ClosedOnDate",
                table: "PropertyDispositions",
                column: "ClosedOnDate");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_Portfolio_Property_Active",
                table: "PropertyDispositions",
                columns: new[] { "PortfolioId", "PropertyId" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_PortfolioId",
                table: "PropertyDispositions",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyDispositions_PropertyId",
                table: "PropertyDispositions",
                column: "PropertyId");

            EnableForce(migrationBuilder, "EvictionCases");
            CreatePolicy(migrationBuilder, "EvictionCases", PortfolioPredicate);
            EnableForce(migrationBuilder, "EvictionCaseEvents");
            CreatePolicy(migrationBuilder, "EvictionCaseEvents", PortfolioPredicate);
            EnableForce(migrationBuilder, "PropertyDispositions");
            CreatePolicy(migrationBuilder, "PropertyDispositions", PortfolioPredicate);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropPolicy(migrationBuilder, "EvictionCaseEvents");
            DisableRls(migrationBuilder, "EvictionCaseEvents");
            DropPolicy(migrationBuilder, "PropertyDispositions");
            DisableRls(migrationBuilder, "PropertyDispositions");
            DropPolicy(migrationBuilder, "EvictionCases");
            DisableRls(migrationBuilder, "EvictionCases");

            migrationBuilder.DropTable(
                name: "EvictionCaseEvents");

            migrationBuilder.DropTable(
                name: "PropertyDispositions");

            migrationBuilder.DropTable(
                name: "EvictionCases");
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
