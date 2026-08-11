using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerDistributionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "OwnerDistributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedBusinessDate",
                table: "OwnerDistributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedByUserId",
                table: "OwnerDistributions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankReference",
                table: "OwnerDistributions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExportReference",
                table: "OwnerDistributions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExportedAt",
                table: "OwnerDistributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "OwnerDistributions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedByUserId",
                table: "OwnerDistributions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "OwnerDistributions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "OwnerDistributions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "OwnerDistributions"
                SET "Status" = 1,
                    "ApprovedAt" = COALESCE("UpdatedAt", "CreatedAt"),
                    "ApprovedBusinessDate" = date_trunc('day', COALESCE("Date", "UpdatedAt", "CreatedAt"))
                WHERE "DeletedAt" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_ApprovedByUserId",
                table: "OwnerDistributions",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_PortfolioId_Status_Date",
                table: "OwnerDistributions",
                columns: new[] { "PortfolioId", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDistributions_RejectedByUserId",
                table: "OwnerDistributions",
                column: "RejectedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OwnerDistributions_AspNetUsers_ApprovedByUserId",
                table: "OwnerDistributions",
                column: "ApprovedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_OwnerDistributions_AspNetUsers_RejectedByUserId",
                table: "OwnerDistributions",
                column: "RejectedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OwnerDistributions_AspNetUsers_ApprovedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropForeignKey(
                name: "FK_OwnerDistributions_AspNetUsers_RejectedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropIndex(
                name: "IX_OwnerDistributions_ApprovedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropIndex(
                name: "IX_OwnerDistributions_PortfolioId_Status_Date",
                table: "OwnerDistributions");

            migrationBuilder.DropIndex(
                name: "IX_OwnerDistributions_RejectedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "ApprovedBusinessDate",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "BankReference",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "ExportReference",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "ExportedAt",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "OwnerDistributions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "OwnerDistributions");
        }
    }
}
