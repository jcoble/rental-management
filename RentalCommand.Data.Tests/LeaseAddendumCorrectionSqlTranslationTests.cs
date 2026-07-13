using FluentAssertions;
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

    private static string PrivateSql(string fieldName) =>
        (string)(typeof(AtomicLeaseMutationPersistence)
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue()
            ?? throw new InvalidOperationException($"Missing SQL constant {fieldName}."));
}
