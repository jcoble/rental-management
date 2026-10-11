using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Data;
using RentalCommand.Data.Esign;

namespace RentalCommand.IntegrationTests;

public sealed class NativeEsignWriteDescriptorTests
{
    private readonly RentalCommandDbContext _db = new(
        new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    [Fact]
    public void AllNineOperations_PreserveLegacyContractsAndLockPrefixes()
    {
        var agreement = Describe<IssueLeaseAgreementCommand, IssueLeaseAgreementResult>(Agreement());
        var addendum = Describe<IssueLeaseAddendumCommand, IssueLeaseAddendumResult>(Addendum());
        var view = Describe<RecordNativeEsignViewCommand, RecordNativeEsignViewResult>(
            new("token-hash", "127.0.0.1", "agent", DateTime.UnixEpoch));
        var sign = Describe<RecordNativeSignatureCommand, NativeSignerActionResult>(
            new("token-hash", SignatureSignatureType.Typed, "Tenant", null, null, null, null,
                "127.0.0.1", "agent", DateTime.UnixEpoch));
        var decline = Describe<RecordNativeDeclineCommand, NativeSignerActionResult>(
            new("token-hash", "reason", "127.0.0.1", "agent", DateTime.UnixEpoch));
        var finalize = Describe<FinalizeNativeEsignRequestCommand, FinalizeNativeEsignRequestResult>(
            new(Guid.NewGuid(), "fingerprint", 31, Guid.NewGuid(), Guid.NewGuid(),
                "storage", "executed.pdf", 42, "sha256"));
        var resendAgreement = Describe<ResendNativeEsignInvitationCommand, ResendNativeEsignInvitationResult>(
            Resend(NativeEsignInvitationParentKind.LeaseAgreement));
        var resendAddendum = Describe<ResendNativeEsignInvitationCommand, ResendNativeEsignInvitationResult>(
            Resend(NativeEsignInvitationParentKind.LeaseAddendum));
        var reconcile = Describe<ReconcileNativeEsignAgreementFinancialsCommand,
            ReconcileNativeEsignAgreementFinancialsResult>(new(31, Guid.NewGuid()));
        var batch = Describe<ReconcileNativeEsignAgreementFinancialsBatchCommand,
            ReconcileNativeEsignAgreementFinancialsBatchResult>(new(Guid.NewGuid(), 20));

        agreement.Should().BeEquivalentTo(new Descriptor("lease-agreement.issue", "lease-agreement.issue.v1",
            WriteLockProtocol.NativeEsignLeaseManagement, ["LeaseManagement"], []));
        addendum.Should().BeEquivalentTo(new Descriptor("lease-addendum.issue", "lease-addendum.issue.v1",
            WriteLockProtocol.NativeEsignLeaseManagement, ["LeaseManagement"], []));
        view.Should().BeEquivalentTo(new Descriptor("native-esign.view", "native-esign.view.v1", null, [], []));
        sign.Should().BeEquivalentTo(new Descriptor("native-esign.sign", "native-esign.sign.v1", null, [], []));
        decline.Should().BeEquivalentTo(new Descriptor("native-esign.decline", "native-esign.decline.v1", null, [], []));
        finalize.Should().BeEquivalentTo(new Descriptor("native-esign.finalize", "native-esign.finalize.v1",
            WriteLockProtocol.NativeEsignRequest, ["SignatureRequest"], ["LeaseManagement"]));
        resendAgreement.Should().BeEquivalentTo(new Descriptor("lease-agreement.esign-invitation.resend",
            "native-esign.invitation-resend.v1", WriteLockProtocol.NativeEsignLeaseManagement,
            ["LeaseManagement"], []));
        resendAddendum.Should().BeEquivalentTo(new Descriptor("lease-addendum.esign-invitation.resend",
            "native-esign.invitation-resend.v1", WriteLockProtocol.NativeEsignLeaseManagement,
            ["LeaseManagement"], []));
        reconcile.Should().BeEquivalentTo(new Descriptor("native-esign.agreement-financials.reconcile",
            "native-esign.agreement-financials.reconcile.v1", WriteLockProtocol.NativeEsignRequest,
            ["SignatureRequest"], ["LeaseManagement"]));
        batch.Should().BeEquivalentTo(new Descriptor("native-esign.agreement-financials.batch-reconcile",
            "native-esign.agreement-financials.batch-reconcile.v1", null, [], []));
    }

    [Fact]
    public void ResendFingerprint_StillExcludesOnlyLegacyReplayAuthorizationFields()
    {
        typeof(ResendNativeEsignInvitationCommand).GetProperties()
            .Where(property => property.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() is not null)
            .Select(property => property.Name)
            .Should().Equal("AuthSessionId", "AccessContextId", "ExpectedAccessRevision",
                "DeliveryIdempotencyKey");

        var otherCommands = new[]
        {
            typeof(IssueLeaseAgreementCommand), typeof(IssueLeaseAddendumCommand),
            typeof(RecordNativeEsignViewCommand), typeof(RecordNativeSignatureCommand),
            typeof(RecordNativeDeclineCommand), typeof(FinalizeNativeEsignRequestCommand),
            typeof(ReconcileNativeEsignAgreementFinancialsCommand),
            typeof(ReconcileNativeEsignAgreementFinancialsBatchCommand),
        };
        otherCommands.SelectMany(type => type.GetProperties())
            .Should().NotContain(property =>
                property.GetCustomAttribute<AtomicFingerprintIgnoreAttribute>() != null);
    }

    private Descriptor Describe<TCommand, TResult>(TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        var write = NativeEsignWriteSupport.Write<TCommand, TResult>(_db, command);
        return new(write.OperationName, write.ResultContract, write.LockPlan.Protocol,
            write.LockPlan.Locks.Select(item => item.LockNamespace).ToArray(),
            write.LockPlan.DeferredLockNamespaces.ToArray());
    }

    private static IssueLeaseAgreementCommand Agreement() => new(
        Guid.NewGuid(), 7, new string('f', 64), 11, 12, 13, 1, "delivery", "Agreement",
        "storage", "agreement.pdf", 42, new string('a', 64), "https://example.test",
        [new NativeEsignSignerCommand(14)], 15, Guid.NewGuid(), 16, 17);

    private static IssueLeaseAddendumCommand Addendum() => new(
        Guid.NewGuid(), 7, new string('f', 64), 11, 12, 13, 1, "delivery", "Addendum",
        "storage", "addendum.pdf", 42, new string('a', 64), "https://example.test",
        [new NativeEsignAddendumSignerCommand(14)], 15, Guid.NewGuid(), 16, 17);

    private static ResendNativeEsignInvitationCommand Resend(
        NativeEsignInvitationParentKind kind) => new(
        11, 12, kind, 13, 14, 15, Guid.NewGuid(), 16, 17, "delivery");

    private sealed record Descriptor(
        string Operation,
        string Contract,
        WriteLockProtocol? Protocol,
        string[] Locks,
        string[] DeferredLocks);
}
