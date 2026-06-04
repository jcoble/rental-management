using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseEsign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EsignEnvelopeId",
                table: "Leases",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EsignStatus",
                table: "Leases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SignedDocumentStoredFileId",
                table: "Leases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_EsignEnvelopeId",
                table: "Leases",
                column: "EsignEnvelopeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leases_EsignEnvelopeId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "EsignEnvelopeId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "EsignStatus",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "SignedDocumentStoredFileId",
                table: "Leases");
        }
    }
}
