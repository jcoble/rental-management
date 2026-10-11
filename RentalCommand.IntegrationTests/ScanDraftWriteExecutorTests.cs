using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Scanning;

namespace RentalCommand.IntegrationTests;

public sealed class ScanDraftWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void FinalizeVoiceCreateConfirmAndManualLease_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(Finalize()).Should().Be(
            "a72bc8078cff8d70919e059cb86f8fecdedf5d5aa26a7b27173a202467affd72");
        AtomicCommandFingerprint.Create(VoiceCreate()).Should().Be(
            "80fa35fc496f748bad0196a893e79703797fd06fc6a62887bba4048d8ccc5f7b");
        AtomicCommandFingerprint.Create(Confirm()).Should().Be(
            "64f20366556cedc35a96853644b94c2e66be9b1bb50c411c7b7c1a068463d4c3");
        AtomicCommandFingerprint.Create(ManualLease()).Should().Be(
            "0e4f1ee3a6feb5120b4b62c964203c5af93fb6e4a817f0669e27710125dbc56b");
    }

    [Fact]
    public void SixWrites_PreserveLegacyLockPlansAndResultContracts()
    {
        AssertPlan(Write(Finalize(), ScanDraftWriteSupport.FinalizeResultContract),
            WriteLockProtocol.Portfolio, "Portfolio");
        AssertPlan(Write(Retry(), ScanDraftWriteSupport.MutationResultContract),
            WriteLockProtocol.AuthorizationScopeScanDraft,
            "AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft");
        AssertPlan(Write(VoiceCreate(), ScanDraftWriteSupport.MutationResultContract),
            WriteLockProtocol.AuthorizationScope,
            "AuthSession", "WorkspaceAccessContext", "Portfolio");
        AssertPlan(Write(VoiceAnswer(), ScanDraftWriteSupport.MutationResultContract),
            WriteLockProtocol.AuthorizationScopeScanDraft,
            "AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft");
        AssertPlan(Write(PaymentAccount(), ScanDraftWriteSupport.MutationResultContract),
            WriteLockProtocol.AuthorizationScopeScanDraft,
            "AuthSession", "WorkspaceAccessContext", "Portfolio", "ScanDraft");

        var reject = Write(Reject(), ScanDraftWriteSupport.RejectResultContract);
        reject.LockPlan.Locks.Should().BeEmpty(
            "RejectAuthorizedAsync must remain the sole ScanDraft lock acquisition");
        reject.LockPlan.Protocol.Should().BeNull();

        var confirm = Write(Confirm(), ScanDraftWriteSupport.ConfirmResultContract);
        confirm.LockPlan.Locks.Should().BeEmpty(
            "AtomicScanConfirmationPersistence must remain the sole ScanDraft lock owner");
        confirm.LockPlan.Protocol.Should().BeNull();
        AssertPlan(Write(ManualLease(), ScanDraftWriteSupport.ConfirmResultContract),
            WriteLockProtocol.AuthorizationScope,
            "AuthSession", "WorkspaceAccessContext", "Portfolio");
    }

    private static TransactionalWrite<TCommand, bool> Write<TCommand>(
        TCommand command, string resultContract)
        where TCommand : notnull, IAtomicCommandData =>
        ScanDraftWriteSupport.Write(
            "test.operation", command, resultContract,
            (_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.CompletedTask);

    private static void AssertPlan<TCommand>(
        TransactionalWrite<TCommand, bool> write,
        WriteLockProtocol protocol,
        params string[] namespaces)
        where TCommand : notnull, IAtomicCommandData
    {
        write.ResultContract.Should().NotBeNullOrWhiteSpace();
        write.LockPlan.Protocol.Should().Be(protocol);
        write.LockPlan.Locks.Select(item => item.LockNamespace).Should().Equal(namespaces);
    }

    private static FinalizeScanUploadCommand Finalize() => new(
        1, 7, SessionId, 9, 3, "frozen-upload", "request-fingerprint", "Payment",
        true, "Frozen batch", new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        [new FinalizeScanUploadFile(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "source/path", "source.pdf",
            "application/pdf", 1234, new string('a', 64),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), "thumbnail/path",
            "source-preview.jpg", 321, new string('b', 64))],
        new ScanCaptureContextData(
            WorkspaceExperience.Management, 9, 3, 10, 11, 12, 13, 14, 15, 16, 17, 18,
            "Frozen source"));

    private static RetryScanDraftCommand Retry() =>
        new(1, 42, 7, SessionId, 9, 3, "retry-delivery");

    private static CreateVoiceScanDraftCommand VoiceCreate() => new(
        1, 7, SessionId, 9, 3, "Expense", "{\"amount\":12}", "voice-model", 25, 10,
        "voice/path.m4a", "voice.m4a", "audio/mp4", 123, new string('c', 64),
        "voice-delivery");

    private static AnswerVoiceScanDraftCommand VoiceAnswer() => new(
        1, 42, 7, SessionId, 9, 3, "{\"amount\":null}", "Expense",
        "{\"amount\":12}", "voice-model", 30, 10, "answer-delivery");

    private static SetScanDraftPaymentAccountCommand PaymentAccount() =>
        new(1, 42, 7, SessionId, 9, 3, 19, "payment-delivery");

    private static RejectScanDraftCommand Reject() =>
        new(1, 42, 7, new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
            SessionId, 9, 3, "Not a receipt");

    private static ConfirmScanDraftCommand Confirm() => new(
        1, 42, 7, new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        new string('d', 64),
        new ScanConfirmationTargetData(
            ScanConfirmationTargetKind.Expense,
            Expense: new ScanExpenseTargetData(Receipt(), true, 10, 11, null)),
        SourceStoredFileId: 12,
        AuthSessionId: SessionId,
        AccessContextId: 9,
        ExpectedAccessRevision: 3,
        DeliveryIdempotencyKey: "confirm-delivery",
        SourceContentSha256: new string('e', 64),
        SourceLabel: "Frozen source",
        CaptureContext: new ScanCaptureContextData(
            WorkspaceExperience.Management, 9, 3, 10, 11, null, null, null, null, null,
            null, null, "Frozen source"));

    private static CreateManualLeaseCommand ManualLease() => new(
        1, 7, SessionId, 9, 3,
        new ScanLeaseTargetData(
            10, 11, 12, "Frozen Tenant", "tenant@example.test", "555-0100", null,
            "Frozen Home", "SingleFamily", RentalStructure.SingleRental,
            "10 Main Street", "Akron", "OH", "44308", "1", 2m, 1m, 900,
            "LEASE-42", new DateTime(2099, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2100, 8, 31, 0, 0, 0, DateTimeKind.Utc), 1_450m, 1_450m,
            75m, 1, LeaseScanReviewDisposition.NeedsSignatures,
            TermsPayload: "{}", RentTrackingStartMode: RentTrackingStartMode.ForwardOnly),
        "manual-delivery");

    private static ScanReceiptData Receipt() => new(
        "Frozen Vendor", null, null, null, null, "R-42",
        new DateTime(2099, 8, 19, 0, 0, 0, DateTimeKind.Utc),
        100m, 8m, 8m, null, null, null, 108m, "Card", "4242",
        ScheduleECategory.Repairs, "Receipt", null, null, [], null, null, null, []);
}
