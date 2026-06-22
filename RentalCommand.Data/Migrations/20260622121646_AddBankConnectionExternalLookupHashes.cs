using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankConnectionExternalLookupHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalAccountIdHash",
                table: "BankConnections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalItemIdHash",
                table: "BankConnections",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankConnections_PortfolioId_Provider_ExternalItemIdHash_Ext~",
                table: "BankConnections",
                columns: new[] { "PortfolioId", "Provider", "ExternalItemIdHash", "ExternalAccountIdHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankConnections_PortfolioId_Provider_ExternalItemIdHash_Ext~",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "ExternalAccountIdHash",
                table: "BankConnections");

            migrationBuilder.DropColumn(
                name: "ExternalItemIdHash",
                table: "BankConnections");
        }
    }
}
