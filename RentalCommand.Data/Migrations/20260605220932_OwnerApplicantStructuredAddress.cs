using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnerApplicantStructuredAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentAddressLine1",
                table: "RentalApplications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentAddressLine2",
                table: "RentalApplications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentCity",
                table: "RentalApplications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentPostalCode",
                table: "RentalApplications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentState",
                table: "RentalApplications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                table: "OwnerEntities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                table: "OwnerEntities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "OwnerEntities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "OwnerEntities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "OwnerEntities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentAddressLine1",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "CurrentAddressLine2",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "CurrentCity",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "CurrentPostalCode",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "CurrentState",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                table: "OwnerEntities");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                table: "OwnerEntities");

            migrationBuilder.DropColumn(
                name: "City",
                table: "OwnerEntities");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "OwnerEntities");

            migrationBuilder.DropColumn(
                name: "State",
                table: "OwnerEntities");
        }
    }
}
