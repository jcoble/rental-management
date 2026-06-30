using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceLeaseEndAutoSendBoolsWithAction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSendMonthToMonth",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "AutoSendMoveOut",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "AutoSendRenewal",
                table: "NotificationSettings");

            migrationBuilder.AddColumn<string>(
                name: "LeaseEndAutoAction",
                table: "NotificationSettings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Draft");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseEndAutoAction",
                table: "NotificationSettings");

            migrationBuilder.AddColumn<bool>(
                name: "AutoSendMonthToMonth",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoSendMoveOut",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoSendRenewal",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
