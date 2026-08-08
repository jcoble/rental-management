using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Tests;

public sealed class LeaseLifecycleProjectionContractTests
{
    private static RentalCommandDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new RentalCommandDbContext(options);
    }

    [Fact]
    public void Effective_clock_materializes_one_statement_instant_and_rejects_deleted_workspaces()
    {
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().Contain("WITH statement_clock AS MATERIALIZED");
        LeaseEffectiveClockSql.CreateEffectiveNowUtc
            .Split("statement_timestamp()", StringSplitOptions.None)
            .Should().HaveCount(2);
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().NotContain("clock_timestamp()");
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().Contain("portfolio.\"DeletedAt\" IS NULL");
        LeaseEffectiveClockSql.CreateBusinessDate.Should().Contain("portfolio.\"DeletedAt\" IS NULL");
    }

    [Fact]
    public void Governing_agreement_is_derived_only_from_executed_nonvoid_facts()
    {
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"FullyExecutedAtUtc\" IS NOT NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"VoidedAtUtc\" IS NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"DraftCanceledAtUtc\" IS NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("AS \"IsGoverning\"");
    }

    [Fact]
    public void Governing_execution_requires_issued_and_executed_artifact_evidence()
    {
        using var db = CreateDb();
        var agreement = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(LeaseAgreement))!;

        LeaseManagementLifecycleViewSql.Definition
            .Should().Contain("agreement.\"FullyExecutedAtUtc\" IS NOT NULL");
        agreement.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_LeaseAgreement_Execution"
            && constraint.Sql.Contains("\"FullyExecutedAtUtc\" IS NULL) = (\"ExecutedArtifactId\" IS NULL")
            && constraint.Sql.Contains("\"FullyExecutedAtUtc\" IS NULL OR \"IssuedAtUtc\" IS NOT NULL"));
        agreement.GetCheckConstraints().Should().Contain(constraint =>
            constraint.Name == "CK_LeaseAgreement_Issuance"
            && constraint.Sql == "(\"IssuedAtUtc\" IS NULL) = (\"IssuedArtifactId\" IS NULL)");
    }

    [Fact]
    public void Canonical_lease_and_account_views_source_only_active_portfolios()
    {
        var definitions = new[]
        {
            LeaseAgreementStatusViewSql.Definition,
            UnitOccupancyViewSql.Definition,
            LeaseManagementLifecycleViewSql.Definition,
            LeaseReconciliationExceptionViewSql.Definition,
            LeaseAddendumStatusViewSql.Definition,
            TenantChargeBalanceViewSql.Definition,
            TenantAccountBalanceViewSql.Definition,
            SecurityDepositBalanceViewSql.Definition,
        };

        foreach (var definition in definitions)
        {
            definition.Should().Contain("portfolio.\"DeletedAt\" IS NULL");
        }
    }

    [Fact]
    public void Canonical_access_views_materialize_business_date_from_active_portfolios()
    {
        RelationshipAccessProjectionSql.Create.Should().Contain("WITH effective_portfolio_dates AS MATERIALIZED");
        RelationshipAccessProjectionSql.Create.Should().Contain("portfolio.\"DeletedAt\" IS NULL");
        RelationshipAccessProjectionSql.Create.Should().NotContain("rc_business_date(c.\"PortfolioId\")");

        AccessEnvelopeViewSql.Definition.Should().Contain("WITH effective_portfolio_dates AS MATERIALIZED");
        AccessEnvelopeViewSql.Definition.Should().Contain("portfolio.\"DeletedAt\" IS NULL");
        AccessEnvelopeViewSql.Definition.Should().NotContain("rc_business_date(c.\"PortfolioId\")");
        AccessEnvelopeViewSql.Definition.Should().NotContain("rc_business_date(ec.\"PortfolioId\")");
        AccessEnvelopeViewSql.Definition.Should().Contain("owner_experience_access AS");
        AccessEnvelopeViewSql.Definition.Should().Contain("owner_entity.\"IsPrimary\"");
        AccessEnvelopeViewSql.Definition.Should().Contain("administrator_role.\"Key\" = 'workspace-administrator'");
    }

    [Fact]
    public void Morning_briefing_projection_uses_authoritative_facts_without_baking_in_today()
    {
        var definition = MorningBriefingCandidateViewSql.Definition;

        definition.Should().Contain("\"vw_tenant_charge_balances\"");
        definition.Should().Contain("\"vw_lease_management_lifecycle\"");
        definition.Should().Contain("\"vw_lease_agreement_status\"");
        definition.Should().Contain("\"WorkOrders\"");
        definition.Should().Contain("\"Appointments\"");
        definition.Should().Contain("\"Inspections\"");
        definition.Should().Contain("\"RequiredCapability\"");
        definition.Should().NotContain("\"Notifications\"");
        definition.Should().NotContain("CURRENT_DATE");
        definition.Should().NotContain("rc_business_date");
    }
}
