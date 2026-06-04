using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScanBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BatchId",
                table: "ScanDrafts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScanBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetEntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanBatches_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScanDrafts_BatchId",
                table: "ScanDrafts",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanBatches_PortfolioId",
                table: "ScanBatches",
                column: "PortfolioId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanBatches_Status",
                table: "ScanBatches",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_ScanDrafts_ScanBatches_BatchId",
                table: "ScanDrafts",
                column: "BatchId",
                principalTable: "ScanBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScanDrafts_ScanBatches_BatchId",
                table: "ScanDrafts");

            migrationBuilder.DropTable(
                name: "ScanBatches");

            migrationBuilder.DropIndex(
                name: "IX_ScanDrafts_BatchId",
                table: "ScanDrafts");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "ScanDrafts");
        }
    }
}
