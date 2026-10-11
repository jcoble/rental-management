using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class RegistrationTermsPrivacyConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TermsPrivacyAccepted",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsPrivacyAcceptedAtUtc",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsPrivacyVersion",
                table: "AspNetUsers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TermsPrivacyAccepted",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TermsPrivacyAcceptedAtUtc",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TermsPrivacyVersion",
                table: "AspNetUsers");
        }
    }
}
