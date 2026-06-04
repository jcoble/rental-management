using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationChannelPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PortfolioId",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill: the old single global settings row (PortfolioId defaulted to 0) is reassigned
            // to the lowest existing portfolio id so the now-portfolio-scoped service keeps finding it.
            // No-op when there are no portfolios or no legacy row. Done before the unique index so a
            // (defensive) duplicate can't break index creation.
            migrationBuilder.Sql(
                """
                UPDATE "NotificationSettings"
                SET "PortfolioId" = (SELECT MIN("Id") FROM "Portfolios")
                WHERE "PortfolioId" = 0
                  AND EXISTS (SELECT 1 FROM "Portfolios");
                """);

            migrationBuilder.CreateTable(
                name: "NotificationPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    NotificationType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EnableInApp = table.Column<bool>(type: "boolean", nullable: false),
                    EnableEmail = table.Column<bool>(type: "boolean", nullable: false),
                    EnableSms = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationPreferences_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSettings_PortfolioId",
                table: "NotificationSettings",
                column: "PortfolioId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationPreferences_PortfolioId_NotificationType",
                table: "NotificationPreferences",
                columns: new[] { "PortfolioId", "NotificationType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationPreferences");

            migrationBuilder.DropIndex(
                name: "IX_NotificationSettings_PortfolioId",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "PortfolioId",
                table: "NotificationSettings");
        }
    }
}
