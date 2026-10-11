using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Data;

namespace RentalCommand.TestCommon;

/// <summary>
/// Test-only <see cref="RentalCommandDbContext"/> whose relational model can be created by
/// SQLite. Production configuration remains PostgreSQL-specific; tests that verify SQL
/// translation, database constraints, projections, triggers, or migrations must still use
/// PostgreSQL.
/// </summary>
public class SqliteCompatibleRentalCommandDbContext(
    DbContextOptions<RentalCommandDbContext> options) : RentalCommandDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyRentalCommandSqliteCompatibility();
    }
}

/// <summary>
/// Central compatibility pass for lightweight SQLite unit-test schemas.
/// </summary>
public static class SqliteModelCompatibilityExtensions
{
    private const string SqliteUuidDefault =
        "(lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || " +
        "substr(lower(hex(randomblob(2))), 2) || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || " +
        "substr(lower(hex(randomblob(2))), 2) || '-' || lower(hex(randomblob(6))))";

    /// <summary>
    /// Removes PostgreSQL DDL annotations and translates database defaults that SQLite can
    /// faithfully provide. This operates only on the derived test model.
    /// </summary>
    public static ModelBuilder ApplyRentalCommandSqliteCompatibility(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            // Let the SQLite provider infer every store type from the CLR type/converter. This
            // covers jsonb, inet, uuid, bytea, timestamp/date/time, and future PostgreSQL types
            // without maintaining another property-by-property list in tests.
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnType(null);

                var defaultSql = property.GetDefaultValueSql();
                if (defaultSql is null)
                    continue;

                property.SetDefaultValueSql(TranslateDefaultSql(defaultSql));
            }

            // Constraint behavior belongs to PostgreSQL integration tests. Several canonical
            // constraints use PostgreSQL operators (for example regex '~') that SQLite cannot
            // parse while creating an otherwise useful unit-test schema.
            foreach (var constraint in entityType.GetCheckConstraints().ToList())
                entityType.RemoveCheckConstraint(constraint.Name);

            // EF trigger metadata is used to alter provider save behavior even though
            // EnsureCreated does not install trigger bodies. Unit-test SQLite models have no
            // production triggers, so remove that metadata as well.
            foreach (var trigger in entityType.GetDeclaredTriggers().ToList())
                entityType.RemoveTrigger(trigger.ModelName);

            // ToView mappings intentionally remain query-only. EnsureCreated excludes views
            // from its DDL, and the few unit tests that need a projection install an explicit
            // SQLite view tailored to that test. Production projection SQL remains PostgreSQL.
        }

        return modelBuilder;
    }

    private static string? TranslateDefaultSql(string defaultSql) =>
        defaultSql.Trim() switch
        {
            "gen_random_uuid()" => SqliteUuidDefault,
            "clock_timestamp()" => "CURRENT_TIMESTAMP",
            "now()" => "CURRENT_TIMESTAMP",
            _ => IsPostgresOnlySql(defaultSql) ? null : defaultSql,
        };

    private static bool IsPostgresOnlySql(string sql) =>
        sql.Contains("::", StringComparison.Ordinal) ||
        sql.Contains("ILIKE", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("current_setting(", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("gen_random_", StringComparison.OrdinalIgnoreCase) ||
        sql.Contains("clock_timestamp(", StringComparison.OrdinalIgnoreCase);
}
