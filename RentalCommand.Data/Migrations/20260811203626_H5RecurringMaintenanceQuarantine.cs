using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class H5RecurringMaintenanceQuarantine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WorkerClaimLastFailureAtUtc",
                table: "RecurringMaintenanceTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerClaimLastFailureReason",
                table: "RecurringMaintenanceTasks",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WorkerClaimQuarantinedAtUtc",
                table: "RecurringMaintenanceTasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkerClaimLastFailureAtUtc",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "WorkerClaimLastFailureReason",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropColumn(
                name: "WorkerClaimQuarantinedAtUtc",
                table: "RecurringMaintenanceTasks");
        }
    }
}
