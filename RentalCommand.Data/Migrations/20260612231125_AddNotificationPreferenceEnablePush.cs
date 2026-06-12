using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPreferenceEnablePush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnablePush",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                // Push defaults ON for in-app-style events (the phone-first interrupt channel);
                // the send itself is a no-op until a push credential is configured. Backfills
                // existing stored rows to ON so the matrix matches the entity/default semantics.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnablePush",
                table: "NotificationPreferences");
        }
    }
}
