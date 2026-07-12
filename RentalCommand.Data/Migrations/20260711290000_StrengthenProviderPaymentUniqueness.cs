using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711290000_StrengthenProviderPaymentUniqueness")]
public sealed class StrengthenProviderPaymentUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PaymentTransactions_ProviderPaymentIntentId",
            table: "PaymentTransactions");

        migrationBuilder.CreateIndex(
            name: "IX_PaymentTransactions_Provider_ProviderPaymentIntentId",
            table: "PaymentTransactions",
            columns: new[] { "Provider", "ProviderPaymentIntentId" },
            unique: true,
            filter: "\"ProviderPaymentIntentId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PaymentTransactions_Provider_ProviderPaymentIntentId",
            table: "PaymentTransactions");

        migrationBuilder.CreateIndex(
            name: "IX_PaymentTransactions_ProviderPaymentIntentId",
            table: "PaymentTransactions",
            column: "ProviderPaymentIntentId",
            unique: true,
            filter: "\"ProviderPaymentIntentId\" IS NOT NULL");
    }
}
