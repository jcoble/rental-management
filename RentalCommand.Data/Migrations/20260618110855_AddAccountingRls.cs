using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <summary>
    /// Extends the PostgreSQL Row-Level Security (RLS) tenant-isolation backstop
    /// (established in <c>AddRlsTenantIsolationAndDropDeadBankConnectionDeletedAt</c>)
    /// to the four accounting-integration backbone tables added by
    /// <c>AddAccountingIntegrationBackbone</c>. The schema migration alone leaves
    /// these tables UNPROTECTED — without this migration cross-portfolio reads/writes
    /// are not forbidden at the DB layer and AC-3 fails.
    ///
    /// <para>Same contract as the original: each table gets <c>ENABLE</c> +
    /// <c>FORCE ROW LEVEL SECURITY</c> and a PUBLIC <c>tenant_isolation</c> policy
    /// (USING + WITH CHECK) keyed on the <c>app.current_portfolio_id</c> /
    /// <c>app.is_admin</c> session GUCs the connection interceptor sets per request.
    /// The grants to the <c>rentalcommand_api</c> role are already covered by that
    /// migration's <c>ALTER DEFAULT PRIVILEGES</c> for new tables in the public
    /// schema, so only ENABLE/FORCE + policies are needed here.</para>
    ///
    /// <para>All four tables carry a non-nullable <c>PortfolioId</c>, so they use the
    /// direct portfolio predicate. The helpers DROP-then-CREATE so a re-applied
    /// migration on a partially-migrated DB stays idempotent.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class AddAccountingRls : Migration
    {
        // The four accounting-integration tables — all have a non-nullable PortfolioId column.
        private static readonly string[] DirectPortfolioTables =
        {
            "AccountingConnections", "OAuthStates", "AccountingEntityMappings", "AccountingSyncMaps",
        };

        // Identical predicate to the base RLS migration: visible iff the row's portfolio matches the
        // session GUC, or the session is an admin/background/unauthenticated context.
        private const string PortfolioPredicate =
            "(\"PortfolioId\" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int " +
            "OR current_setting('app.is_admin', true) = 'true')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in DirectPortfolioTables)
            {
                EnableForce(migrationBuilder, table);
                CreatePolicy(migrationBuilder, table, PortfolioPredicate);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in DirectPortfolioTables)
            {
                DropPolicy(migrationBuilder, table);
                DisableRls(migrationBuilder, table);
            }
        }

        private static void EnableForce(MigrationBuilder mb, string table)
        {
            mb.Sql($"ALTER TABLE \"{table}\" ENABLE ROW LEVEL SECURITY;");
            mb.Sql($"ALTER TABLE \"{table}\" FORCE ROW LEVEL SECURITY;");
        }

        private static void DisableRls(MigrationBuilder mb, string table)
        {
            mb.Sql($"ALTER TABLE \"{table}\" NO FORCE ROW LEVEL SECURITY;");
            mb.Sql($"ALTER TABLE \"{table}\" DISABLE ROW LEVEL SECURITY;");
        }

        // DROP + CREATE so a re-applied migration on a partially-migrated DB is idempotent.
        private static void CreatePolicy(MigrationBuilder mb, string table, string predicate)
        {
            mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON \"{table}\";");
            mb.Sql(
                $"CREATE POLICY tenant_isolation ON \"{table}\" " +
                $"USING {predicate} WITH CHECK {predicate};");
        }

        private static void DropPolicy(MigrationBuilder mb, string table)
            => mb.Sql($"DROP POLICY IF EXISTS tenant_isolation ON \"{table}\";");
    }
}
