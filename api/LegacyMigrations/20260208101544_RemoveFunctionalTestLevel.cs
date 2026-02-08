using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFunctionalTestLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remap TestLevel values: Functional(2) removed, Comprehensive(3→2), FullE2E(4→3)
            // Use CASE to avoid cascading update issues

            migrationBuilder.Sql(@"
                UPDATE Tasks SET RequiredTestLevel = CASE RequiredTestLevel
                    WHEN 4 THEN 3
                    WHEN 3 THEN 2
                    WHEN 2 THEN 1
                    ELSE RequiredTestLevel
                END
                WHERE RequiredTestLevel IN (2, 3, 4)");

            migrationBuilder.Sql(@"
                UPDATE TestPlans SET RequiredLevel = CASE RequiredLevel
                    WHEN 4 THEN 3
                    WHEN 3 THEN 2
                    WHEN 2 THEN 1
                    ELSE RequiredLevel
                END
                WHERE RequiredLevel IN (2, 3, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
