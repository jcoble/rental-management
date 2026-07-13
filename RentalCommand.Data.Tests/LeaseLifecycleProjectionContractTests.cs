using FluentAssertions;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Data.Tests;

public sealed class LeaseLifecycleProjectionContractTests
{
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
    }
}
