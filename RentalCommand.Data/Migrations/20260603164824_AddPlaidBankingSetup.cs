using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaidBankingSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAccessTokenCipherText",
                table: "BankConnections",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SyncCursorCipherText",
                table: "BankConnections",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalAccessTokenCipherText",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "SyncCursorCipherText",
                table: "BankConnections");
        }
    }
}
