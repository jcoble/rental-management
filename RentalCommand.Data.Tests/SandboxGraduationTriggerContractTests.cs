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
    public void LegalDeleteGuards_AcceptOnlyTheExactSandboxGraduationReason()
    {
        LeaseSql.Should().Contain(
            "IF current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN\n" +
            "      RETURN OLD;");
        LeaseSql.Should().Contain(
            "IF TG_OP = 'DELETE'\n" +
            "     AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN");

        LeaseSql.Should().NotContain("app.is_admin");
        LeaseSql.Should().NotContain("BackgroundWorker");
        LeaseSql.Should().NotContain("PlatformOperation");
    }

    [Fact]
    public void LegalEvidence_AllowsExactGraduationDeletes_ButNeverUpdates()
    {
        LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection.Should().Contain(
            "IF TG_OP = 'DELETE'\n" +
            "     AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN");
        LeaseLegalSchemaSql.CreateArtifactAndAgreementProtection.Should().Contain(
            "RAISE EXCEPTION 'LegalDocumentArtifacts are immutable';");
        LeaseLegalSchemaSql.CreateSignatureAuditAppendOnly.Should().Contain(
            "IF TG_OP = 'DELETE'\n" +
            "     AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN");
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
    public void TenantMoneyDeleteGuards_AcceptOnlyTheExactSandboxGraduationReason()
    {
        TenantAccountSql.Should().Contain(
            "IF TG_OP = 'DELETE'\n" +
            "     AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN\n" +
            "    RETURN OLD;");
        TenantAccountSql.Should().Contain(
            "IF current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN\n" +
            "      RETURN OLD;");

        TenantAccountSql.Should().NotContain("app.is_admin");
        TenantAccountSql.Should().NotContain("BackgroundWorker");
        TenantAccountSql.Should().NotContain("PlatformOperation");
    }

    [Fact]
    public void AppendOnlyGuard_DoesNotPermitSandboxGraduationUpdates()
    {
        TenantAccountSql.Should().Contain(
            "IF TG_OP = 'DELETE'\n" +
            "     AND current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN");
        TenantAccountSql.Should().Contain(
            "RAISE EXCEPTION '% is append-only; % is not permitted', TG_TABLE_NAME, TG_OP");
        TenantAccountSql.Should().NotContain(
            "IF current_setting('app.rls_bypass_reason', true) = 'SandboxGraduation' THEN\n" +
            "    RETURN NEW;");
    }
}
