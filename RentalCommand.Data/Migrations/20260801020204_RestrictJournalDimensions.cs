using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictJournalDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_OwnerEntities_OwnerEntityId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_Properties_PropertyId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_TenantAccounts_TenantAccountId",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_Units_UnitId",
                table: "JournalLines");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_OwnerEntities_RestrictHistory",
                table: "JournalLines",
                column: "OwnerEntityId",
                principalTable: "OwnerEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_Properties_RestrictHistory",
                table: "JournalLines",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_TenantAccounts_RestrictHistory",
                table: "JournalLines",
                column: "TenantAccountId",
                principalTable: "TenantAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_Units_RestrictHistory",
                table: "JournalLines",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_OwnerEntities_RestrictHistory",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_Properties_RestrictHistory",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_TenantAccounts_RestrictHistory",
                table: "JournalLines");

            migrationBuilder.DropForeignKey(
                name: "FK_JournalLines_Units_RestrictHistory",
                table: "JournalLines");

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_OwnerEntities_OwnerEntityId",
                table: "JournalLines",
                column: "OwnerEntityId",
                principalTable: "OwnerEntities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_Properties_PropertyId",
                table: "JournalLines",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_TenantAccounts_TenantAccountId",
                table: "JournalLines",
                column: "TenantAccountId",
                principalTable: "TenantAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_JournalLines_Units_UnitId",
                table: "JournalLines",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
