using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggableSmsProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmsCredentialACipherText",
                table: "NotificationSettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsCredentialBCipherText",
                table: "NotificationSettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsCredentialCCipherText",
                table: "NotificationSettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsFromNumberCipherText",
                table: "NotificationSettings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsProvider",
                table: "NotificationSettings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmsCredentialACipherText",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SmsCredentialBCipherText",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SmsCredentialCCipherText",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SmsFromNumberCipherText",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SmsProvider",
                table: "NotificationSettings");
        }
    }
}
