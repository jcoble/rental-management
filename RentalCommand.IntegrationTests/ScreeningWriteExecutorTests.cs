using FluentAssertions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Screening;
using RentalCommand.Data.Screening;

namespace RentalCommand.IntegrationTests;

public sealed class ScreeningWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task EightMigratedRules_ExecuteThroughRecorderWithLegacyLockSequences()
    {
        await AssertStaffRuleAsync(Track(), "screening.external.create",
            ScreeningWriteSupport.MutationResultContract);
        await AssertStaffRuleAsync(PrepareIntegrated(), "screening.integrated.prepare",
            ScreeningWriteSupport.IntegratedPrepareResultContract);
        await AssertStaffRuleAsync(FinalizeIntegrated(), "screening.integrated.finalize",
            ScreeningWriteSupport.MutationResultContract);
        await AssertStaffRuleAsync(Update(), "screening.external.update",
            ScreeningWriteSupport.MutationResultContract);
        await AssertStaffRuleAsync(Decision(), "screening.decision",
            ScreeningWriteSupport.MutationResultContract);
        await AssertStaffRuleAsync(PrepareAdverse(), "adverse-action.prepare",
            ScreeningWriteSupport.AdversePrepareResultContract);
        await AssertStaffRuleAsync(FinalizeAdverse(), "adverse-action.finalize",
            ScreeningWriteSupport.AdverseFinalizeResultContract);

        var providerExecuted = false;
        var providerWrite = ScreeningWriteSupport.Write(
            ProviderDelivery(),
            (_, _, _) =>
            {
                providerExecuted = true;
                return Task.FromResult(true);
            },
            (_, _, _) => Task.CompletedTask);
        var providerRecorder = new RecordingRuleExecutor();
        await providerRecorder.ExecuteAsync("provider-key", providerWrite);

        providerExecuted.Should().BeTrue();
        providerWrite.OperationName.Should().Be("screening.provider-delivery");
        providerWrite.ResultContract.Should().Be(ScreeningWriteSupport.MutationResultContract);
        providerWrite.LockPlan.Locks.Should().BeEmpty(
            "the provider rule resolves its RentalApplication target before acquiring its conditional lock");
        providerRecorder.Acquired.Should().BeEmpty();
    }

    [Fact]
    public void Fingerprints_PreserveEveryLegacyIgnoredFieldAndDurableProviderFileIdentity()
    {
        var track = Track();
        AtomicCommandFingerprint.Create(track with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 19,
            ExpectedAccessRevision = 20, DeliveryIdempotencyKey = "changed",
        }).Should().Be(AtomicCommandFingerprint.Create(track));

        var integrated = FinalizeIntegrated();
        AtomicCommandFingerprint.Create(integrated with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 19,
            ExpectedAccessRevision = 20, DeliveryIdempotencyKey = "changed",
        }).Should().Be(AtomicCommandFingerprint.Create(integrated));
        AtomicCommandFingerprint.Create(integrated with { ProviderReference = "different" })
            .Should().NotBe(AtomicCommandFingerprint.Create(integrated));

        var adverse = FinalizeAdverse();
        AtomicCommandFingerprint.Create(adverse with
        {
            AuthSessionId = Guid.NewGuid(), AccessContextId = 19,
            ExpectedAccessRevision = 20, DeliveryIdempotencyKey = "changed",
            GeneratedAtUtc = adverse.GeneratedAtUtc.AddDays(1),
        }).Should().Be(AtomicCommandFingerprint.Create(adverse));
        AtomicCommandFingerprint.Create(adverse with { PendingUploadId = Guid.NewGuid() })
            .Should().NotBe(AtomicCommandFingerprint.Create(adverse));
        AtomicCommandFingerprint.Create(adverse with { RequestFingerprint = "different" })
            .Should().NotBe(AtomicCommandFingerprint.Create(adverse));

        var provider = ProviderDelivery();
        AtomicCommandFingerprint.Create(provider with { DeliveryIdempotencyKey = "changed" })
            .Should().Be(AtomicCommandFingerprint.Create(provider));
        AtomicCommandFingerprint.Create(provider with { DeliveryId = "different" })
            .Should().NotBe(AtomicCommandFingerprint.Create(provider));
    }

    [Fact]
    public async Task ScreeningService_ComposesLegacyOperationAndExactCallerKey()
    {
        var executor = new CapturingRequestExecutor();
        var service = new ScreeningService(
            null!, null!, null!, null!, executor, null!, TimeProvider.System);
        var scope = new RentalCommand.Core.Authorization.WorkspaceReadScope(
            7, 8, SessionId, 9, 10);

        await service.TrackExternalAsync(scope, 42, new TrackExternalScreeningRequest
        {
            OperationKey = " caller-delivery-key ",
            ProviderDisplayName = "Recorder",
            Status = ApplicantScreeningStatus.Created,
        });

        executor.OperationName.Should().Be("screening.external.create");
        executor.IdempotencyKey.Should().Be(
            "7:42:9c9bb8898597fff3ed5273de798ec455c0bac0c6413b200793748f11a473b867");
    }

    private static async Task AssertStaffRuleAsync<TCommand>(
        TCommand command, string operation, string contract)
        where TCommand : notnull, IAtomicCommandData
    {
        var executed = false;
        var write = ScreeningWriteSupport.Write(
            command,
            (_, _, _) =>
            {
                executed = true;
                return Task.FromResult(true);
            },
            (_, _, _) => Task.CompletedTask);
        var recorder = new RecordingRuleExecutor();

        await recorder.ExecuteAsync("key", write);

        executed.Should().BeTrue();
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);
        write.LockPlan.Protocol.Should().Be(WriteLockProtocol.AuthorizationScopeRentalApplication);
        recorder.Acquired.Should().Equal(
            $"AuthSession:{SessionId}", "WorkspaceAccessContext:9", "Portfolio:7", "RentalApplication:42");
    }

    private static TrackExternalScreeningCommand Track() => new(
        7, 42, 8, SessionId, 9, 10, "track", "External", null, null, null, null, null,
        ApplicantScreeningStatus.Created, "track-delivery");

    private static PrepareIntegratedScreeningCommand PrepareIntegrated() => new(
        7, 42, 8, SessionId, 9, 10, "integrated", "provider", "Provider", "prepare-delivery");

    private static FinalizeIntegratedScreeningCommand FinalizeIntegrated() => new(
        7, 42, 43, 8, SessionId, 9, 10, "integrated", "provider", true,
        "provider-reference", "https://provider.test", new DateTime(2099, 1, 1), null,
        "CRA", "Address", "Phone", "finalize-delivery");

    private static UpdateExternalScreeningCommand Update() => new(
        7, 42, 43, 8, SessionId, 9, 10, "update", ApplicantScreeningStatus.Completed,
        "reference", null, "CRA", "Address", "Phone", new DateTime(2099, 1, 2), "update-delivery");

    private static RecordScreeningDecisionCommand Decision() => new(
        7, 42, 43, 8, SessionId, 9, 10, "decision", ScreeningDecision.Decline,
        "Reason", true, "decision-delivery");

    private static PrepareAdverseActionNoticeCommand PrepareAdverse() => new(
        7, 42, 8, SessionId, 9, 10, "adverse", "Reason", true);

    private static CreateAdverseActionNoticeCommand FinalizeAdverse() => new(
        7, 42, 43, new DateTime(2099, 1, 3), "decision-fingerprint", 8, SessionId, 9, 10,
        "Reason", "CRA", Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "adverse-action-pdf", "operation-hash", "request-fingerprint", "path/file.pdf",
        "file.pdf", "application/pdf", 123, true, "adverse-delivery", new DateTime(2099, 1, 4));

    private static ApplyScreeningProviderDeliveryCommand ProviderDelivery() => new(
        "provider", "delivery", "reference", "completed", ApplicantScreeningStatus.Completed,
        new DateTime(2099, 1, 5), null, "CRA", "Address", "Phone", "provider-delivery");

    private sealed class RecordingRuleExecutor : IWriteExecutor
    {
        public List<string> Acquired { get; } = [];

        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            var context = new Mock<IAtomicCommandContext>();
            context.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>((name, id, _) => Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            context.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid, CancellationToken>((name, id, _) => Acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            foreach (var writeLock in write.LockPlan.Locks)
                await writeLock.AcquireAsync(context.Object, ct);
            var value = await write.ExecuteAsync(write.Request, context.Object, ct);
            return new(value, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }
    }

    private sealed class CapturingRequestExecutor : IWriteExecutor
    {
        public string? OperationName { get; private set; }
        public string? IdempotencyKey { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            IdempotencyKey = idempotencyKey;
            OperationName = write.OperationName;
            object result = new ScreeningMutationResult(ScreeningMutationOutcome.NotFound);
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)result, AtomicCommandDisposition.Executed, Guid.NewGuid()));
        }
    }
}
