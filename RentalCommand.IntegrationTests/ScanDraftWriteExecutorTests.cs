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
    public void FinalizeAndVoiceCreate_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(Finalize()).Should().Be(
            "a72bc8078cff8d70919e059cb86f8fecdedf5d5aa26a7b27173a202467affd72");
        AtomicCommandFingerprint.Create(VoiceCreate()).Should().Be(
            "80fa35fc496f748bad0196a893e79703797fd06fc6a62887bba4048d8ccc5f7b");
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
    }

    [Fact]
    public async Task AllLegacyHandlerArmsThrow()
    {
        await AssertRetiredAsync<FinalizeScanUploadCommand, FinalizeScanUploadResult>(
            new FinalizeScanUploadHandler(), Finalize());
        await AssertRetiredAsync<RetryScanDraftCommand, ScanDraftMutationResult>(
            new RetryScanDraftHandler(), Retry());
        await AssertRetiredAsync<CreateVoiceScanDraftCommand, ScanDraftMutationResult>(
            new CreateVoiceScanDraftHandler(), VoiceCreate());
        await AssertRetiredAsync<AnswerVoiceScanDraftCommand, ScanDraftMutationResult>(
            new AnswerVoiceScanDraftHandler(), VoiceAnswer());
        await AssertRetiredAsync<SetScanDraftPaymentAccountCommand, ScanDraftMutationResult>(
            new SetScanDraftPaymentAccountHandler(), PaymentAccount());
        await AssertRetiredAsync<RejectScanDraftCommand, RejectScanDraftResult>(
            new RejectScanDraftHandler(), Reject());
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

    private static async Task AssertRetiredAsync<TCommand, TResult>(
        IAtomicCommandHandler<TCommand, TResult> handler,
        TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await ((Func<Task>)(() => handler.HandleAsync(command, null!, default)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scan upload and draft mutations no longer use the legacy atomic handlers.");
        await ((Func<Task>)(() => handler.AuthorizeReplayAsync(command, null!, default)))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Scan upload and draft mutations no longer use the legacy atomic handlers.");
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
}
