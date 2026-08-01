using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCreditTargetAndServicePeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RelatedTenantLedgerEntryId",
                table: "TenantLedgerEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodEndOn",
                table: "TenantLedgerEntries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServicePeriodStartOn",
                table: "TenantLedgerEntries",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_TenantAccountId_RelatedTena~",
                table: "TenantLedgerEntries",
                columns: new[] { "PortfolioId", "TenantAccountId", "RelatedTenantLedgerEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLedgerEntries_RelatedTenantLedgerEntryId_TenantAccoun~",
                table: "TenantLedgerEntries",
                columns: new[] { "RelatedTenantLedgerEntryId", "TenantAccountId", "PortfolioId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantLedgerEntry_ServicePeriod",
                table: "TenantLedgerEntries",
                sql: "(\"ServicePeriodStartOn\" IS NULL OR \"ServicePeriodEndOn\" IS NULL OR \"ServicePeriodEndOn\" >= \"ServicePeriodStartOn\")");

            migrationBuilder.AddForeignKey(
                name: "FK_TenantLedgerEntries_TenantLedgerEntries_RelatedTenantLedger~",
                table: "TenantLedgerEntries",
                columns: new[] { "RelatedTenantLedgerEntryId", "TenantAccountId", "PortfolioId" },
                principalTable: "TenantLedgerEntries",
                principalColumns: new[] { "Id", "TenantAccountId", "PortfolioId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TenantLedgerEntries_TenantLedgerEntries_RelatedTenantLedger~",
                table: "TenantLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_TenantLedgerEntries_PortfolioId_TenantAccountId_RelatedTena~",
                table: "TenantLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_TenantLedgerEntries_RelatedTenantLedgerEntryId_TenantAccoun~",
                table: "TenantLedgerEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantLedgerEntry_ServicePeriod",
                table: "TenantLedgerEntries");

            migrationBuilder.DropColumn(
                name: "RelatedTenantLedgerEntryId",
                table: "TenantLedgerEntries");

            migrationBuilder.DropColumn(
                name: "ServicePeriodEndOn",
                table: "TenantLedgerEntries");

            migrationBuilder.DropColumn(
                name: "ServicePeriodStartOn",
                table: "TenantLedgerEntries");
        }
    }
}
