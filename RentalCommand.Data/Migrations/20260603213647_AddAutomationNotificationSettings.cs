using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableLateFees",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableLeaseExpiryReminders",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableRentCharges",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LateFeeGraceDays",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "LeaseExpiryReminderDays",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyTenants",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RentChargeLeadDays",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableLateFees",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "EnableLeaseExpiryReminders",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "EnableRentCharges",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "LateFeeGraceDays",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "LeaseExpiryReminderDays",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "NotifyTenants",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "RentChargeLeadDays",
                table: "NotificationSettings");
        }
    }
}
