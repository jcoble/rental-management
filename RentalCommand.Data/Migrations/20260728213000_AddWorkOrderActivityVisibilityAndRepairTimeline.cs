using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260728213000_AddWorkOrderActivityVisibilityAndRepairTimeline")]
    public partial class AddWorkOrderActivityVisibilityAndRepairTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkOrderStatusEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Status");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "WorkOrderStatusEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddColumn<DateTime>(
                name: "ChronologyRepairOriginalCreatedAtUtc",
                table: "WorkOrderStatusEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChronologyRepairOriginalUpdatedAtUtc",
                table: "WorkOrders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "WorkOrderStatusEvents" AS e
                SET "ChronologyRepairOriginalCreatedAtUtc" = e."CreatedAtUtc",
                    "CreatedAtUtc" = w."RequestedAt"
                FROM "WorkOrders" AS w
                WHERE e."WorkOrderId" = w."Id"
                  AND e."PortfolioId" = w."PortfolioId"
                  AND e."CreatedAtUtc" < w."RequestedAt"
                  AND e."ChronologyRepairOriginalCreatedAtUtc" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "WorkOrders"
                SET "ChronologyRepairOriginalUpdatedAtUtc" = "UpdatedAt",
                    "UpdatedAt" = "RequestedAt"
                WHERE "UpdatedAt" < "RequestedAt"
                  AND "ChronologyRepairOriginalUpdatedAtUtc" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkOrderStatusEvents");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "WorkOrderStatusEvents");

            migrationBuilder.DropColumn(
                name: "ChronologyRepairOriginalCreatedAtUtc",
                table: "WorkOrderStatusEvents");

            migrationBuilder.DropColumn(
                name: "ChronologyRepairOriginalUpdatedAtUtc",
                table: "WorkOrders");
        }
    }
}
