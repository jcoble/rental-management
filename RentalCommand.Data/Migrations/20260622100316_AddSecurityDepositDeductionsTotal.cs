using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityDepositDeductionsTotal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeductionsTotal",
                table: "SecurityDepositHoldings",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("""
                UPDATE "SecurityDepositHoldings" h
                SET "DeductionsTotal" = COALESCE((
                    SELECT SUM(COALESCE((item->>'Amount')::numeric, 0))
                    FROM jsonb_array_elements(
                        CASE
                            WHEN jsonb_typeof(h."DeductionsJson") = 'array' THEN h."DeductionsJson"
                            ELSE '[]'::jsonb
                        END
                    ) AS item
                ), 0)
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeductionsTotal",
                table: "SecurityDepositHoldings");
        }
    }
}
