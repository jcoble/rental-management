using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSimWorkerCommand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dev-only API→Engine command queue (non-prod). Global table: intentionally NO tenant_isolation
            // RLS policy (not added to the allowlists in *AddRls* migrations), like SimulationClock — the
            // Engine's admin session and the API's portfolio-scoped session both read/write it. Guid PK.
            migrationBuilder.CreateTable(
                name: "SimWorkerCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedSimUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    CreatedRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimWorkerCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SimWorkerCommands_Status",
                table: "SimWorkerCommands",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimWorkerCommands");
        }
    }
}
