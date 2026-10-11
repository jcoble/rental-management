using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260729011000_AddAppointmentWorkOrderLink")]
    public partial class AddAppointmentWorkOrderLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkOrderId",
                table: "Appointments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments",
                column: "WorkOrderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_WorkOrders_WorkOrderId",
                table: "Appointments",
                column: "WorkOrderId",
                principalTable: "WorkOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_WorkOrders_WorkOrderId",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_WorkOrderId",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "WorkOrderId",
                table: "Appointments");
        }
    }
}
