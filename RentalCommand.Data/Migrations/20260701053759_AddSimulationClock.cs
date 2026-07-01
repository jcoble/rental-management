using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulationClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SimulationClocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SimAnchorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RealAnchorUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAtRealUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulationClocks", x => x.Id);
                });

            // Seed the single fixed row (Id = 1) in Real mode. Explicit UTC timestamptz literals so the
            // seed is valid under Npgsql's UTC-Kind enforcement (legacy timestamp behavior is OFF) — a
            // parameterized default(DateTime) would throw. Idempotent (safe on re-apply). Global table:
            // intentionally NO tenant_isolation RLS policy, so a portfolio-scoped session can read it.
            migrationBuilder.Sql(
                "INSERT INTO \"SimulationClocks\" " +
                "(\"Id\", \"Mode\", \"SimAnchorUtc\", \"RealAnchorUtc\", \"TimeZoneId\", \"UpdatedAtRealUtc\") " +
                "VALUES (1, 'Real', TIMESTAMPTZ '2000-01-01 00:00:00+00', TIMESTAMPTZ '2000-01-01 00:00:00+00', " +
                "NULL, TIMESTAMPTZ '2000-01-01 00:00:00+00') " +
                "ON CONFLICT (\"Id\") DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SimulationClocks");
        }
    }
}
