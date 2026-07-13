using FluentAssertions;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class SandboxGraduationTriggerContractTests
{
    private static readonly string LeaseSql =
        string.Join(Environment.NewLine, LeaseLegalSchemaSql.CreateStatements);

    private static readonly string TenantAccountSql =
        string.Join(Environment.NewLine, TenantAccountPostgreSqlContract.CreateStatements);

    [Fact]
    public void LegalDeleteGuards_RequireDatabaseValidatedSandboxGraduationAuthority()
    {
        LeaseSql.Should().Contain("rc_sandbox_graduation_allows(OLD.\"PortfolioId\")");

        LeaseSql.Should().NotContain("app.is_admin");
        LeaseSql.Should().NotContain("app.rls_bypass_reason");
        LeaseSql.Should().NotContain("BackgroundWorker");
        LeaseSql.Should().NotContain("PlatformOperation");
    }

    [Fact]
    public void LegalEvidence_AllowsExactGraduationDeletes_ButNeverUpdates()
    {
        LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection.Should().Contain(
            "rc_sandbox_graduation_allows(OLD.\"PortfolioId\")");
        LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection.Should().Contain(
            "RAISE EXCEPTION 'LegalDocumentArtifacts are immutable';");
        LeaseLegalSchemaSql.CreateSignatureAuditAppendOnly.Should().Contain(
            "rc_sandbox_graduation_allows(OLD.\"PortfolioId\")");
        LeaseLegalSchemaSql.CreateSignatureAuditAppendOnly.Should().Contain(
            "RAISE EXCEPTION 'SignatureAuditEvents are append-only';");
        var artifactGuard = LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection[..
            LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection.IndexOf(
                "CREATE TRIGGER \"TR_LegalDocumentArtifacts_Immutable\"",
                StringComparison.Ordinal)];
        artifactGuard.Should().NotContain("RETURN NEW;");
        LeaseLegalSchemaSql.CreateSignatureAuditAppendOnly.Should().NotContain("RETURN NEW;");
    }

    [Fact]
    public void TenantMoneyDeleteGuards_RequireDatabaseValidatedSandboxGraduationAuthority()
    {
        TenantAccountSql.Should().Contain("rc_sandbox_graduation_allows(OLD.\"PortfolioId\")");
        TenantAccountSql.Should().Contain("rc_sandbox_graduation_allows((to_jsonb(OLD) ->> 'PortfolioId')::integer)");

        TenantAccountSql.Should().NotContain("app.is_admin");
        TenantAccountSql.Should().NotContain("app.rls_bypass_reason");
        TenantAccountSql.Should().NotContain("BackgroundWorker");
        TenantAccountSql.Should().NotContain("PlatformOperation");
    }

    [Fact]
    public void AppendOnlyGuard_DoesNotPermitSandboxGraduationUpdates()
    {
        TenantAccountSql.Should().Contain("rc_sandbox_graduation_allows(OLD.\"PortfolioId\")");
        TenantAccountSql.Should().Contain(
            "RAISE EXCEPTION '% is append-only; % is not permitted', TG_TABLE_NAME, TG_OP");
        TenantAccountSql.Should().NotContain("app.rls_bypass_reason");
    }
}
