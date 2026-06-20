using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenseUnitId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnitId",
                table: "Expenses",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_UnitId",
                table: "Expenses",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_Expenses_Units_UnitId",
                table: "Expenses",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Expenses_Units_UnitId",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_UnitId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "Expenses");
        }
    }
}
