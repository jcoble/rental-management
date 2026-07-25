using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class LeaseAddendumCorrectionSqlTranslationTests
{
    [Fact]
    public void Children_are_copied_by_one_exact_server_side_statement()
    {
        var sql = PrivateSql("CopyAddendumCorrectionChildrenSql");

        sql.Should().Contain("WITH eligible AS MATERIALIZED");
        sql.Should().Contain("source.\"Id\" = @sourceAddendumId");
        sql.Should().Contain("correction.\"Id\" = @correctionAddendumId");
        sql.Should().Contain("correction.\"ReplacesAddendumId\" = source.\"Id\"");
        sql.Should().Contain("source.\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().Contain("source.\"SupersededByAddendumId\" IS NULL");
        sql.Should().Contain("correction.\"EffectiveFromOn\" > source.\"EffectiveFromOn\"");
        sql.Should().Contain("FOR UPDATE OF source, correction");
        sql.Should().Contain("INSERT INTO \"LeaseAddendumSigners\"");
        sql.Should().Contain("INSERT INTO \"LeaseAddendumFinancialEffects\"");
        sql.Should().Contain("AS \"CreatedSignerIdsJson\"");
        sql.Should().Contain("AS \"CreatedFinancialEffectIdsJson\"");
        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDmlTargets(sql).Should().BeEquivalentTo(
        [
            new AtomicRawDmlTarget("LeaseAddendumSigners", AtomicRawDmlOperation.Insert),
            new AtomicRawDmlTarget("LeaseAddendumFinancialEffects", AtomicRawDmlOperation.Insert),
        ]);
    }

    [Fact]
    public void Addendum_signer_scope_uses_the_shared_exact_party_tenant_pair_statement()
    {
        var sql = PrivateSql("ValidateAgreementDraftSignerScopeSql");

        sql.Should().Contain("party.\"TenantId\" <> input.tenant_id");
        sql.Should().Contain("(input.lease_management_party_id IS NULL) <> (input.tenant_id IS NULL)");
        sql.Should().Contain("party.\"LeaseManagementId\" = @leaseManagementId");
        sql.Should().Contain("party.\"PortfolioId\" = @portfolioId");
    }

    [Fact]
    public void Existing_live_correction_branch_is_filtered_in_postgresql()
    {
        using var db = Context();
        const int portfolioId = 7;
        const int leaseManagementId = 19;
        const int sourceAddendumId = 23;
        var query = LeaseAddendumCommandSupport.LiveCorrectionSuccessors(
            db.LeaseAddenda, portfolioId, leaseManagementId, sourceAddendumId);

        var sql = query.ToQueryString();
        sql.Should().Contain("FROM \"LeaseAddenda\"");
        sql.Should().Contain("@portfolioId='7'");
        sql.Should().Contain("@leaseManagementId='19'");
        sql.Should().Contain("@sourceAddendumId='23'");
        sql.Should().Contain("\"PortfolioId\" = @portfolioId");
        sql.Should().Contain("\"LeaseManagementId\" = @leaseManagementId");
        sql.Should().Contain("\"ReplacesAddendumId\" = @sourceAddendumId");
        sql.Should().Contain("\"DraftCanceledAtUtc\" IS NULL");
        sql.Should().Contain("\"VoidedAtUtc\" IS NULL OR");
        sql.Should().Contain("\"FullyExecutedAtUtc\" IS NOT NULL");
        sql.Should().NotContain("ClientEvaluation");
    }

    private static RentalCommandDbContext Context() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_only;Username=none;Password=none")
            .Options);

    private static string PrivateSql(string fieldName) =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException($"Missing SQL constant {fieldName}."));
}
