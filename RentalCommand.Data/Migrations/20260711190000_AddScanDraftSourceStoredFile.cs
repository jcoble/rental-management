using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711190000_AddScanDraftSourceStoredFile")]
public partial class AddScanDraftSourceStoredFile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ConfirmedEntityId",
            table: "ScanDrafts",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SourceStoredFileId",
            table: "ScanDrafts",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ScanDrafts_SourceStoredFileId",
            table: "ScanDrafts",
            column: "SourceStoredFileId");

        migrationBuilder.AddForeignKey(
            name: "FK_ScanDrafts_StoredFiles_SourceStoredFileId",
            table: "ScanDrafts",
            column: "SourceStoredFileId",
            principalTable: "StoredFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_ScanDrafts_StoredFiles_SourceStoredFileId",
            table: "ScanDrafts");

        migrationBuilder.DropIndex(
            name: "IX_ScanDrafts_SourceStoredFileId",
            table: "ScanDrafts");

        migrationBuilder.DropColumn(
            name: "ConfirmedEntityId",
            table: "ScanDrafts");

        migrationBuilder.DropColumn(
            name: "SourceStoredFileId",
            table: "ScanDrafts");
    }
}
