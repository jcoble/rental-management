using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260726191000_KeepRentChargesAlwaysEnabled")]
public partial class KeepRentChargesAlwaysEnabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "AutomationSettings"
            SET "EnableRentCharges" = TRUE
            WHERE "EnableRentCharges" = FALSE;

            ALTER TABLE "AutomationSettings"
                ALTER COLUMN "EnableRentCharges" SET DEFAULT TRUE;

            ALTER TABLE "AutomationSettings"
                ADD CONSTRAINT "CK_AutomationSettings_RentChargesAlwaysEnabled"
                CHECK ("EnableRentCharges" = TRUE);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "Rent charges are a core ledger invariant and cannot be made optional again.");
    }
}
