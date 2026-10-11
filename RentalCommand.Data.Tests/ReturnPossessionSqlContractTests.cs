using FluentAssertions;
using RentalCommand.Data.Leasing;

namespace RentalCommand.Data.Tests;

public sealed class ReturnPossessionSqlContractTests
{
    [Fact]
    public void Return_possession_derives_one_authoritative_business_date_inside_the_atomic_statement()
    {
        var sql = ReturnPossessionSql();

        sql.Should().Contain("business_clock AS MATERIALIZED");
        sql.Split("rc_business_date(@portfolioId)", StringSplitOptions.None)
            .Should().HaveCount(2, "the business-date function must appear exactly once");
        sql.Should().NotContain("@businessDate");
        sql.Should().NotContain("business_date_matches");
        sql.Should().Contain("party.\"EffectiveFrom\" <= business_clock.business_date");
        sql.Should().Contain("party.\"EffectiveThrough\" >= business_clock.business_date");
        sql.Should().Contain("SET \"EffectiveThrough\" = business_clock.business_date");
    }

    private static string ReturnPossessionSql() =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField("ReturnPossessionSql", System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException("Missing return-possession SQL contract."));
}
