using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Vendors;

namespace RentalCommand.IntegrationTests;

public sealed class WorkOperationAtomicExecutorParityTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime BusinessNow =
        new(2099, 8, 21, 12, 34, 56, DateTimeKind.Utc);

    [Fact]
    public void TwelveCommands_PreserveFrozenLegacyFingerprints()
    {
        var fingerprints = Commands()
            .Select(AtomicCommandFingerprint.Create)
            .ToArray();

        fingerprints.Should().Equal(
            "e6686476ea41829094b770a51e9ddf3201b32d736e425d82ae866ac9fff6a5f5",
            "6a010f0cbca4d96a539ec6c15961cd1b5b36aa5c30d52cea3df09c12cd06ca55",
            "aeedccccd78de841ba411d405c26896d19941a0f19e54bc9f2938f798ee5156c",
            "07685ab86f67658ad0016bc23d3b70413e1319e3f61b9234e2898cf8a9e2a2cf",
            "057b1f805911f6378989454abdb2e03ad929c2673d42b7985f514286bd708680",
            "34d486072b199043ee926fe706f01a0296d9e534f70d2ff3033bd88943efdf9a",
            "303ec46d2f06372e975bae8fca69c742e8235fdef685b85a7634da588e26d80c",
            "a72af2d9646fcc59e9d1f241b32131e1eef251dc33e3fd8efa10629868b5d497",
            "374c0842199103012395def37af397ef242781f81d341fb943ff9863393c82c7",
            "78b47b58e15efaf87310636949420f5ae0a46b135cd4b741560e5a14450f68c1",
            "1d3936f648ca0d675c6817564632a004e030376092a72d94528319f8d32c90c3",
            "65ad689e7363932df8786ac91f3d47f7815fb742f20d8b602b99a43aa8c014a6");
    }

    [Fact]
    public void TwelveWrites_PreserveOperationsContractsAndLockPlans()
    {
        using var db = new RentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().Options);
        var guard = new WorkOrderResponsibilityAccessRevisionGuard();

        AssertWrite(RequestVendorW9Rule.Write(W9(), db),
            "vendor-w9.request", "vendor-w9.request.result.v1", null);
        AssertWrite(AssignWorkOrderResponsibilityRule.Write(Assign(), db, guard),
            "work-order-responsibility.assign", "work-order-responsibility.assign.v1",
            WriteLockProtocol.WorkOrderResponsibility);
        AssertWrite(CompleteVendorDispatchFromInboundRule.Write(Inbound(), db),
            "sms.vendor-done", "complete-vendor-dispatch-from-inbound-result.v1",
            WriteLockProtocol.VendorDispatchInbound, "VendorDispatch");
        AssertWrite(CancelVendorDispatchRule.Write(Cancel(), db),
            "vendor-dispatch.cancel", "vendor-dispatch.cancel.v1",
            WriteLockProtocol.WorkOrder, "WorkOrder");
        AssertWrite(CloseWorkOrderResponsibilityRule.Write(Close(), db, guard),
            "work-order-responsibility.close", "work-order-responsibility.close.v1",
            WriteLockProtocol.WorkOrderResponsibility);
        AssertWrite(UpdateAssignedWorkOrderRule.Write(Update(), db),
            "assigned-work-order.update", "assigned-work-order.update.v1",
            WriteLockProtocol.WorkspaceAccessContextWorkOrder,
            "WorkspaceAccessContext", "WorkOrder");
        AssertWrite(RecordTechnicianWorkEntryRule.Write(Entry(), db),
            "technician-work-entry.record", "technician-work-entry.v1",
            WriteLockProtocol.WorkOrder, "WorkOrder");
        AssertWrite(SendTechnicianAssignmentMessageRule.Write(Message(), db),
            "technician-assignment-message.send", "technician-assignment-message.v1",
            WriteLockProtocol.WorkOrder, "WorkOrder");
        AssertWrite(MarkTechnicianAssignmentConversationReadRule.Write(Read(), db),
            "technician-assignment-conversation.read", "technician-assignment-conversation-read.v1",
            WriteLockProtocol.WorkOrder, "WorkOrder");
        AssertWrite(RecoverVendorDispatchChronologyRule.Write(Recovery(), db),
            "vendor-dispatch.recover-chronology", "vendor-dispatch.chronology-recovery.v1", null);
        AssertWrite(DispatchWorkOrderToVendorRule.Write(Dispatch(), db),
            "vendor-dispatch.create", "vendor-dispatch.create.v1",
            WriteLockProtocol.WorkOrder, "WorkOrder");
        AssertWrite(CreateVendorRatingRule.Write(Rating(), db),
            "vendor-rating.create", "vendor-rating.create.v1",
            WriteLockProtocol.WorkOrderVendor, "WorkOrder", "Vendor");
    }

    private static void AssertWrite<TCommand, TResult>(
        TransactionalWrite<TCommand, TResult> write,
        string operation,
        string resultContract,
        WriteLockProtocol? protocol,
        params string[] lockNamespaces)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(resultContract);
        write.LockPlan.Protocol.Should().Be(protocol);
        write.LockPlan.Locks.Select(item => item.LockNamespace).Should().Equal(lockNamespaces);
    }

    private static IAtomicCommandData[] Commands() =>
    [
        W9(), Assign(), Inbound(), Cancel(), Close(), Update(), Entry(), Message(), Read(),
        Recovery(), Dispatch(), Rating(),
    ];

    private static RequestVendorW9Command W9() =>
        new(1, 2, "w9-client-operation", 3, SessionId, 3, 4, 5, BusinessNow);

    private static AssignWorkOrderResponsibilityCommand Assign() =>
        new(1, 3, SessionId, 4, 5, 6, 7, 8, WorkOrderResponsibilityKind.Primary,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            [new WorkspaceAccessRevisionExpectation(9, 10)], "Frozen assignment", BusinessNow,
            "assign-delivery");

    private static CompleteVendorDispatchFromInboundCommand Inbound() =>
        new("provider-event-42", "+15550102020", true, BusinessNow);

    private static CancelVendorDispatchCommand Cancel() =>
        new(1, 6, 11, 3, "Frozen cancellation", BusinessNow, "cancel-delivery", Access());

    private static CloseWorkOrderResponsibilityCommand Close() =>
        new(1, 3, SessionId, 4, 5, 6,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            [new WorkspaceAccessRevisionExpectation(9, 10)], "Frozen close", BusinessNow,
            "close-delivery");

    private static UpdateAssignedWorkOrderCommand Update() =>
        new(1, 3, SessionId, 4, 5, 6, BusinessNow.AddMinutes(-1), WorkOrderStatus.InProgress,
            "Frozen technician note", BusinessNow.AddDays(1), BusinessNow.AddDays(1).AddHours(2),
            null, BusinessNow, "update-delivery");

    private static RecordTechnicianWorkEntryCommand Entry() =>
        new(1, 3, SessionId, 4, 5, 6, TechnicianWorkEntryKind.Time,
            "Frozen labor", 2.5m, "hours", 12, BusinessNow.AddHours(-1), "entry-delivery");

    private static SendTechnicianAssignmentMessageCommand Message() =>
        new(1, 3, SessionId, 4, 5, 6, "Frozen assignment message", "message-delivery");

    private static MarkTechnicianAssignmentConversationReadCommand Read() =>
        new(1, 3, SessionId, 4, 5, 6, "read-delivery");

    private static RecoverVendorDispatchChronologyCommand Recovery() =>
        new(1, 6, 11, BusinessNow.AddHours(2), 12, 13, "vendor-dispatch:legacy",
            "1:6:dispatch-delivery", BusinessNow, 3, Access(), "recovery-delivery");

    private static DispatchWorkOrderToVendorCommand Dispatch() =>
        new(1, 6, 2, "+15550102020", "Frozen dispatch", 3, BusinessNow, Access());

    private static CreateVendorRatingCommand Rating() =>
        new(1, new StaffOperationActor(3, SessionId, 4, 5), 2, 6, 5,
            "Frozen rating", BusinessNow, "rating-delivery");

    private static DispatchManagementAccess Access() =>
        new(SessionId, 3, 4, 5);
}
