using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UnitListings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: false),
                    UnitId = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Headline = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Rent = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SecurityDeposit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Bedrooms = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    Bathrooms = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SquareFeet = table.Column<int>(type: "integer", nullable: true),
                    AvailableOn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseTerms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PetPolicy = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Utilities = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Parking = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Amenities = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PhotoNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ZillowListingUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ZillowApplicationUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PostedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UnitListings_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UnitListings_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UnitListings_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnitListings_PortfolioId",
                table: "UnitListings",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitListings_PortfolioId_UnitId_Channel",
                table: "UnitListings",
                columns: new[] { "PortfolioId", "UnitId", "Channel" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UnitListings_PropertyId",
                table: "UnitListings",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_UnitListings_UnitId",
                table: "UnitListings",
                column: "UnitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UnitListings");
        }
    }
}
