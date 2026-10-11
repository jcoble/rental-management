using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260727181000_AddStoredFileContentHash")]
public partial class AddStoredFileContentHash : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ContentSha256",
            table: "StoredFiles",
            type: "char(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "UX_StoredFiles_Active_Target_ContentSha256",
            table: "StoredFiles",
            columns: new[] { "PortfolioId", "EntityType", "EntityId", "ContentSha256" },
            unique: true,
            filter: "\"DeletedAt\" IS NULL AND \"ContentSha256\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_StoredFiles_Active_Target_ContentSha256",
            table: "StoredFiles");

        migrationBuilder.DropColumn(
            name: "ContentSha256",
            table: "StoredFiles");
    }
}
