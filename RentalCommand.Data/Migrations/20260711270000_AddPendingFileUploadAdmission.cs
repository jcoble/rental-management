using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711270000_AddPendingFileUploadAdmission")]
public sealed class AddPendingFileUploadAdmission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PendingFileUploads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                ActorScopeId = table.Column<int>(type: "integer", nullable: false),
                Purpose = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                OperationKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StoragePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                StoredFileId = table.Column<int>(type: "integer", nullable: true),
                CleanupClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                CleanupClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                CleanupClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PendingFileUploads", x => x.Id);
                table.ForeignKey(
                    name: "FK_PendingFileUploads_StoredFiles_StoredFileId",
                    column: x => x.StoredFileId,
                    principalTable: "StoredFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PendingFileUploads_PortfolioId_ActorScopeId_Purpose_OperationKeyHash",
            table: "PendingFileUploads",
            columns: new[] { "PortfolioId", "ActorScopeId", "Purpose", "OperationKeyHash" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_PendingFileUploads_State_CreatedAtUtc_CleanupClaimExpiresAtUtc",
            table: "PendingFileUploads",
            columns: new[] { "State", "CreatedAtUtc", "CleanupClaimExpiresAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_PendingFileUploads_StoredFileId",
            table: "PendingFileUploads",
            column: "StoredFileId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "PendingFileUploads");
}
