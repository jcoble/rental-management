using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class PortalMessageLandlordChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PortalMessages_UserAccounts_UserAccountId",
                table: "PortalMessages");

            migrationBuilder.AlterColumn<int>(
                name: "UserAccountId",
                table: "PortalMessages",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "Channels",
                table: "PortalMessages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FromLandlord",
                table: "PortalMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecipientTenantId",
                table: "PortalMessages",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortalMessages_RecipientTenantId",
                table: "PortalMessages",
                column: "RecipientTenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_PortalMessages_Tenants_RecipientTenantId",
                table: "PortalMessages",
                column: "RecipientTenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PortalMessages_UserAccounts_UserAccountId",
                table: "PortalMessages",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PortalMessages_Tenants_RecipientTenantId",
                table: "PortalMessages");

            migrationBuilder.DropForeignKey(
                name: "FK_PortalMessages_UserAccounts_UserAccountId",
                table: "PortalMessages");

            migrationBuilder.DropIndex(
                name: "IX_PortalMessages_RecipientTenantId",
                table: "PortalMessages");

            migrationBuilder.DropColumn(
                name: "Channels",
                table: "PortalMessages");

            migrationBuilder.DropColumn(
                name: "FromLandlord",
                table: "PortalMessages");

            migrationBuilder.DropColumn(
                name: "RecipientTenantId",
                table: "PortalMessages");

            migrationBuilder.AlterColumn<int>(
                name: "UserAccountId",
                table: "PortalMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PortalMessages_UserAccounts_UserAccountId",
                table: "PortalMessages",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
