using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerPortalRoleProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "RoleProfiles",
                columns: new[] { "Id", "DefaultExperience", "DefaultScopeKind", "Description", "DisplayName", "Key" },
                values: new object[] { 5, "Owner", "AllProperties", "Owner relationship access only. Grants no management, leasing, maintenance, or workspace capabilities.", "Owner Portal", "owner-portal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "RoleProfiles",
                keyColumn: "Id",
                keyValue: 5);
        }
    }
}
