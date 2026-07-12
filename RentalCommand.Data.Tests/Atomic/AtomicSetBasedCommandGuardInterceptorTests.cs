using FluentAssertions;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Tests.Atomic;

public sealed class AtomicSetBasedCommandGuardInterceptorTests
{
    [Fact]
    public void ClassifyRawDmlTargets_finds_every_table_and_operation_in_a_cte_batch()
    {
        const string sql = """
            WITH changed AS (
                UPDATE "LeaseManagementParties" SET "ChangeReason" = 'x' RETURNING "Id"
            ), revoked AS (
                UPDATE "TenantUserAccesses" SET "Reason" = 'x' RETURNING "Id"
            )
            INSERT INTO "UnitOperationalPeriods" ("Reason") SELECT 'x' FROM changed
            """;

        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDmlTargets(sql)
            .Should().BeEquivalentTo(
            [
                new AtomicRawDmlTarget("LeaseManagementParties", AtomicRawDmlOperation.Update),
                new AtomicRawDmlTarget("TenantUserAccesses", AtomicRawDmlOperation.Update),
                new AtomicRawDmlTarget("UnitOperationalPeriods", AtomicRawDmlOperation.Insert),
            ]);
    }

    [Fact]
    public void For_update_row_lock_is_not_classified_as_raw_update_dml()
    {
        const string sql = """
            SELECT upload.*
            FROM "PendingFileUploads" AS upload
            WHERE upload."Id" = @id
            FOR UPDATE OF upload SKIP LOCKED
            """;

        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDml(sql).Should().BeNull();
    }

    [Fact]
    public void Actual_dml_after_for_update_is_classified_by_its_operation()
    {
        const string sql = """
            WITH claim AS (
                SELECT connection."Id"
                FROM "AccountingConnections" AS connection
                FOR UPDATE
            ), mappings AS (
                INSERT INTO "AccountingEntityMappings" ("Id")
                SELECT "Id" FROM claim
                RETURNING 1
            )
            SELECT count(*) FROM mappings
            """;

        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDml(sql)
            .Should().Be(AtomicRawDmlOperation.Insert);
    }

    [Theory]
    [InlineData("INSERT INTO widgets (id) VALUES (1)", (int)AtomicRawDmlOperation.Insert)]
    [InlineData("UPDATE widgets SET id = 2", (int)AtomicRawDmlOperation.Update)]
    [InlineData("DELETE FROM widgets WHERE id = 2", (int)AtomicRawDmlOperation.Delete)]
    public void Actual_raw_dml_remains_classified(
        string sql,
        int expected)
    {
        AtomicSetBasedCommandGuardInterceptor.ClassifyRawDml(sql)
            .Should().Be((AtomicRawDmlOperation)expected);
    }
}
