using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729004500_ScopeDeviceTokenUniquenessToPortfolio")]
public partial class ScopeDeviceTokenUniquenessToPortfolio : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DeviceTokens_Token",
            table: "DeviceTokens");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_PortfolioId_Token",
            table: "DeviceTokens",
            columns: new[] { "PortfolioId", "Token" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_DeviceTokens_PortfolioId_Token",
            table: "DeviceTokens");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceTokens_Token",
            table: "DeviceTokens",
            column: "Token",
            unique: true);
    }
}
