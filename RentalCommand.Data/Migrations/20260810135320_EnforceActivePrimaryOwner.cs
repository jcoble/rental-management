using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceActivePrimaryOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities",
                columns: new[] { "PortfolioId", "IsPrimary" },
                unique: true,
                filter: "\"IsPrimary\" AND \"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities",
                columns: new[] { "PortfolioId", "IsPrimary" });
        }
    }
}
