using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260727165000_GrantEngineStoredFileAppend")]
public partial class GrantEngineStoredFileAppend : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT INSERT ON TABLE "StoredFiles" TO rentalcommand_engine;

            DO $sequences$
            DECLARE
              sequence_name text;
            BEGIN
              FOR sequence_name IN
                SELECT DISTINCT format('%I.%I', sequence_namespace.nspname, sequence.relname)
                FROM pg_class AS base_table
                JOIN pg_namespace AS base_namespace ON base_namespace.oid = base_table.relnamespace
                JOIN pg_depend AS dependency
                  ON dependency.refobjid = base_table.oid
                 AND dependency.refclassid = 'pg_class'::regclass
                 AND dependency.deptype IN ('a', 'i')
                JOIN pg_class AS sequence
                  ON sequence.oid = dependency.objid
                 AND sequence.relkind = 'S'
                JOIN pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence.relnamespace
                WHERE base_namespace.nspname = 'public'
                  AND base_table.relname = 'StoredFiles'
              LOOP
                EXECUTE 'GRANT USAGE, SELECT ON SEQUENCE ' || sequence_name || ' TO rentalcommand_engine';
              END LOOP;
            END
            $sequences$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE INSERT ON TABLE "StoredFiles" FROM rentalcommand_engine;

            DO $sequences$
            DECLARE
              sequence_name text;
            BEGIN
              FOR sequence_name IN
                SELECT DISTINCT format('%I.%I', sequence_namespace.nspname, sequence.relname)
                FROM pg_class AS base_table
                JOIN pg_namespace AS base_namespace ON base_namespace.oid = base_table.relnamespace
                JOIN pg_depend AS dependency
                  ON dependency.refobjid = base_table.oid
                 AND dependency.refclassid = 'pg_class'::regclass
                 AND dependency.deptype IN ('a', 'i')
                JOIN pg_class AS sequence
                  ON sequence.oid = dependency.objid
                 AND sequence.relkind = 'S'
                JOIN pg_namespace AS sequence_namespace ON sequence_namespace.oid = sequence.relnamespace
                WHERE base_namespace.nspname = 'public'
                  AND base_table.relname = 'StoredFiles'
              LOOP
                EXECUTE 'REVOKE USAGE, SELECT ON SEQUENCE ' || sequence_name || ' FROM rentalcommand_engine';
              END LOOP;
            END
            $sequences$;
            """);
    }
}
