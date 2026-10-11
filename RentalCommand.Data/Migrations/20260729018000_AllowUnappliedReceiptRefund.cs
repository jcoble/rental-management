using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729018000_AllowUnappliedReceiptRefund")]
public sealed class AllowUnappliedReceiptRefund : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            TenantAccountPostgreSqlContract.PaymentAttemptValidatorStatement(
                allowUnappliedReceiptRefund: true));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            TenantAccountPostgreSqlContract.PaymentAttemptValidatorStatement(
                allowUnappliedReceiptRefund: false));
    }
}
