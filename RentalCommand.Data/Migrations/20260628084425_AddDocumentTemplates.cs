using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DocumentTemplateId",
                table: "SignatureRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DocumentTemplateVersion",
                table: "SignatureRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateFieldSnapshotJson",
                table: "SignatureRequests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DocumentTemplateId",
                table: "Leases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DocumentTemplateVersion",
                table: "Leases",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RenderMode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OriginalStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    DraftHtml = table.Column<string>(type: "text", nullable: true),
                    CompiledStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    DefaultForPortfolio = table.Column<bool>(type: "boolean", nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplates", x => x.Id);
                    table.CheckConstraint("CK_DocumentTemplate_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_StoredFiles_CompiledStoredFileId",
                        column: x => x.CompiledStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DocumentTemplates_StoredFiles_OriginalStoredFileId",
                        column: x => x.OriginalStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DocumentTemplateFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentTemplateId = table.Column<int>(type: "integer", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SignerRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    XPct = table.Column<double>(type: "double precision", nullable: false),
                    YPct = table.Column<double>(type: "double precision", nullable: false),
                    WidthPct = table.Column<double>(type: "double precision", nullable: false),
                    HeightPct = table.Column<double>(type: "double precision", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Locked = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    DefaultText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplateFields", x => x.Id);
                    table.CheckConstraint("CK_DocumentTemplateField_HeightPct", "\"HeightPct\" > 0 AND \"HeightPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_Page", "\"PageNumber\" >= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_WidthPct", "\"WidthPct\" > 0 AND \"WidthPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_XExtent", "\"XPct\" + \"WidthPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_XPct", "\"XPct\" >= 0 AND \"XPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_YExtent", "\"YPct\" + \"HeightPct\" <= 1");
                    table.CheckConstraint("CK_DocumentTemplateField_YPct", "\"YPct\" >= 0 AND \"YPct\" <= 1");
                    table.ForeignKey(
                        name: "FK_DocumentTemplateFields_DocumentTemplates_DocumentTemplateId",
                        column: x => x.DocumentTemplateId,
                        principalTable: "DocumentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_DocumentTemplateId",
                table: "SignatureRequests",
                column: "DocumentTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_Leases_DocumentTemplateId",
                table: "Leases",
                column: "DocumentTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplateFields_DocumentTemplateId",
                table: "DocumentTemplateFields",
                column: "DocumentTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplateFields_DocumentTemplateId_FieldKey",
                table: "DocumentTemplateFields",
                columns: new[] { "DocumentTemplateId", "FieldKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_CompiledStoredFileId",
                table: "DocumentTemplates",
                column: "CompiledStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_OriginalStoredFileId",
                table: "DocumentTemplates",
                column: "OriginalStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId",
                table: "DocumentTemplates",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId_Kind_DefaultForPortfolio",
                table: "DocumentTemplates",
                columns: new[] { "PortfolioId", "Kind", "DefaultForPortfolio" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PortfolioId_Kind_Status",
                table: "DocumentTemplates",
                columns: new[] { "PortfolioId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_PropertyId",
                table: "DocumentTemplates",
                column: "PropertyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Leases_DocumentTemplates_DocumentTemplateId",
                table: "Leases",
                column: "DocumentTemplateId",
                principalTable: "DocumentTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_SignatureRequests_DocumentTemplates_DocumentTemplateId",
                table: "SignatureRequests",
                column: "DocumentTemplateId",
                principalTable: "DocumentTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Leases_DocumentTemplates_DocumentTemplateId",
                table: "Leases");

            migrationBuilder.DropForeignKey(
                name: "FK_SignatureRequests_DocumentTemplates_DocumentTemplateId",
                table: "SignatureRequests");

            migrationBuilder.DropTable(
                name: "DocumentTemplateFields");

            migrationBuilder.DropTable(
                name: "DocumentTemplates");

            migrationBuilder.DropIndex(
                name: "IX_SignatureRequests_DocumentTemplateId",
                table: "SignatureRequests");

            migrationBuilder.DropIndex(
                name: "IX_Leases_DocumentTemplateId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "DocumentTemplateId",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "DocumentTemplateVersion",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "TemplateFieldSnapshotJson",
                table: "SignatureRequests");

            migrationBuilder.DropColumn(
                name: "DocumentTemplateId",
                table: "Leases");

            migrationBuilder.DropColumn(
                name: "DocumentTemplateVersion",
                table: "Leases");
        }
    }
}
