using System.Text.RegularExpressions;
using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Operations;

namespace RentalCommand.Data.Tests.Operations;

public sealed class WorkOperationAtomicContractTests
{
    [Fact]
    public void Receipt_codec_preserves_the_original_response_snapshot()
    {
        var codec = new AtomicJsonResultCodec<WorkOrderMutationResult>("work-order.mutation.v2");
        var expected = new WorkOrderMutationResult(
            OperationMutationOutcome.Applied,
            47,
            new WorkOrderMutationSnapshot(
                47, 1, 2, null, null, null, null, null, "Original", "Description",
                null, null, null, null, null, null, null, null, null, null, null, null,
                "General", WorkOrderPriority.Normal, WorkOrderStatus.New,
                new DateTime(2027, 1, 21, 5, 0, 0, DateTimeKind.Utc),
                null, null, null, null, null, "Staff",
                new DateTime(2027, 1, 21, 5, 0, 0, DateTimeKind.Utc),
                "Property", null, null, null));

        codec.Deserialize(codec.Serialize(expected)).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Vendor_rating_receipt_codec_preserves_the_original_response_snapshot()
    {
        var codec = new AtomicJsonResultCodec<VendorRatingMutationResult>("vendor-rating.create.v1");
        var expected = new VendorRatingMutationResult(
            OperationMutationOutcome.Applied, 83, 12, "{\"Id\":83,\"Stars\":5}");

        codec.Deserialize(codec.Serialize(expected)).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Vendor_dispatch_chronology_recovery_uses_one_declared_atomic_sql_mutation()
    {
        var source = File.ReadAllText(
            Path.Combine(
                AppContext.BaseDirectory,
                "../../../../RentalCommand.Data/Operations/" +
                "RecoverVendorDispatchChronologyRule.cs"));
        var compactSource = Regex.Replace(source, @"\s+", " ");

        source.Should().Contain(
            "_db.ExecuteAtomicSqlMutationAsync<ChronologyRecoveryMutation>(context");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"AtomicAuditLogs\", AtomicSqlMutationOperation.Insert)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"AtomicAuditLogs\", AtomicSqlMutationOperation.Update)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"VendorDispatches\", AtomicSqlMutationOperation.Update)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"WorkOrderStatusEvents\", AtomicSqlMutationOperation.Update)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"WorkOrders\", AtomicSqlMutationOperation.Update)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"OutboxMessages\", AtomicSqlMutationOperation.Insert)");
        compactSource.Should().Contain(
            "new AtomicSqlMutationTarget( \"OutboxMessages\", AtomicSqlMutationOperation.Update)");
        source.Should().Contain("UPDATE \"AtomicAuditLogs\" AS audit");
        source.Should().Contain("UPDATE \"VendorDispatches\" AS dispatch");
        source.Should().Contain("UPDATE \"WorkOrderStatusEvents\" AS status_event");
        source.Should().Contain("UPDATE \"WorkOrders\" AS work_order");
        source.Should().Contain("UPDATE \"OutboxMessages\" AS outbox");
        source.Should().Contain("INSERT INTO \"AtomicAuditLogs\"");
        source.Should().Contain("INSERT INTO \"OutboxMessages\"");
    }

}
