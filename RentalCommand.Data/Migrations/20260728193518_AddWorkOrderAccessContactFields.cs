using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderAccessContactFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessWarnings",
                table: "WorkOrders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CallBeforeEntry",
                table: "WorkOrders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CallIfNotHome",
                table: "WorkOrders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryNotes",
                table: "WorkOrders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PermissionToEnter",
                table: "WorkOrders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PetWarnings",
                table: "WorkOrders",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterEmail",
                table: "WorkOrders",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterName",
                table: "WorkOrders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterPhone",
                table: "WorkOrders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResidentMustBePresent",
                table: "WorkOrders",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedByLabel",
                table: "WorkOrders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessWarnings",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "CallBeforeEntry",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "CallIfNotHome",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "EntryNotes",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PermissionToEnter",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "PetWarnings",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "RequesterEmail",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "RequesterName",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "RequesterPhone",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "ResidentMustBePresent",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "SubmittedByLabel",
                table: "WorkOrders");
        }
    }
}
