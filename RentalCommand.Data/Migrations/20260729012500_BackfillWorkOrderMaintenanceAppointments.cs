using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260729012500_BackfillWorkOrderMaintenanceAppointments")]
    public partial class BackfillWorkOrderMaintenanceAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicate_work_order_id integer;
                BEGIN
                    SELECT appointment."WorkOrderId"
                    INTO duplicate_work_order_id
                    FROM "Appointments" AS appointment
                    WHERE appointment."WorkOrderId" IS NOT NULL
                    GROUP BY appointment."WorkOrderId"
                    HAVING COUNT(*) > 1
                    ORDER BY appointment."WorkOrderId"
                    LIMIT 1;

                    IF duplicate_work_order_id IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Cannot enforce unique appointment work order links; duplicate Appointment.WorkOrderId % exists.',
                            duplicate_work_order_id;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments");

            migrationBuilder.Sql("""
                INSERT INTO "Appointments" (
                    "PortfolioId",
                    "PropertyId",
                    "UnitId",
                    "LeaseManagementId",
                    "RentalApplicationId",
                    "TenantId",
                    "WorkOrderId",
                    "Title",
                    "ProspectName",
                    "ProspectEmail",
                    "Type",
                    "Status",
                    "ScheduledStart",
                    "ScheduledEnd",
                    "AssignedTo",
                    "Notes",
                    "CreatedAt",
                    "UpdatedAt")
                SELECT
                    work_order."PortfolioId",
                    work_order."PropertyId",
                    work_order."UnitId",
                    work_order."LeaseManagementId",
                    NULL,
                    work_order."TenantId",
                    work_order."Id",
                    work_order."Title",
                    NULL,
                    NULL,
                    4,
                    CASE WHEN work_order."Status" = 1 THEN 1 ELSE 0 END,
                    work_order."ScheduledFor",
                    work_order."ScheduledWindowEnd",
                    vendor."Name",
                    COALESCE(NULLIF(work_order."TechnicianAccessInstructions", ''), work_order."Description"),
                    work_order."RequestedAt",
                    work_order."UpdatedAt"
                FROM "WorkOrders" AS work_order
                LEFT JOIN "Vendors" AS vendor
                    ON vendor."PortfolioId" = work_order."PortfolioId"
                    AND vendor."Id" = work_order."VendorId"
                WHERE work_order."TenantId" IS NOT NULL
                    AND work_order."LeaseManagementId" IS NOT NULL
                    AND work_order."ScheduledFor" IS NOT NULL
                    AND work_order."Status" NOT IN (4, 5, 7)
                    AND NOT EXISTS (
                        SELECT 1
                        FROM "Appointments" AS appointment
                        WHERE appointment."WorkOrderId" = work_order."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments",
                column: "WorkOrderId",
                unique: true,
                filter: "\"WorkOrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments",
                column: "WorkOrderId");
        }
    }
}
