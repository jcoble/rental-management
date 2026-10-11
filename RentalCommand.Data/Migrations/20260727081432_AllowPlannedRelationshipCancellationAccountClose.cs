using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowPlannedRelationshipCancellationAccountClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                TenantAccountPostgreSqlContract.TenantAccountCloseValidatorStatement(
                    allowPlannedCancellation: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                TenantAccountPostgreSqlContract.TenantAccountCloseValidatorStatement(
                    allowPlannedCancellation: false));
        }
    }
}
