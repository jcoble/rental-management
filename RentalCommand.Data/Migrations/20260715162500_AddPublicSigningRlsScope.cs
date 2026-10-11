using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260715162500_AddPublicSigningRlsScope")]
public partial class AddPublicSigningRlsScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "LegalDocumentArtifacts", "SignatureRequests", "SignatureSigners", "StoredFiles",
                 })
        {
            migrationBuilder.Sql(
                $"GRANT SELECT ON TABLE \"{table}\" TO rentalcommand_rls_authority;");
        }

        // CREATE OR REPLACE keeps the authority bundle internally consistent while adding the
        // public-signing functions to an already-created clean baseline database.
        migrationBuilder.Sql(FoundationBaselinePostgreSql.RlsAuthorityFunctionSql);
        migrationBuilder.Sql(FoundationBaselinePostgreSql.PublicSigningPoliciesSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[]
                 {
                     "AtomicAuditLogs", "PendingFileUploads", "StoredFiles", "LegalDocumentArtifacts",
                     "SignatureAuditEvents", "SignatureRequests", "SignatureSigners", "Portfolios",
                 })
        {
            migrationBuilder.Sql($"""
                DROP POLICY IF EXISTS public_signing_update ON "{table}";
                DROP POLICY IF EXISTS public_signing_insert ON "{table}";
                DROP POLICY IF EXISTS public_signing_select ON "{table}";
                """);
        }

        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS rc_public_signing_file_allows(integer, integer, text, bigint);
            DROP FUNCTION IF EXISTS rc_public_signing_artifact_allows(integer, integer);
            DROP FUNCTION IF EXISTS rc_public_signing_request_allows(integer, integer);
            DROP FUNCTION IF EXISTS rc_public_signing_scope_allows(integer);
            """);

        foreach (var table in new[]
                 {
                     "LegalDocumentArtifacts", "SignatureRequests", "SignatureSigners", "StoredFiles",
                 })
        {
            migrationBuilder.Sql(
                $"REVOKE SELECT ON TABLE \"{table}\" FROM rentalcommand_rls_authority;");
        }
    }
}
