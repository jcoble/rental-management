using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditSearchTrgmIndexes : Migration
    {
        // Trigram GIN indexes that back the audit page's free-text search (AuditQueryService.ApplySearch).
        // Search runs `LOWER(col) LIKE LOWER('%term%')` across the visible text columns; a plain B-tree
        // can't serve a leading-wildcard LIKE, so without these the search degrades to a sequential scan
        // as the trail grows. pg_trgm's GIN index on the lowered column makes the contains-match
        // index-driven and keeps the query Postgres-side at scale.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_EntityType_trgm\" " +
                "ON \"AuditLogs\" USING gin (lower(\"EntityType\") gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_ActorLabel_trgm\" " +
                "ON \"AuditLogs\" USING gin (lower(\"ActorLabel\") gin_trgm_ops);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_IpAddress_trgm\" " +
                "ON \"AuditLogs\" USING gin (lower(\"IpAddress\") gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_AuditLogs_EntityType_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_AuditLogs_ActorLabel_trgm\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_AuditLogs_IpAddress_trgm\";");
            // Leave the pg_trgm extension installed; other features may rely on it.
        }
    }
}
