using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectIdToTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add column without FK first (default 1 for existing rows)
            migrationBuilder.AddColumn<int>(
                name: "ProjectId",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // Backfill tasks that have a phase: set ProjectId from Phase -> Milestone -> Project
            migrationBuilder.Sql(@"
                UPDATE Tasks
                SET ProjectId = (
                    SELECT m.ProjectId
                    FROM Phases p
                    JOIN Milestones m ON p.MilestoneId = m.Id
                    WHERE p.Id = Tasks.PhaseId
                )
                WHERE Tasks.PhaseId IS NOT NULL
            ");

            // PullRequestUrl already added by AddAgentActivityTracking migration

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ProjectId",
                table: "Tasks",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Projects_ProjectId",
                table: "Tasks",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Projects_ProjectId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_ProjectId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "Tasks");
        }
    }
}
