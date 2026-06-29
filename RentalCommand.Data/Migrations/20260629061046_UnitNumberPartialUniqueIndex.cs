using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnitNumberPartialUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Units_PropertyId_UnitNumber",
                table: "Units");

            migrationBuilder.CreateIndex(
                name: "IX_Units_PropertyId_UnitNumber",
                table: "Units",
                columns: new[] { "PropertyId", "UnitNumber" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Units_PropertyId_UnitNumber",
                table: "Units");

            migrationBuilder.CreateIndex(
                name: "IX_Units_PropertyId_UnitNumber",
                table: "Units",
                columns: new[] { "PropertyId", "UnitNumber" },
                unique: true);
        }
    }
}
