using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711230000_AddVendorNormalizedPhone")]
public partial class AddVendorNormalizedPhone : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "NormalizedPhone",
            table: "Vendors",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Vendors_NormalizedPhone",
            table: "Vendors",
            column: "NormalizedPhone");

        migrationBuilder.CreateIndex(
            name: "IX_VendorDispatches_VendorId_Status_DispatchedAtUtc",
            table: "VendorDispatches",
            columns: new[] { "VendorId", "Status", "DispatchedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_VendorDispatches_VendorId_Status_DispatchedAtUtc",
            table: "VendorDispatches");

        migrationBuilder.DropIndex(
            name: "IX_Vendors_NormalizedPhone",
            table: "Vendors");

        migrationBuilder.DropColumn(
            name: "NormalizedPhone",
            table: "Vendors");
    }
}
