using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurringMaintenanceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecurringMaintenanceTaskId",
                table: "WorkOrders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCost",
                table: "RecurringMaintenanceTasks",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "ScheduledTime",
                table: "RecurringMaintenanceTasks",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_RecurringMaintenanceTaskId",
                table: "WorkOrders",
                column: "RecurringMaintenanceTaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkOrders_RecurringMaintenanceTasks_RecurringMaintenanceTa~",
                table: "WorkOrders",
                column: "RecurringMaintenanceTaskId",
                principalTable: "RecurringMaintenanceTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkOrders_RecurringMaintenanceTasks_RecurringMaintenanceTa~",
                table: "WorkOrders");

            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_RecurringMaintenanceTaskId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "RecurringMaintenanceTaskId",
                table: "WorkOrders");

            migrationBuilder.DropColumn(
                name: "EstimatedCost",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "ScheduledTime",
                table: "RecurringMaintenanceTasks");
        }
    }
}
