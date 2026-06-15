using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableApplicationForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomFieldAnswersJson",
                table: "RentalApplications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncomeSourcesJson",
                table: "RentalApplications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PetsJson",
                table: "RentalApplications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApplicationFormConfig",
                table: "Portfolios",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomFieldAnswersJson",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "IncomeSourcesJson",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "PetsJson",
                table: "RentalApplications");

            migrationBuilder.DropColumn(
                name: "ApplicationFormConfig",
                table: "Portfolios");
        }
    }
}
