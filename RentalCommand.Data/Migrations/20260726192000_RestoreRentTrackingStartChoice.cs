using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260726192000_RestoreRentTrackingStartChoice")]
public partial class RestoreRentTrackingStartChoice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "RentTrackingStartOn",
            table: "TenantAccounts",
            type: "date",
            nullable: true);

        // Existing canonical accounts were created while the user choice was absent. Protect them
        // from a surprise historical sweep; explicit backfill remains represented by null for new
        // accounts whose reviewer chooses it.
        migrationBuilder.Sql("""
            UPDATE "TenantAccounts" AS account
            SET "RentTrackingStartOn" = GREATEST(
                agreement."TermStartOn",
                rc_business_date(account."PortfolioId"))
            FROM "LeaseAgreements" AS agreement
            WHERE agreement."LeaseManagementId" = account."LeaseManagementId"
              AND agreement."PortfolioId" = account."PortfolioId"
              AND agreement."VersionNumber" = 1;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RentTrackingStartOn",
            table: "TenantAccounts");
    }
}
