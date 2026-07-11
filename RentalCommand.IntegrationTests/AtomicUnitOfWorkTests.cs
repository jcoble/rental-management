using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Esign;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Esign;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for the explicitly opt-in atomic persistence kernel.</summary>
public sealed class AtomicUnitOfWorkTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ExpenseResult> ExpenseCodec =
        new("atomic-expense-result.v2");
    private static readonly AtomicJsonResultCodec<EventResult> EventCodec =
        new("atomic-event-result.v1");
    private static readonly AtomicJsonResultCodec<ServiceBearingResult> ServiceBearingResultCodec =
        new("atomic-service-bearing-result.v1");
    private static readonly AtomicJsonResultCodec<IQueryable<Expense>> QueryResultCodec =
        new("atomic-query-result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable)
        {
            return;
        }

        // Once Docker passes the explicit preflight, startup, migration, DI, and application errors
        // are deliberately not caught: they fail the test class instead of becoming false skips.
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("rentalcommand")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HandlerProbe>();
        services.AddSingleton<NestedProbe>();
        services.AddSingleton<AtomicFlushFailureInterceptor>();
        services.AddSingleton<TransactionEvidenceInterceptor>();
        services.AddSingleton<ILlmProvider, FakeLlmProvider>();
        services.AddSingleton<IndirectRemoteWrapper>();
        services.AddSingleton(new HttpClient());
        services.AddHttpClient();
        services.AddSingleton<DbConnection>(new NpgsqlConnection(_postgres.GetConnectionString()));
        services.AddSingleton<Func<string>>(() => "forbidden-factory");
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<CreateExpenseCommand, ExpenseResult, CreateExpenseHandler>();
        services.AddAtomicCommandHandler<RetryExpenseCommand, ExpenseResult, RetryExpenseHandler>();
        services.AddAtomicCommandHandler<CreatedSemanticCommand, ExpenseResult, CreatedSemanticHandler>();
        services.AddAtomicCommandHandler<ExactAuditCommand, ExpenseResult, ExactAuditHandler>();
        services.AddAtomicCommandHandler<SemanticEventCommand, EventResult, SemanticEventHandler>();
        services.AddAtomicCommandHandler<SetBasedCommand, ExpenseResult, SetBasedHandler>();
        services.AddAtomicCommandHandler<NestedOuterCommand, ExpenseResult, NestedOuterHandler>();
        services.AddAtomicCommandHandler<NestedInnerCommand, ExpenseResult, NestedInnerHandler>();
        services.AddAtomicCommandHandler<DbContextDependencyCommand, ExpenseResult, DbContextDependencyHandler>();
        services.AddAtomicCommandHandler<AdoDependencyCommand, ExpenseResult, AdoDependencyHandler>();
        services.AddAtomicCommandHandler<RealRemoteCommand, ExpenseResult, RealRemoteHandler>();
        services.AddAtomicCommandHandler<IndirectRemoteCommand, ExpenseResult, IndirectRemoteHandler>();
        services.AddAtomicCommandHandler<HttpClientDependencyCommand, ExpenseResult, HttpClientDependencyHandler>();
        services.AddAtomicCommandHandler<HttpClientFactoryDependencyCommand, ExpenseResult, HttpClientFactoryDependencyHandler>();
        services.AddAtomicCommandHandler<ServiceProviderDependencyCommand, ExpenseResult, ServiceProviderDependencyHandler>();
        services.AddAtomicCommandHandler<DelegateDependencyCommand, ExpenseResult, DelegateDependencyHandler>();
        services.AddAtomicCommandHandler<ServiceBearingCommand, ExpenseResult, ServiceBearingCommandHandler>();
        services.AddAtomicCommandHandler<ServiceBearingResultCommand, ServiceBearingResult, ServiceBearingResultHandler>();
        services.AddAtomicCommandHandler<QueryResultCommand, IQueryable<Expense>, QueryResultHandler>();
        services.AddAtomicCommandHandler<
            CreateNativeEsignRequestCommand,
            CreateNativeEsignRequestResult,
            CreateNativeEsignRequestHandler>();
        services.AddAtomicCommandHandler<
            RecordNativeEsignViewCommand,
            RecordNativeEsignViewResult,
            RecordNativeEsignViewHandler>();
        services.AddAtomicCommandHandler<
            RecordNativeSignatureCommand,
            NativeSignerActionResult,
            RecordNativeSignatureHandler>();
        services.AddAtomicCommandHandler<
            RecordNativeDeclineCommand,
            NativeSignerActionResult,
            RecordNativeDeclineHandler>();
        services.AddAtomicCommandHandler<
            FinalizeNativeEsignRequestCommand,
            FinalizeNativeEsignRequestResult,
            FinalizeNativeEsignRequestHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(
                    _postgres.GetConnectionString(),
                    npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 2))
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<AtomicFlushFailureInterceptor>(),
                    provider.GetRequiredService<TransactionEvidenceInterceptor>()));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await db.Database.MigrateAsync();
        var portfolio = new Portfolio
        {
            Name = "Atomic Kernel Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Command_CommitsBusinessAuditReceipt_AndTransactionEvidence()
    {
        SkipIfDockerUnavailable();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        var identity = Identity(nameof(Command_CommitsBusinessAuditReceipt_AndTransactionEvidence));

        var outcome = await UnitOfWork.ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-success"),
            ExpenseCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Id == outcome.Value.Id)).Should().Be(1);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.EntityId == outcome.Value.Id)).Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(1);
        evidence.Rollbacks.Should().Be(0);
    }

    [SkippableFact]
    public async Task HybridHost_RoutesUnconvertedAndAtomicWritesToExactlyOneAuditPipelineEach()
    {
        SkipIfDockerUnavailable();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HandlerProbe>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddScoped<IAuditScope, AuditScope>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<CreateExpenseCommand, ExpenseResult, CreateExpenseHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>()));

        await using var hybrid = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var legacyMarker = $"hybrid-legacy-{Guid.NewGuid():N}";
        await using (var legacyScope = hybrid.CreateAsyncScope())
        {
            var db = legacyScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            db.Expenses.Add(new Expense
            {
                PortfolioId = _portfolioId,
                Category = ScheduleECategory.Repairs,
                Description = legacyMarker,
                Status = ExpenseStatus.Pending,
                Amount = 10m,
                IncurredAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var atomicMarker = $"hybrid-atomic-{Guid.NewGuid():N}";
        await hybrid.GetRequiredService<IAtomicUnitOfWork>().ExecuteAsync(
            Identity(nameof(HybridHost_RoutesUnconvertedAndAtomicWritesToExactlyOneAuditPipelineEach)),
            new CreateExpenseCommand(_portfolioId, atomicMarker),
            ExpenseCodec);

        await using var verifyScope = hybrid.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var legacyId = await verify.Expenses
            .Where(expense => expense.Description == legacyMarker)
            .Select(expense => expense.Id)
            .SingleAsync();
        var atomicId = await verify.Expenses
            .Where(expense => expense.Description == atomicMarker)
            .Select(expense => expense.Id)
            .SingleAsync();

        (await verify.AuditLogs.CountAsync(row => row.EntityType == nameof(Expense)
            && row.EntityId == legacyId)).Should().Be(1);
        (await verify.AtomicAuditLogs.CountAsync(row => row.EntityType == nameof(Expense)
            && row.EntityId == legacyId)).Should().Be(0);
        (await verify.AuditLogs.CountAsync(row => row.EntityType == nameof(Expense)
            && row.EntityId == atomicId)).Should().Be(0);
        (await verify.AtomicAuditLogs.CountAsync(row => row.EntityType == nameof(Expense)
            && row.EntityId == atomicId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task NativeEsignCommand_CommitsLeaseEnvelopeDocumentSignersAuditAndOutbox_Once()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var identity = Identity(nameof(NativeEsignCommand_CommitsLeaseEnvelopeDocumentSignersAuditAndOutbox_Once));
        var command = NativeEsignCommand(leaseId, "blob/first.pdf");
        var codec = new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1");

        var first = await UnitOfWork.ExecuteAsync(identity, command, codec);
        var replay = await UnitOfWork.ExecuteAsync(
            identity,
            NativeEsignCommand(leaseId, "blob/retry.pdf"),
            codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var lease = await verify.Db.Leases.AsNoTracking().SingleAsync(row => row.Id == leaseId);
        lease.EsignEnvelopeId.Should().Be(first.Value.PublicId);
        lease.EsignStatus.Should().Be(EsignStatus.Sent);
        lease.Status.Should().Be(LeaseStatus.PendingSignature);
        (await verify.Db.SignatureRequests.CountAsync(row => row.LeaseId == leaseId)).Should().Be(1);
        (await verify.Db.SignatureSigners.CountAsync(row => row.SignatureRequestId == first.Value.SignatureRequestId))
            .Should().Be(1);
        (await verify.Db.SignatureAuditEvents.CountAsync(row =>
            row.SignatureRequestId == first.Value.SignatureRequestId)).Should().Be(1);
        (await verify.Db.StoredFiles.CountAsync(row => row.Id == first.Value.OriginalStoredFileId)).Should().Be(1);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith($"lease-esign:{first.Value.SignatureRequestId}:")))
            .Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task NativeEsignCommand_FinalAuditFailure_RollsBackEntirePackage()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var identity = Identity(nameof(NativeEsignCommand_FinalAuditFailure_RollsBackEntirePackage));
        Services.GetRequiredService<AtomicFlushFailureInterceptor>().Arm();

        var action = () => UnitOfWork.ExecuteAsync(
            identity,
            NativeEsignCommand(leaseId, "blob/rollback.pdf"),
            new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"));

        await action.Should().ThrowAsync<InjectedFailureException>();
        await using var verify = await VerificationScope.CreateAsync(Services);
        var lease = await verify.Db.Leases.AsNoTracking().SingleAsync(row => row.Id == leaseId);
        lease.EsignEnvelopeId.Should().BeNull();
        lease.EsignStatus.Should().Be(EsignStatus.None);
        lease.Status.Should().Be(LeaseStatus.Draft);
        (await verify.Db.SignatureRequests.CountAsync(row => row.LeaseId == leaseId)).Should().Be(0);
        (await verify.Db.StoredFiles.CountAsync(row => row.EntityType == "esign-original" && row.EntityId == leaseId))
            .Should().Be(0);
        (await verify.Db.OutboxMessages.CountAsync(row =>
            row.IdempotencyKey.StartsWith("lease-esign:") && row.PortfolioId == _portfolioId))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task NativeEsignConcurrentFinalSigners_SerializeAggregate_AndLeaveExecutionPending()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var send = NativeEsignCommand(leaseId, "blob/concurrent.pdf") with
        {
            Signers = new[]
            {
                new NativeEsignSignerCommand("First Signer", "first@example.test", $"token-{Guid.NewGuid():N}"),
                new NativeEsignSignerCommand("Second Signer", "second@example.test", $"token-{Guid.NewGuid():N}"),
            },
        };
        var sent = await UnitOfWork.ExecuteAsync(
            Identity(nameof(NativeEsignConcurrentFinalSigners_SerializeAggregate_AndLeaveExecutionPending) + "-send"),
            send,
            new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"));
        var now = DateTime.UtcNow;
        var codec = new AtomicJsonResultCodec<NativeSignerActionResult>("native-esign.sign.v1");

        var actions = send.Signers.Select((signer, index) => UnitOfWork.ExecuteAsync(
            Identity(nameof(NativeEsignConcurrentFinalSigners_SerializeAggregate_AndLeaveExecutionPending) + index),
            new RecordNativeSignatureCommand(
                signer.Token,
                SignatureSignatureType.Typed,
                signer.Name,
                null,
                $"198.51.100.{index + 1}",
                "integration-test",
                now),
            codec));
        var outcomes = await Task.WhenAll(actions);

        outcomes.Should().OnlyContain(outcome => outcome.Value.Outcome == NativeSignerActionOutcome.Applied);
        outcomes.Count(outcome => outcome.Value.ExecutionRequired).Should().Be(1);
        await using var verify = await VerificationScope.CreateAsync(Services);
        var request = await verify.Db.SignatureRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sent.Value.SignatureRequestId);
        request.Status.Should().Be(SignatureRequestStatus.ExecutionPending);
        (await verify.Db.SignatureSigners.AsNoTracking()
            .CountAsync(signer => signer.SignatureRequestId == request.Id
                && signer.Status == SignatureSignerStatus.Signed)).Should().Be(2);
        (await verify.Db.SignatureAuditEvents.AsNoTracking()
            .CountAsync(audit => audit.SignatureRequestId == request.Id
                && audit.Type == SignatureAuditEventType.Signed)).Should().Be(2);
    }

    [SkippableFact]
    public async Task NativeEsignConcurrentFirstViews_CommitOneLegalFactAuditAndRedactedReceipt()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var send = NativeEsignCommand(leaseId, "blob/view-concurrent.pdf");
        var sent = await UnitOfWork.ExecuteAsync(
            Identity(nameof(NativeEsignConcurrentFirstViews_CommitOneLegalFactAuditAndRedactedReceipt) + "-send"),
            send,
            new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"));
        var token = send.Signers.Single().Token;
        var identity = ViewIdentity(token);
        var command = new RecordNativeEsignViewCommand(
            token,
            "203.0.113.20",
            "integration/view",
            DateTime.UtcNow);
        var codec = new AtomicJsonResultCodec<RecordNativeEsignViewResult>("native-esign.view.v1");

        var outcomes = await Task.WhenAll(
            UnitOfWork.ExecuteAsync(identity, command, codec),
            UnitOfWork.ExecuteAsync(identity, command, codec));

        outcomes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes.Should().OnlyContain(outcome => outcome.Value.Outcome == NativeEsignViewOutcome.Available);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var signer = await verify.Db.SignatureSigners.AsNoTracking()
            .SingleAsync(candidate => candidate.Token == token);
        signer.Status.Should().Be(SignatureSignerStatus.Viewed);
        signer.IpAddress.Should().Be("203.0.113.20");
        var request = await verify.Db.SignatureRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sent.Value.SignatureRequestId);
        request.Status.Should().Be(SignatureRequestStatus.Viewed);
        (await verify.Db.SignatureAuditEvents.AsNoTracking().CountAsync(audit =>
            audit.SignatureRequestId == request.Id
            && audit.Type == SignatureAuditEventType.Viewed)).Should().Be(1);

        var receipt = await verify.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(candidate =>
            candidate.CommandType == identity.CommandType
            && candidate.IdempotencyKey == identity.IdempotencyKey);
        receipt.IdempotencyKey.Should().HaveLength(64);
        receipt.IdempotencyKey.Should().NotContain(token);
        receipt.ResultJson.Should().NotContain(token);
        var auditPayloads = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(audit => audit.CommandType == identity.CommandType
                && audit.CommandIdempotencyKey == identity.IdempotencyKey)
            .Select(audit => new { audit.OldValues, audit.NewValues })
            .ToListAsync();
        auditPayloads.Should().HaveCount(2);
        auditPayloads.Should().OnlyContain(audit =>
            (audit.OldValues == null || !audit.OldValues.Contains(token, StringComparison.Ordinal))
            && (audit.NewValues == null || !audit.NewValues.Contains(token, StringComparison.Ordinal)),
            "the two command-scoped audit payloads must never retain the raw signer token");
    }

    [SkippableFact]
    public async Task NativeEsignFirstView_FinalAuditFailureRollsBackBusinessLegalEventAndReceipt()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var send = NativeEsignCommand(leaseId, "blob/view-rollback.pdf");
        var sent = await UnitOfWork.ExecuteAsync(
            Identity(nameof(NativeEsignFirstView_FinalAuditFailureRollsBackBusinessLegalEventAndReceipt) + "-send"),
            send,
            new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"));
        var token = send.Signers.Single().Token;
        var identity = ViewIdentity(token);
        Services.GetRequiredService<AtomicFlushFailureInterceptor>().Arm();

        var action = () => UnitOfWork.ExecuteAsync(
            identity,
            new RecordNativeEsignViewCommand(token, "203.0.113.30", "integration/rollback", DateTime.UtcNow),
            new AtomicJsonResultCodec<RecordNativeEsignViewResult>("native-esign.view.v1"));

        await action.Should().ThrowAsync<InjectedFailureException>();
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.SignatureSigners.AsNoTracking().SingleAsync(candidate => candidate.Token == token)).Status
            .Should().Be(SignatureSignerStatus.Pending);
        (await verify.Db.SignatureRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sent.Value.SignatureRequestId)).Status
            .Should().Be(SignatureRequestStatus.Sent);
        (await verify.Db.SignatureAuditEvents.AsNoTracking().CountAsync(audit =>
            audit.SignatureRequestId == sent.Value.SignatureRequestId
            && audit.Type == SignatureAuditEventType.Viewed)).Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(candidate =>
            candidate.CommandType == identity.CommandType
            && candidate.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(audit =>
            audit.CommandType == identity.CommandType
            && audit.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task NativeEsignFirstViewAndSignature_ShareAggregateLockWithoutStatusDowngrade()
    {
        SkipIfDockerUnavailable();
        var leaseId = await SeedLeaseAsync();
        var send = NativeEsignCommand(leaseId, "blob/view-sign-race.pdf");
        var sent = await UnitOfWork.ExecuteAsync(
            Identity(nameof(NativeEsignFirstViewAndSignature_ShareAggregateLockWithoutStatusDowngrade) + "-send"),
            send,
            new AtomicJsonResultCodec<CreateNativeEsignRequestResult>("native-esign.send.v1"));
        var token = send.Signers.Single().Token;
        var now = DateTime.UtcNow;

        var outcomes = await Task.WhenAll(ViewOnceAsync(), SignOnceAsync());

        outcomes.Should().OnlyContain(completed => completed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.SignatureSigners.AsNoTracking().SingleAsync(candidate => candidate.Token == token)).Status
            .Should().Be(SignatureSignerStatus.Signed);
        (await verify.Db.SignatureRequests.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sent.Value.SignatureRequestId)).Status
            .Should().Be(SignatureRequestStatus.ExecutionPending);
        (await verify.Db.SignatureAuditEvents.AsNoTracking().CountAsync(audit =>
            audit.SignatureRequestId == sent.Value.SignatureRequestId
            && audit.Type == SignatureAuditEventType.Signed)).Should().Be(1);
        (await verify.Db.SignatureAuditEvents.AsNoTracking().CountAsync(audit =>
            audit.SignatureRequestId == sent.Value.SignatureRequestId
            && audit.Type == SignatureAuditEventType.Viewed)).Should().BeLessThanOrEqualTo(1);

        async Task<bool> ViewOnceAsync()
        {
            var outcome = await UnitOfWork.ExecuteAsync(
                ViewIdentity(token),
                new RecordNativeEsignViewCommand(token, "203.0.113.50", "integration/view-race", now),
                new AtomicJsonResultCodec<RecordNativeEsignViewResult>("native-esign.view.v1"));
            return outcome.Value.Outcome == NativeEsignViewOutcome.Available;
        }

        async Task<bool> SignOnceAsync()
        {
            var outcome = await UnitOfWork.ExecuteAsync(
                Identity(nameof(NativeEsignFirstViewAndSignature_ShareAggregateLockWithoutStatusDowngrade) + "-sign"),
                new RecordNativeSignatureCommand(
                    token,
                    SignatureSignatureType.Typed,
                    "Tenant Signer",
                    null,
                    "203.0.113.51",
                    "integration/sign-race",
                    now),
                new AtomicJsonResultCodec<NativeSignerActionResult>("native-esign.sign.v1"));
            return outcome.Value.Outcome == NativeSignerActionOutcome.Applied;
        }
    }

    [SkippableFact]
    public async Task FailureDuringFinalAuditFlush_RollsBackBusinessAuditReceipt()
    {
        SkipIfDockerUnavailable();
        var failure = Services.GetRequiredService<AtomicFlushFailureInterceptor>();
        var evidence = Services.GetRequiredService<TransactionEvidenceInterceptor>();
        evidence.Reset();
        failure.Arm();
        var identity = Identity(nameof(FailureDuringFinalAuditFlush_RollsBackBusinessAuditReceipt));

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-final-failure"),
            ExpenseCodec);

        await act.Should().ThrowAsync<InjectedFailureException>();
        await AssertNothingCommitted(identity, "atomic-final-failure");
        evidence.Starts.Should().Be(1);
        evidence.Commits.Should().Be(0);
        evidence.Rollbacks.Should().Be(1);
    }

    [SkippableFact]
    public async Task ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(ConcurrentSameKey_ExecutesOneProducer_AndReplaysTheOther));
        var command = new CreateExpenseCommand(_portfolioId, "atomic-concurrent", DelayMilliseconds: 250);

        var outcomes = await Task.WhenAll(
            UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec),
            UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec));

        outcomes.Select(result => result.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes.Select(result => result.Value).Should().OnlyContain(value => value == outcomes[0].Value);
        Probe.Entries.Count(entry => entry.Marker == command.Marker).Should().Be(1);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == command.Marker)).Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task TransientFirstAttempt_UsesFreshHandlerDbContextAndAuditScope()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(TransientFirstAttempt_UsesFreshHandlerDbContextAndAuditScope));
        var command = new RetryExpenseCommand(_portfolioId, "atomic-retry");

        var outcome = await UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec);

        outcome.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        var attempts = Probe.Entries.Where(entry => entry.Marker == command.Marker).ToArray();
        attempts.Should().HaveCount(2);
        attempts.Select(entry => entry.HandlerId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.PersistenceSessionId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.AuditScopeId).Should().OnlyHaveUniqueItems();
        attempts.Select(entry => entry.AttemptId).Should().OnlyHaveUniqueItems();
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == command.Marker)).Should().Be(1);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task CreatedSemanticAudit_BindsUnresolvedId_AndStampsGeneratedId()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(CreatedSemanticAudit_BindsUnresolvedId_AndStampsGeneratedId));

        var outcome = await UnitOfWork.ExecuteAsync(
            identity,
            new CreatedSemanticCommand(_portfolioId, "atomic-created-semantic"),
            ExpenseCodec);

        outcome.Value.Id.Should().BePositive();
        await using var verify = await VerificationScope.CreateAsync(Services);
        var row = await verify.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(audit =>
            audit.CommandType == identity.CommandType
            && audit.CommandIdempotencyKey == identity.IdempotencyKey);
        row.EntityId.Should().Be(outcome.Value.Id);
        row.Operation.Should().Be(AuditLogOperation.Created);
        row.ChangeReason.Should().Be("rich-created-audit");
        row.NewValues.Should().Contain("atomic-created-semantic");
    }

    [SkippableFact]
    public async Task SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SemanticAudit_BindsBeforeFlush_AndEnrichesExactDescriptorAfterFlush));

        var outcome = await UnitOfWork.ExecuteAsync(
            identity,
            new ExactAuditCommand(_portfolioId, "atomic-exact-audit"),
            ExpenseCodec);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var updates = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.EntityId == outcome.Value.Id
                && row.Operation == AuditLogOperation.Updated)
            .OrderBy(row => row.MutationOrdinal)
            .ToListAsync();
        updates.Should().HaveCount(2);
        updates.Select(row => row.MutationOrdinal).Should().OnlyHaveUniqueItems();
        updates.Select(row => row.ChangeReason).Should().Equal("bound-before-flush", "enriched-after-flush");
    }

    [SkippableFact]
    public async Task SemanticOnlyEvent_UsesSeparateExplicitPath_AndReplays()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SemanticOnlyEvent_UsesSeparateExplicitPath_AndReplays));
        var command = new SemanticEventCommand(_portfolioId, "policy-enabled");

        var first = await UnitOfWork.ExecuteAsync(identity, command, EventCodec);
        var replay = await UnitOfWork.ExecuteAsync(identity, command, EventCodec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task SetBasedAuditMismatch_IsRejectedAndRollsBack()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SetBasedAuditMismatch_IsRejectedAndRollsBack));

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new SetBasedCommand(_portfolioId, "atomic-set-mismatch", MismatchAudit: true),
            ExpenseCodec);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*exact set-based target*");
        await AssertNothingCommitted(identity, "atomic-set-mismatch-1");
    }

    [SkippableFact]
    public async Task SetBasedExecutor_ExecutesTwoDistinctExactMutations()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(SetBasedExecutor_ExecutesTwoDistinctExactMutations));

        await UnitOfWork.ExecuteAsync(
            identity,
            new SetBasedCommand(_portfolioId, "atomic-set-two", MismatchAudit: false),
            ExpenseCodec);

        await using var verify = await VerificationScope.CreateAsync(Services);
        var rows = await verify.Db.Expenses.AsNoTracking()
            .Where(row => row.Description.StartsWith("atomic-set-two"))
            .OrderBy(row => row.Description)
            .Select(row => row.Amount)
            .ToListAsync();
        rows.Should().Equal(301m, 302m);
        var audits = await verify.Db.AtomicAuditLogs.AsNoTracking()
            .Where(row => row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey
                && row.Operation == AuditLogOperation.Updated)
            .OrderBy(row => row.MutationOrdinal)
            .ToListAsync();
        audits.Should().HaveCount(2);
        audits.Select(row => row.EntityId).Should().OnlyHaveUniqueItems();
        audits.Select(row => row.ChangeReason).Should().Equal("exact-set-1", "exact-set-2");
    }

    [SkippableFact]
    public async Task RawExecuteSqlDml_IsRejectedBeforeDatabaseMutation()
    {
        SkipIfDockerUnavailable();
        var identity = Identity(nameof(RawExecuteSqlDml_IsRejectedBeforeDatabaseMutation));
        var created = await UnitOfWork.ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "atomic-raw-sql"),
            ExpenseCodec);
        await using var rawScope = await VerificationScope.CreateAsync(Services);

        Func<Task> act = () => rawScope.Db.Database.ExecuteSqlRawAsync(
            """UPDATE "Expenses" SET "Amount" = 999 WHERE "Id" = {0}""",
            created.Value.Id);

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*Raw Update DML is forbidden*");
        Func<Task> interpolatedAct = () => rawScope.Db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Expenses" SET "Amount" = 998 WHERE "Id" = {created.Value.Id}""");
        await interpolatedAct.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*Raw Update DML is forbidden*");
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking()
            .Where(expense => expense.Id == created.Value.Id)
            .Select(expense => expense.Amount)
            .SingleAsync()).Should().Be(100m);
    }

    [SkippableFact]
    public async Task HandlerAdmission_RejectsDbContextAndRawAdoDependencies()
    {
        SkipIfDockerUnavailable();
        await AssertAdmissionRejected(
            new DbContextDependencyCommand(_portfolioId),
            "*RentalCommandDbContext*");
        await AssertAdmissionRejected(
            new AdoDependencyCommand(_portfolioId),
            "*DbConnection*");
    }

    [SkippableFact]
    public async Task NestedCommand_JoinsOwnerScope_WithoutSecondReceipt()
    {
        SkipIfDockerUnavailable();
        var outerIdentity = Identity(nameof(NestedCommand_JoinsOwnerScope_WithoutSecondReceipt));
        var innerIdentity = new AtomicCommandIdentity("test.atomic.inner", outerIdentity.IdempotencyKey);

        var outer = await UnitOfWork.ExecuteAsync(
            outerIdentity,
            new NestedOuterCommand(_portfolioId, "atomic-nested", innerIdentity),
            ExpenseCodec);

        Nested.InnerOutcome.Should().NotBeNull();
        Nested.InnerOutcome!.Disposition.Should().Be(AtomicCommandDisposition.Joined);
        Nested.InnerOutcome.AttemptId.Should().Be(outer.AttemptId);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            (row.CommandType == outerIdentity.CommandType || row.CommandType == innerIdentity.CommandType)
            && row.IdempotencyKey == outerIdentity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task HandlerAdmission_RejectsActualRemoteAndIndirectWrapper()
    {
        SkipIfDockerUnavailable();
        await AssertAdmissionRejected(
            new RealRemoteCommand(_portfolioId),
            "*ILlmProvider*");
        await AssertAdmissionRejected(
            new IndirectRemoteCommand(_portfolioId),
            "*ILlmProvider*");
    }

    [SkippableFact]
    public async Task HandlerAdmission_RejectsHttpClientServiceProviderAndDelegateFactories()
    {
        SkipIfDockerUnavailable();
        await AssertAdmissionRejected(
            new HttpClientDependencyCommand(_portfolioId),
            "*HttpClient*");
        await AssertAdmissionRejected(
            new HttpClientFactoryDependencyCommand(_portfolioId),
            "*IHttpClientFactory*");
        await AssertAdmissionRejected(
            new ServiceProviderDependencyCommand(_portfolioId),
            "*IServiceProvider*");
        await AssertAdmissionRejected(
            new DelegateDependencyCommand(_portfolioId),
            "*Func*");
    }

    [SkippableFact]
    public async Task CommandAdmission_RejectsServiceBearingInputBeforeReceiptClaim()
    {
        SkipIfDockerUnavailable();
        await AssertAdmissionRejected(
            new ServiceBearingCommand(
                _portfolioId,
                Services.GetRequiredService<ILlmProvider>()),
            "*command member*ILlmProvider*");
    }

    [SkippableFact]
    public async Task ResultAdmission_RejectsServiceBearingAndQueryableContractsBeforeReceiptClaim()
    {
        SkipIfDockerUnavailable();
        var serviceIdentity = Identity("admission-service-bearing-result");
        Func<Task> serviceAct = async () => await UnitOfWork.ExecuteAsync(
            serviceIdentity,
            new ServiceBearingResultCommand(_portfolioId),
            ServiceBearingResultCodec);
        await serviceAct.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*result member*ILlmProvider*");
        await AssertNoReceipt(serviceIdentity);

        var queryIdentity = Identity("admission-queryable-result");
        Func<Task> queryAct = async () => await UnitOfWork.ExecuteAsync(
            queryIdentity,
            new QueryResultCommand(_portfolioId),
            QueryResultCodec);
        await queryAct.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*IQueryable*");
        await AssertNoReceipt(queryIdentity);
    }

    [SkippableFact]
    public async Task CodecAdmission_RejectsCallerCodecCapturingRemoteBeforeReceiptClaim()
    {
        SkipIfDockerUnavailable();
        var identity = Identity("admission-remote-result-codec");
        var codec = new RemoteResultCodec(Services.GetRequiredService<ILlmProvider>());

        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(
            identity,
            new CreateExpenseCommand(_portfolioId, "forbidden-codec"),
            codec);

        await act.Should().ThrowAsync<AtomicArchitectureException>()
            .WithMessage("*result codec*RemoteResultCodec*forbidden*");
        await AssertNoReceipt(identity);
    }

    private IServiceProvider Services => _services!;
    private IAtomicUnitOfWork UnitOfWork => Services.GetRequiredService<IAtomicUnitOfWork>();
    private HandlerProbe Probe => Services.GetRequiredService<HandlerProbe>();
    private NestedProbe Nested => Services.GetRequiredService<NestedProbe>();

    private AtomicCommandIdentity Identity(string testName) =>
        new($"test.atomic.{testName}", Guid.NewGuid().ToString("N"));

    private static AtomicCommandIdentity ViewIdentity(string token) =>
        new(
            "native-esign.view",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant());

    private CreateNativeEsignRequestCommand NativeEsignCommand(int leaseId, string storageKey)
    {
        var now = DateTime.UtcNow;
        return new CreateNativeEsignRequestCommand(
            _portfolioId,
            leaseId,
            Guid.NewGuid().ToString("N"),
            $"lease-{leaseId}-agreement.pdf",
            $"Lease #{leaseId}",
            storageKey,
            FileSize: 1234,
            DocumentTemplateId: null,
            DocumentTemplateVersion: null,
            TemplateFieldSnapshotJson: null,
            WebBaseUrl: "https://app.test",
            CreatedAtUtc: now,
            LinkExpiresAtUtc: now.AddDays(14),
            Signers:
            new[]
            {
                new NativeEsignSignerCommand(
                    "Tenant Signer",
                    $"tenant-{Guid.NewGuid():N}@example.com",
                    Guid.NewGuid().ToString("N")),
            });
    }

    private async Task<int> SeedLeaseAsync()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options;
        await using var db = new RentalCommandDbContext(options);
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = $"Atomic e-sign property {Guid.NewGuid():N}",
            AddressLine1 = "100 Atomic Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = _portfolioId,
            FirstName = "Atomic",
            LastName = "Signer",
            Email = $"atomic-{Guid.NewGuid():N}@example.com",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(property, tenant);
        await db.SaveChangesAsync();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = $"A-{Guid.NewGuid():N}"[..20],
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1000m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Units.Add(unit);
        await db.SaveChangesAsync();

        var lease = new Lease
        {
            PortfolioId = _portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = $"AT-{Guid.NewGuid():N}"[..20],
            Status = LeaseStatus.Draft,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1000m,
            SecurityDeposit = 1000m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Leases.Add(lease);
        await db.SaveChangesAsync();
        return lease.Id;
    }

    private async Task AssertAdmissionRejected<TCommand>(TCommand command, string messagePattern)
        where TCommand : notnull, IAtomicCommandData
    {
        var identity = Identity($"admission-{typeof(TCommand).Name}");
        Func<Task> act = async () => await UnitOfWork.ExecuteAsync(identity, command, ExpenseCodec);

        await act.Should().ThrowAsync<AtomicArchitectureException>().WithMessage(messagePattern);
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private async Task AssertNothingCommitted(AtomicCommandIdentity identity, string marker)
    {
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.Expenses.AsNoTracking().CountAsync(row => row.Description == marker)).Should().Be(0);
        (await verify.Db.AtomicAuditLogs.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.CommandIdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey))
            .Should().Be(0);
    }

    private async Task AssertNoReceipt(AtomicCommandIdentity identity)
    {
        await using var verify = await VerificationScope.CreateAsync(Services);
        (await verify.Db.AtomicCommandReceipts.AsNoTracking().CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private void SkipIfDockerUnavailable() =>
        Skip.IfNot(_dockerAvailable, "Docker socket preflight failed; PostgreSQL atomic-kernel tests skipped.");

    private static async Task<bool> DockerSocketPreflightAsync()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        if (Uri.TryCreate(dockerHost, UriKind.Absolute, out var hostUri)
            && hostUri.Scheme is "tcp" or "http" or "https")
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(hostUri.Host, hostUri.Port > 0 ? hostUri.Port : 2375);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        var candidates = new[]
        {
            dockerHost?.StartsWith("unix://", StringComparison.Ordinal) == true ? dockerHost[7..] : null,
            "/var/run/docker.sock",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker/run/docker.sock"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima/default/docker.sock"),
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal);

        foreach (var path in candidates)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path!));
                return true;
            }
            catch (SocketException)
            {
                // Try the next well-known Docker socket.
            }
        }

        return false;
    }

    private static Expense NewExpense(int portfolioId, string marker) => new()
    {
        PortfolioId = portfolioId,
        Category = ScheduleECategory.Repairs,
        Description = marker,
        Status = ExpenseStatus.Pending,
        Amount = 100m,
        IncurredAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed record ExpenseResult(int Id) : IAtomicResultData;
    private sealed record EventResult(string Name) : IAtomicResultData;
    private sealed record ServiceBearingResult(ILlmProvider Provider) : IAtomicResultData;
    private sealed record CreateExpenseCommand(int PortfolioId, string Marker, int DelayMilliseconds = 0)
        : IAtomicCommandData;
    private sealed record RetryExpenseCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record CreatedSemanticCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record ExactAuditCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record SemanticEventCommand(int PortfolioId, string Name) : IAtomicCommandData;
    private sealed record SetBasedCommand(int PortfolioId, string Marker, bool MismatchAudit) : IAtomicCommandData;
    private sealed record NestedOuterCommand(
        int PortfolioId,
        string Marker,
        AtomicCommandIdentity InnerIdentity) : IAtomicCommandData;
    private sealed record NestedInnerCommand(int PortfolioId, string Marker) : IAtomicCommandData;
    private sealed record DbContextDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record AdoDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record RealRemoteCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record IndirectRemoteCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record HttpClientDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record HttpClientFactoryDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record ServiceProviderDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record DelegateDependencyCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record ServiceBearingCommand(int PortfolioId, ILlmProvider Provider) : IAtomicCommandData;
    private sealed record ServiceBearingResultCommand(int PortfolioId) : IAtomicCommandData;
    private sealed record QueryResultCommand(int PortfolioId) : IAtomicCommandData;

    private sealed class CreateExpenseHandler : IAtomicCommandHandler<CreateExpenseCommand, ExpenseResult>
    {
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public CreateExpenseHandler(HandlerProbe probe) => _probe = probe;

        public async Task<ExpenseResult> HandleAsync(
            CreateExpenseCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, attempt);
            if (command.DelayMilliseconds > 0)
            {
                await Task.Delay(command.DelayMilliseconds, ct);
            }

            var expense = NewExpense(command.PortfolioId, command.Marker);
            attempt.Persistence.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class RetryExpenseHandler : IAtomicCommandHandler<RetryExpenseCommand, ExpenseResult>
    {
        private readonly HandlerProbe _probe;
        private readonly Guid _handlerId = Guid.NewGuid();

        public RetryExpenseHandler(HandlerProbe probe) => _probe = probe;

        public async Task<ExpenseResult> HandleAsync(
            RetryExpenseCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.Record(command.Marker, _handlerId, attempt);
            var expense = NewExpense(command.PortfolioId, command.Marker);
            attempt.Persistence.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            if (_probe.Entries.Count(entry => entry.Marker == command.Marker) == 1)
            {
                throw new NpgsqlException("injected transient attempt", new TimeoutException());
            }

            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class CreatedSemanticHandler : IAtomicCommandHandler<CreatedSemanticCommand, ExpenseResult>
    {
        public CreatedSemanticHandler() { }

        public async Task<ExpenseResult> HandleAsync(
            CreatedSemanticCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            attempt.Persistence.Add(expense);
            attempt.BindSemanticAudit(
                expense,
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Expense),
                    EntityId: 0,
                    Operation: AuditLogOperation.Created,
                    NewValues: $$"""{"Description":"{{command.Marker}}"}""",
                    ChangeReason: "rich-created-audit"));
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class ExactAuditHandler : IAtomicCommandHandler<ExactAuditCommand, ExpenseResult>
    {
        public ExactAuditHandler() { }

        public async Task<ExpenseResult> HandleAsync(
            ExactAuditCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            attempt.Persistence.Add(expense);
            await attempt.FlushBusinessAsync(ct);

            expense.Amount = 150m;
            attempt.BindSemanticAudit(
                expense,
                Audit(command.PortfolioId, expense.Id, "bound-before-flush", 150m));
            await attempt.FlushBusinessAsync(ct);

            expense.Amount = 225m;
            var second = await attempt.FlushBusinessAsync(ct);
            attempt.EnrichMutation(
                second.Mutations.Single(),
                Audit(command.PortfolioId, expense.Id, "enriched-after-flush", 225m));
            return new ExpenseResult(expense.Id);
        }

        private static AtomicSemanticAudit Audit(int portfolioId, int id, string reason, decimal amount) =>
            new(
                portfolioId,
                nameof(Expense),
                id,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Amount":{{amount}}}""",
                ChangeReason: reason);
    }

    private sealed class SemanticEventHandler : IAtomicCommandHandler<SemanticEventCommand, EventResult>
    {
        public SemanticEventHandler() { }

        public Task<EventResult> HandleAsync(
            SemanticEventCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            attempt.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                "PortfolioPolicy",
                command.PortfolioId,
                AuditLogOperation.Updated,
                NewValues: $$"""{"Name":"{{command.Name}}"}""",
                ChangeReason: command.Name));
            return Task.FromResult(new EventResult(command.Name));
        }
    }

    private sealed class SetBasedHandler : IAtomicCommandHandler<SetBasedCommand, ExpenseResult>
    {
        public SetBasedHandler() { }

        public async Task<ExpenseResult> HandleAsync(
            SetBasedCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var first = NewExpense(command.PortfolioId, $"{command.Marker}-1");
            var second = NewExpense(command.PortfolioId, $"{command.Marker}-2");
            attempt.Persistence.AddRange([first, second]);
            await attempt.FlushBusinessAsync(ct);

            var firstAuditId = command.MismatchAudit ? second.Id : first.Id;
            await attempt.SetBased.UpdatePropertyAsync<Expense, decimal>(
                command.PortfolioId,
                first.Id,
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Expense),
                    firstAuditId,
                    AuditLogOperation.Updated,
                    NewValues: """{"Amount":301}""",
                    ChangeReason: "exact-set-1"),
                row => row.Amount,
                301m,
                ct);
            if (!command.MismatchAudit)
            {
                await attempt.SetBased.UpdatePropertyAsync<Expense, decimal>(
                    command.PortfolioId,
                    second.Id,
                    new AtomicSemanticAudit(
                        command.PortfolioId,
                        nameof(Expense),
                        second.Id,
                        AuditLogOperation.Updated,
                        NewValues: """{"Amount":302}""",
                        ChangeReason: "exact-set-2"),
                    row => row.Amount,
                    302m,
                    ct);
            }

            return new ExpenseResult(first.Id);
        }
    }

    private sealed class NestedOuterHandler : IAtomicCommandHandler<NestedOuterCommand, ExpenseResult>
    {
        private readonly IAtomicUnitOfWork _unitOfWork;
        private readonly NestedProbe _probe;

        public NestedOuterHandler(IAtomicUnitOfWork unitOfWork, NestedProbe probe)
        {
            _unitOfWork = unitOfWork;
            _probe = probe;
        }

        public async Task<ExpenseResult> HandleAsync(
            NestedOuterCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            _probe.InnerOutcome = await _unitOfWork.ExecuteAsync(
                command.InnerIdentity,
                new NestedInnerCommand(command.PortfolioId, command.Marker),
                ExpenseCodec,
                ct);
            return _probe.InnerOutcome.Value;
        }
    }

    private sealed class NestedInnerHandler : IAtomicCommandHandler<NestedInnerCommand, ExpenseResult>
    {
        public NestedInnerHandler() { }

        public async Task<ExpenseResult> HandleAsync(
            NestedInnerCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            var expense = NewExpense(command.PortfolioId, command.Marker);
            attempt.Persistence.Add(expense);
            await attempt.FlushBusinessAsync(ct);
            return new ExpenseResult(expense.Id);
        }
    }

    private sealed class DbContextDependencyHandler
        : IAtomicCommandHandler<DbContextDependencyCommand, ExpenseResult>
    {
        public DbContextDependencyHandler(RentalCommandDbContext db) { }

        public Task<ExpenseResult> HandleAsync(
            DbContextDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class AdoDependencyHandler : IAtomicCommandHandler<AdoDependencyCommand, ExpenseResult>
    {
        public AdoDependencyHandler(DbConnection connection) { }

        public Task<ExpenseResult> HandleAsync(
            AdoDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class RealRemoteHandler : IAtomicCommandHandler<RealRemoteCommand, ExpenseResult>
    {
        public RealRemoteHandler(ILlmProvider provider) { }

        public Task<ExpenseResult> HandleAsync(
            RealRemoteCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class IndirectRemoteWrapper : IAtomicTransactionSafeDependency
    {
        public IndirectRemoteWrapper(ILlmProvider provider) { }
    }

    private sealed class IndirectRemoteHandler : IAtomicCommandHandler<IndirectRemoteCommand, ExpenseResult>
    {
        public IndirectRemoteHandler(IndirectRemoteWrapper wrapper) { }

        public Task<ExpenseResult> HandleAsync(
            IndirectRemoteCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class HttpClientDependencyHandler
        : IAtomicCommandHandler<HttpClientDependencyCommand, ExpenseResult>
    {
        public HttpClientDependencyHandler(HttpClient httpClient) { }

        public Task<ExpenseResult> HandleAsync(
            HttpClientDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class HttpClientFactoryDependencyHandler
        : IAtomicCommandHandler<HttpClientFactoryDependencyCommand, ExpenseResult>
    {
        public HttpClientFactoryDependencyHandler(IHttpClientFactory factory) { }

        public Task<ExpenseResult> HandleAsync(
            HttpClientFactoryDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class ServiceProviderDependencyHandler
        : IAtomicCommandHandler<ServiceProviderDependencyCommand, ExpenseResult>
    {
        public ServiceProviderDependencyHandler(IServiceProvider services) { }

        public Task<ExpenseResult> HandleAsync(
            ServiceProviderDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class DelegateDependencyHandler
        : IAtomicCommandHandler<DelegateDependencyCommand, ExpenseResult>
    {
        public DelegateDependencyHandler(Func<string> factory) { }

        public Task<ExpenseResult> HandleAsync(
            DelegateDependencyCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class ServiceBearingCommandHandler
        : IAtomicCommandHandler<ServiceBearingCommand, ExpenseResult>
    {
        public ServiceBearingCommandHandler() { }

        public Task<ExpenseResult> HandleAsync(
            ServiceBearingCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class ServiceBearingResultHandler
        : IAtomicCommandHandler<ServiceBearingResultCommand, ServiceBearingResult>
    {
        public ServiceBearingResultHandler() { }

        public Task<ServiceBearingResult> HandleAsync(
            ServiceBearingResultCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class QueryResultHandler
        : IAtomicCommandHandler<QueryResultCommand, IQueryable<Expense>>
    {
        public QueryResultHandler() { }

        public Task<IQueryable<Expense>> HandleAsync(
            QueryResultCommand command,
            IAtomicWriteAttempt attempt,
            CancellationToken ct) => throw ForbiddenHandlerRan();
    }

    private sealed class RemoteResultCodec : IAtomicResultCodec<ExpenseResult>
    {
        private readonly ILlmProvider _provider;

        public RemoteResultCodec(ILlmProvider provider) => _provider = provider;

        public string ContractName => _provider.GetType().FullName!;
        public string Serialize(ExpenseResult result) => throw ForbiddenHandlerRan();
        public ExpenseResult Deserialize(string json) => throw ForbiddenHandlerRan();
    }

    private static InvalidOperationException ForbiddenHandlerRan() =>
        new("A forbidden handler must be rejected before construction/execution.");

    private sealed class FakeLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class HandlerProbe : IAtomicTransactionSafeDependency
    {
        public HandlerProbe() { }

        public ConcurrentBag<AttemptEvidence> Entries { get; } = [];

        public void Record(
            string marker,
            Guid handlerId,
            IAtomicWriteAttempt attempt) =>
            Entries.Add(new AttemptEvidence(
                marker,
                handlerId,
                attempt.Persistence.SessionId,
                attempt.AuditScopeId,
                attempt.AttemptId));
    }

    private sealed record AttemptEvidence(
        string Marker,
        Guid HandlerId,
        Guid PersistenceSessionId,
        Guid AuditScopeId,
        Guid AttemptId);

    private sealed class NestedProbe : IAtomicTransactionSafeDependency
    {
        public NestedProbe() { }

        public AtomicCommandOutcome<ExpenseResult>? InnerOutcome { get; set; }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:atomic-kernel";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class InjectedFailureException(string message) : Exception(message);

    private sealed class AtomicFlushFailureInterceptor : SaveChangesInterceptor
    {
        private int _armed;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && eventData.Context?.ChangeTracker.Entries<AtomicAuditLog>()
                    .Any(entry => entry.State == EntityState.Added) == true
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                throw new InjectedFailureException("during atomic audit companion flush");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransactionEvidenceInterceptor : DbTransactionInterceptor
    {
        private int _starts;
        private int _commits;
        private int _rollbacks;
        public int Starts => Volatile.Read(ref _starts);
        public int Commits => Volatile.Read(ref _commits);
        public int Rollbacks => Volatile.Read(ref _rollbacks);
        public void Reset()
        {
            Interlocked.Exchange(ref _starts, 0);
            Interlocked.Exchange(ref _commits, 0);
            Interlocked.Exchange(ref _rollbacks, 0);
        }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commits);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _rollbacks);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class VerificationScope(AsyncServiceScope scope, RentalCommandDbContext db)
        : IAsyncDisposable
    {
        public RentalCommandDbContext Db { get; } = db;

        public static Task<VerificationScope> CreateAsync(IServiceProvider services)
        {
            var scope = services.CreateAsyncScope();
            return Task.FromResult(new VerificationScope(
                scope,
                scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>()));
        }

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
