using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeEsign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignatureRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    PublicId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LeaseId = table.Column<int>(type: "integer", nullable: false),
                    DocumentName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OriginalStoredFileId = table.Column<int>(type: "integer", nullable: false),
                    SignedStoredFileId = table.Column<int>(type: "integer", nullable: true),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Leases_LeaseId",
                        column: x => x.LeaseId,
                        principalTable: "Leases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_StoredFiles_OriginalStoredFileId",
                        column: x => x.OriginalStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SignatureRequests_StoredFiles_SignedStoredFileId",
                        column: x => x.SignedStoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SignatureSigners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignatureRequestId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SignatureType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TypedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DrawnSignatureImage = table.Column<byte[]>(type: "bytea", nullable: true),
                    ConsentGiven = table.Column<bool>(type: "boolean", nullable: false),
                    SignedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ViewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureSigners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureSigners_SignatureRequests_SignatureRequestId",
                        column: x => x.SignatureRequestId,
                        principalTable: "SignatureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignatureAuditEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignatureRequestId = table.Column<int>(type: "integer", nullable: false),
                    SignerId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignatureAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignatureAuditEvents_SignatureRequests_SignatureRequestId",
                        column: x => x.SignatureRequestId,
                        principalTable: "SignatureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SignatureAuditEvents_SignatureSigners_SignerId",
                        column: x => x.SignerId,
                        principalTable: "SignatureSigners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAuditEvents_SignatureRequestId",
                table: "SignatureAuditEvents",
                column: "SignatureRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureAuditEvents_SignerId",
                table: "SignatureAuditEvents",
                column: "SignerId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_LeaseId",
                table: "SignatureRequests",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_OriginalStoredFileId",
                table: "SignatureRequests",
                column: "OriginalStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_PortfolioId",
                table: "SignatureRequests",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_PublicId",
                table: "SignatureRequests",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_SignedStoredFileId",
                table: "SignatureRequests",
                column: "SignedStoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureRequests_Status",
                table: "SignatureRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_SignatureRequestId",
                table: "SignatureSigners",
                column: "SignatureRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SignatureSigners_Token",
                table: "SignatureSigners",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignatureAuditEvents");

            migrationBuilder.DropTable(
                name: "SignatureSigners");

            migrationBuilder.DropTable(
                name: "SignatureRequests");
        }
    }
}
