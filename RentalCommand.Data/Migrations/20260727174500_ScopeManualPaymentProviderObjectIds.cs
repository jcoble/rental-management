using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260727174500_ScopeManualPaymentProviderObjectIds")]
    public partial class ScopeManualPaymentProviderObjectIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantPaymentAttempts_Provider_ProviderObjectId",
                table: "TenantPaymentAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_Provider_ProviderObjectId",
                table: "TenantPaymentAttempts",
                columns: new[] { "Provider", "ProviderObjectId" },
                unique: true,
                filter: "\"ProviderObjectId\" IS NOT NULL AND \"Provider\" <> 'manual'");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_ManualProviderObject",
                table: "TenantPaymentAttempts",
                columns: new[] { "Provider", "TenantAccountId", "ProviderObjectId" },
                unique: true,
                filter: "\"Provider\" = 'manual' AND \"ProviderObjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantPaymentAttempts_ManualProviderObject",
                table: "TenantPaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TenantPaymentAttempts_Provider_ProviderObjectId",
                table: "TenantPaymentAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPaymentAttempts_Provider_ProviderObjectId",
                table: "TenantPaymentAttempts",
                columns: new[] { "Provider", "ProviderObjectId" },
                unique: true,
                filter: "\"ProviderObjectId\" IS NOT NULL");
        }
    }
}
