using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerEntityIsPrimary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary",
                table: "OwnerEntities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities",
                columns: new[] { "PortfolioId", "IsPrimary" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OwnerEntities_PortfolioId_IsPrimary",
                table: "OwnerEntities");

            migrationBuilder.DropColumn(
                name: "IsPrimary",
                table: "OwnerEntities");
        }
    }
}
