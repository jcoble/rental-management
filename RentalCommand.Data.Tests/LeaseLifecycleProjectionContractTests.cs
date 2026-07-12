using FluentAssertions;
using RentalCommand.Data;

namespace RentalCommand.Data.Tests;

public sealed class LeaseLifecycleProjectionContractTests
{
    [Fact]
    public void Effective_clock_uses_one_consistent_statement_instant_for_read_models()
    {
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().Contain("statement_timestamp()");
        LeaseEffectiveClockSql.CreateEffectiveNowUtc.Should().NotContain("clock_timestamp()");
    }

    [Fact]
    public void Governing_agreement_is_derived_only_from_executed_nonvoid_facts()
    {
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"FullyExecutedAtUtc\" IS NOT NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"VoidedAtUtc\" IS NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("agreement.\"DraftCanceledAtUtc\" IS NULL");
        LeaseAgreementStatusViewSql.Definition.Should().Contain("AS \"IsGoverning\"");
    }
}
