using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceLlmCredentialPurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkspaceLlmCredentials_PortfolioId",
                table: "WorkspaceLlmCredentials");

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "WorkspaceLlmCredentials",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "extraction");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceLlmCredentials_PortfolioId_Purpose",
                table: "WorkspaceLlmCredentials",
                columns: new[] { "PortfolioId", "Purpose" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkspaceLlmCredentials_PortfolioId_Purpose",
                table: "WorkspaceLlmCredentials");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "WorkspaceLlmCredentials");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceLlmCredentials_PortfolioId",
                table: "WorkspaceLlmCredentials",
                column: "PortfolioId",
                unique: true);
        }
    }
}
