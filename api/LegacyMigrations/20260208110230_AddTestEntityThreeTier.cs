using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddTestEntityThreeTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create the new Tests table
            migrationBuilder.CreateTable(
                name: "Tests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TestPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TestFile = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Framework = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunOutput = table.Column<string>(type: "TEXT", nullable: true),
                    TotalRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    PassedRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tests_TestPlans_TestPlanId",
                        column: x => x.TestPlanId,
                        principalTable: "TestPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tests_TestPlanId",
                table: "Tests",
                column: "TestPlanId");

            // 2. Migrate TestRecords → Tests (create auto-plans for tasks that have TestRecords but no TestPlans)
            migrationBuilder.Sql(@"
                -- Create auto-generated plans for tasks that have TestRecords but no TestPlans
                INSERT INTO TestPlans (TaskId, RequiredLevel, Name, Description, Status, Source, CreatedAt, UpdatedAt)
                SELECT DISTINCT tr.TaskId, 0, 'Migrated Tests', 'Auto-migrated from TestRecords', 0, 1, datetime('now'), datetime('now')
                FROM TestRecords tr
                WHERE NOT EXISTS (SELECT 1 FROM TestPlans tp WHERE tp.TaskId = tr.TaskId);

                -- Migrate TestRecords into the Tests table
                INSERT INTO Tests (TestPlanId, OrderIndex, Name, Description, Type, Status, TestFile, Framework, LastRunAt, LastRunOutput, TotalRuns, PassedRuns, FailedRuns, CreatedAt)
                SELECT
                    tp.Id,
                    ROW_NUMBER() OVER (PARTITION BY tp.Id ORDER BY tr.Id) - 1,
                    COALESCE(tr.TestName, 'Test'),
                    NULL,
                    tr.TestType,
                    tr.Status,
                    tr.TestFile,
                    tr.Framework,
                    tr.LastRunAt,
                    tr.LastRunOutput,
                    tr.TotalRuns,
                    tr.PassedRuns,
                    tr.FailedRuns,
                    tr.CreatedAt
                FROM TestRecords tr
                JOIN TestPlans tp ON tp.TaskId = tr.TaskId;
            ");

            // 3. Migrate existing TestSteps: for each TestPlan that has steps, create a default Test and reparent steps
            migrationBuilder.Sql(@"
                -- Create a default Test for each TestPlan that has existing steps
                INSERT INTO Tests (TestPlanId, OrderIndex, Name, Type, Status, CreatedAt)
                SELECT DISTINCT ts.TestPlanId, 0, 'UI Test', 2, 1, datetime('now')
                FROM TestSteps ts
                WHERE NOT EXISTS (SELECT 1 FROM Tests t WHERE t.TestPlanId = ts.TestPlanId);
            ");

            // 4. Add TestId column to TestSteps (nullable first for migration)
            migrationBuilder.AddColumn<int>(
                name: "TestId",
                table: "TestSteps",
                type: "INTEGER",
                nullable: true);

            // 5. Set TestId on all existing steps to point to their plan's Test
            migrationBuilder.Sql(@"
                UPDATE TestSteps
                SET TestId = (
                    SELECT t.Id FROM Tests t
                    WHERE t.TestPlanId = TestSteps.TestPlanId
                    ORDER BY t.OrderIndex
                    LIMIT 1
                );
            ");

            // 6. Drop the old FK and column
            migrationBuilder.DropForeignKey(
                name: "FK_TestSteps_TestPlans_TestPlanId",
                table: "TestSteps");

            migrationBuilder.DropIndex(
                name: "IX_TestSteps_TestPlanId",
                table: "TestSteps");

            migrationBuilder.DropColumn(
                name: "TestPlanId",
                table: "TestSteps");

            // 7. Make TestId non-nullable
            migrationBuilder.AlterColumn<int>(
                name: "TestId",
                table: "TestSteps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TestSteps_TestId",
                table: "TestSteps",
                column: "TestId");

            migrationBuilder.AddForeignKey(
                name: "FK_TestSteps_Tests_TestId",
                table: "TestSteps",
                column: "TestId",
                principalTable: "Tests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // 8. Drop the old TestRecords table
            migrationBuilder.DropTable(
                name: "TestRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TestSteps_Tests_TestId",
                table: "TestSteps");

            migrationBuilder.DropIndex(
                name: "IX_TestSteps_TestId",
                table: "TestSteps");

            migrationBuilder.DropColumn(
                name: "TestId",
                table: "TestSteps");

            migrationBuilder.AddColumn<int>(
                name: "TestPlanId",
                table: "TestSteps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TestSteps_TestPlanId",
                table: "TestSteps",
                column: "TestPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_TestSteps_TestPlans_TestPlanId",
                table: "TestSteps",
                column: "TestPlanId",
                principalTable: "TestPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(
                name: "Tests");

            migrationBuilder.CreateTable(
                name: "TestRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FailedRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    Framework = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunOutput = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunResult = table.Column<string>(type: "TEXT", nullable: true),
                    PassedRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TestFile = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TestName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TestType = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRuns = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestRecords_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TestRecords_TaskId",
                table: "TestRecords",
                column: "TaskId");
        }
    }
}
