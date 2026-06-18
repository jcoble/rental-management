using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingSyncMapMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "AccountingSyncMaps",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "AccountingSyncMaps");
        }
    }
}
