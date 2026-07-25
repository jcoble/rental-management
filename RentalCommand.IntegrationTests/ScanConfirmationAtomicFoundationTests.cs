using System.Collections.Concurrent;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Scanning;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for the inert scan-confirm lifecycle foundation.</summary>
public sealed class ScanConfirmationAtomicFoundationTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<ConfirmScanDraftResult> Codec =
        new("scan-confirm.result.v1");

    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private readonly ConcurrentDictionary<int, string> _preparedFingerprints = new();

    public async Task InitializeAsync()
    {
        _dockerAvailable = await DockerSocketPreflightAsync();
        if (!_dockerAvailable)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("rentalcommand")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddSingleton<TestWriterProbe>();
        services.AddSingleton<AtomicCompanionFailureInterceptor>();
        services.AddScoped<TestExpenseTargetWriter>();
        services.AddAtomicPersistenceKernel(allowUnconvertedWrites: true);
        services.AddAtomicCommandHandler<
            ConfirmScanDraftCommand,
            ConfirmScanDraftResult,
            ConfirmScanDraftHandler<TestExpenseTargetWriter>>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<AtomicCompanionFailureInterceptor>()));
        _services = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        await db.Database.EnsureCreatedAsync();
        var portfolio = new Portfolio
        {
            Name = "Atomic scan confirmation",
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
    public async Task DuplicateReceipt_ReplaysOneResultAndCreatesOneTarget()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("duplicate");
        var command = Command(draftId, "duplicate-target");
        var identity = Identity(draftId);

        var first = await UnitOfWork.ExecuteAsync(identity, command, Codec);
        var replay = await UnitOfWork.ExecuteAsync(identity, command, Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().Be(first.Value);
        await using var verify = Scope();
        (await verify.Db.Expenses.CountAsync(expense => expense.Description == "duplicate-target"))
            .Should().Be(1);
        (await verify.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task NotReady_DoesNotPersistReceiptAndCanSucceedAfterStateChanges()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("not-ready-retry");
        var identity = Identity(draftId);
        await using (var arrange = Scope())
        {
            await arrange.Db.ScanDrafts.Where(row => row.Id == draftId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Processing"));
        }

        var firstAttempt = () => UnitOfWork.ExecuteAsync(
            identity, Command(draftId, "not-ready-target"), Codec);

        await firstAttempt.Should().ThrowAsync<ScanConfirmationValidationException>();
        await using (var verifyNoReceipt = Scope())
        {
            (await verifyNoReceipt.Db.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            await verifyNoReceipt.Db.ScanDrafts.Where(row => row.Id == draftId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, "Reviewing"));
        }

        var retry = await UnitOfWork.ExecuteAsync(
            identity, Command(draftId, "not-ready-target"), Codec);

        retry.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        retry.Disposition.Should().Be(AtomicCommandDisposition.Executed);
    }

    [SkippableTheory]
    [InlineData("extraction")]
    [InlineData("source")]
    [InlineData("target")]
    public async Task ChangedPreparedDraftFact_RollsBackReceiptAndAllBusinessEffects(string changedFact)
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync($"stale-{changedFact}");
        var command = Command(draftId, $"stale-{changedFact}-target");
        var identity = Identity(draftId, $"stale-{changedFact}");
        int? replacementSourceId = null;

        await using (var arrange = Scope())
        {
            if (changedFact == "source")
            {
                var replacement = new StoredFile
                {
                    PortfolioId = _portfolioId,
                    FileName = "replacement.jpg",
                    FilePath = $"scan/replacement-{Guid.NewGuid():N}.jpg",
                    ContentType = "image/jpeg",
                    FileSize = 101,
                    EntityType = "Expense",
                    UploadedAt = CommandTime,
                };
                arrange.Db.StoredFiles.Add(replacement);
                await arrange.Db.SaveChangesAsync();
                replacementSourceId = replacement.Id;
            }

            var draftQuery = arrange.Db.ScanDrafts.Where(row => row.Id == draftId);
            if (changedFact == "extraction")
            {
                await draftQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.ExtractedFields, "{\"total\":{\"value\":999}}"));
            }
            else if (changedFact == "source")
            {
                await draftQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.SourceStoredFileId, replacementSourceId));
            }
            else
            {
                await draftQuery.ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.TargetEntityType, "Payment"));
            }
        }

        var action = () => UnitOfWork.ExecuteAsync(identity, command, Codec);

        (await action.Should().ThrowAsync<ScanConfirmationValidationException>())
            .Which.Message.Should().Contain("Review the latest extraction");
        await using var verify = Scope();
        var draft = await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
        draft.Status.Should().Be("Reviewing");
        draft.ConfirmedEntityId.Should().BeNull();
        (await verify.Db.Expenses.CountAsync(expense => expense.Description == $"stale-{changedFact}-target"))
            .Should().Be(0);
        (await verify.Db.StoredFiles.CountAsync(file => file.EntityId != null
            && (file.Id == draft.SourceStoredFileId || file.Id == replacementSourceId))).Should().Be(0);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.Db.OutboxMessages.CountAsync()).Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task AlreadyConfirmed_ReturnsCanonicalResultBeforeFingerprintComparison()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("already-confirmed-stale-fingerprint");
        var first = await UnitOfWork.ExecuteAsync(
            Identity(draftId, "first-confirm"), Command(draftId, "canonical-target"), Codec);
        var staleCommand = Command(draftId, "ignored-target") with
        {
            ExpectedDraftFingerprint = new string('0', 64),
        };

        var recovered = await UnitOfWork.ExecuteAsync(
            Identity(draftId, "recover-confirmed"), staleCommand, Codec);

        recovered.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.AlreadyConfirmed);
        recovered.Value.TargetEntityId.Should().Be(first.Value.TargetEntityId);
        await using var verify = Scope();
        (await verify.Db.Expenses.CountAsync()).Should().Be(1);
    }

    [SkippableFact]
    public async Task SimultaneousDifferentReceipts_SerializeOnDraftAndReturnCanonicalTarget()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("simultaneous");
        var probe = Services.GetRequiredService<TestWriterProbe>();
        probe.PauseDraft(draftId);

        var firstTask = UnitOfWork.ExecuteAsync(
            Identity(draftId, "first"), Command(draftId, "simultaneous-target"), Codec);
        await probe.WaitUntilEnteredAsync();
        var secondTask = UnitOfWork.ExecuteAsync(
            Identity(draftId, "second"), Command(draftId, "simultaneous-target"), Codec);
        probe.Release();

        var outcomes = await Task.WhenAll(firstTask, secondTask);

        outcomes.Should().ContainSingle(outcome => outcome.Value.Outcome == ConfirmScanDraftOutcome.Confirmed);
        outcomes.Should().ContainSingle(outcome => outcome.Value.Outcome == ConfirmScanDraftOutcome.AlreadyConfirmed);
        outcomes.Select(outcome => outcome.Value.TargetEntityId).Distinct().Should().ContainSingle();
        await using var verify = Scope();
        (await verify.Db.Expenses.CountAsync(expense => expense.Description == "simultaneous-target"))
            .Should().Be(1);
        (await verify.Db.ScanDrafts.SingleAsync(draft => draft.Id == draftId)).Status
            .Should().Be("Confirmed");
    }

    [SkippableFact]
    public async Task InjectedCompanionFailure_RollsBackDraftTargetFileAuditAndReceipt()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("rollback");
        Services.GetRequiredService<AtomicCompanionFailureInterceptor>().Arm();
        var identity = Identity(draftId);

        var action = () => UnitOfWork.ExecuteAsync(
            identity, Command(draftId, "rollback-target"), Codec);

        await action.Should().ThrowAsync<InjectedScanWriterFailure>();
        await using var verify = Scope();
        var draft = await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
        var file = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == draft.SourceStoredFileId);
        draft.Status.Should().Be("Reviewing");
        draft.ConfirmedAt.Should().BeNull();
        file.EntityId.Should().BeNull();
        file.EntityType.Should().Be("Expense");
        (await verify.Db.Expenses.CountAsync(expense => expense.Description == "rollback-target"))
            .Should().Be(0);
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [SkippableFact]
    public async Task SuccessfulWriter_GeneratedIdIsLinkedToSourceAndAuditedWithFinalization()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftAsync("source-link");
        var identity = Identity(draftId);
        int decoyFileId;
        await using (var arrange = Scope())
        {
            var arrangedDraft = await arrange.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
            var decoyFile = new StoredFile
            {
                PortfolioId = _portfolioId,
                FileName = "same-path-decoy.jpg",
                FilePath = arrangedDraft.FilePath,
                ContentType = "image/jpeg",
                FileSize = 100,
                EntityType = "Expense",
                UploadedAt = CommandTime.AddMinutes(-4),
            };
            arrange.Db.StoredFiles.Add(decoyFile);
            await arrange.Db.SaveChangesAsync();
            decoyFileId = decoyFile.Id;
        }

        var outcome = await UnitOfWork.ExecuteAsync(
            identity, Command(draftId, "source-link-target"), Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        outcome.Value.TargetEntityId.Should().BePositive();
        await using var verify = Scope();
        var draft = await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
        var file = await verify.Db.StoredFiles.AsNoTracking()
            .SingleAsync(row => row.Id == draft.SourceStoredFileId);
        var decoy = await verify.Db.StoredFiles.AsNoTracking().SingleAsync(row => row.Id == decoyFileId);
        draft.Status.Should().Be("Confirmed");
        draft.ConfirmedAt.Should().Be(CommandTime);
        draft.ConfirmedEntityId.Should().Be(outcome.Value.TargetEntityId);
        file.EntityType.Should().Be("Expense");
        file.EntityId.Should().Be(outcome.Value.TargetEntityId);
        decoy.EntityId.Should().BeNull("FilePath is not a source-file identity");
        (await verify.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && (row.EntityType == nameof(ScanDraft)
                || row.EntityType == nameof(StoredFile)
                || row.EntityType == nameof(Expense))))
            .Should().BeGreaterThanOrEqualTo(4, "claim, target, file, finalization, and semantic provenance are atomic");
    }

    [SkippableFact]
    public async Task TranscriptOnlyVoiceStyleDraft_FinalizesWithoutSourceFile()
    {
        SkipIfDockerUnavailable();
        var draftId = await SeedReviewingDraftWithoutSourceAsync();

        var outcome = await UnitOfWork.ExecuteAsync(
            Identity(draftId), Command(draftId, "voice-target"), Codec);

        outcome.Value.Outcome.Should().Be(ConfirmScanDraftOutcome.Confirmed);
        await using var verify = Scope();
        var draft = await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId);
        draft.SourceStoredFileId.Should().BeNull();
        draft.Status.Should().Be("Confirmed");
        draft.ConfirmedEntityId.Should().Be(outcome.Value.TargetEntityId);
        (await verify.Db.StoredFiles.CountAsync(file => file.FilePath == draft.FilePath)).Should().Be(0);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidNonNullSource_FailsAtomically(bool foreignPortfolio)
    {
        SkipIfDockerUnavailable();
        int draftId;
        await using (var arrange = Scope())
        {
            var sourcePortfolioId = _portfolioId;
            if (foreignPortfolio)
            {
                var foreign = new Portfolio
                {
                    Name = "Foreign source owner",
                    ManagementCompanyName = "Other Co",
                    TimeZone = "UTC",
                    CreatedAt = CommandTime,
                    UpdatedAt = CommandTime,
                };
                arrange.Db.Portfolios.Add(foreign);
                await arrange.Db.SaveChangesAsync();
                sourcePortfolioId = foreign.Id;
            }

            var source = new StoredFile
            {
                PortfolioId = sourcePortfolioId,
                FileName = "invalid-source.jpg",
                FilePath = $"scan/invalid-{Guid.NewGuid():N}.jpg",
                ContentType = "image/jpeg",
                FileSize = 100,
                EntityType = "Expense",
                UploadedAt = CommandTime.AddMinutes(-5),
                DeletedAt = foreignPortfolio ? null : CommandTime.AddMinutes(-1),
            };
            var draft = new ScanDraft
            {
                PortfolioId = _portfolioId,
                FilePath = source.FilePath,
                SourceStoredFile = source,
                TargetEntityType = "Expense",
                Status = "Reviewing",
                ExtractedFields = "{\"total\":{\"value\":\"125.00\"}}",
                CreatedAt = CommandTime.AddMinutes(-5),
            };
            arrange.Db.AddRange(source, draft);
            await arrange.Db.SaveChangesAsync();
            draftId = draft.Id;
            RememberPreparedFingerprint(draft);
        }
        var identity = Identity(draftId);

        var action = () => UnitOfWork.ExecuteAsync(
            identity, Command(draftId, "invalid-source-target"), Codec);

        await action.Should().ThrowAsync<AtomicReceiptInvariantException>();
        await using var verify = Scope();
        (await verify.Db.ScanDrafts.AsNoTracking().SingleAsync(row => row.Id == draftId)).Status
            .Should().Be("Reviewing");
        (await verify.Db.Expenses.CountAsync(expense => expense.Description == "invalid-source-target"))
            .Should().Be(0);
        (await verify.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private static readonly DateTime CommandTime =
        new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private ConfirmScanDraftCommand Command(int draftId, string description) =>
        new(
            _portfolioId,
            draftId,
            ConfirmedByUserId: 42,
            ConfirmedAtUtc: CommandTime,
            ExpectedDraftFingerprint: _preparedFingerprints[draftId],
            Target: new ScanConfirmationTargetData(
                ScanConfirmationTargetKind.Expense,
                Expense: new ScanExpenseTargetData(
                    Receipt(description),
                    IsPaid: true,
                    PropertyId: null,
                    UnitId: null,
                    WorkOrderId: null)));

    private static ScanReceiptData Receipt(string vendorName) =>
        new(
            vendorName, null, null, null, null, null, CommandTime,
            Subtotal: 125m, Tax: null, TaxRate: null, Tip: null, Discount: null,
            Shipping: null, Total: 125m, PaymentMethod: "Check", CardLast4: null,
            Category: ScheduleECategory.Repairs, DocumentKind: "Receipt", Notes: null,
            DueDate: null, LineItems: [], PayerName: null, CheckNumber: null, BankName: null,
            ExtraFields: []);

    private AtomicCommandIdentity Identity(int draftId, string? suffix = null) =>
        suffix is null
            ? ScanConfirmationCommandIdentity.Create(_portfolioId, draftId, "foundation-default")
            : new AtomicCommandIdentity("scan.confirm", $"{_portfolioId}:{draftId}:{suffix}");

    private async Task<int> SeedReviewingDraftAsync(string marker)
    {
        await using var scope = Scope();
        var path = $"scan/{marker}-{Guid.NewGuid():N}.jpg";
        var file = new StoredFile
        {
            PortfolioId = _portfolioId,
            FileName = marker + ".jpg",
            FilePath = path,
            ContentType = "image/jpeg",
            FileSize = 100,
            EntityType = "Expense",
            UploadedAt = CommandTime.AddMinutes(-5),
        };
        var draft = new ScanDraft
        {
            PortfolioId = _portfolioId,
            FilePath = path,
            SourceStoredFile = file,
            TargetEntityType = "Expense",
            Status = "Reviewing",
            ExtractedFields = "{\"total\":{\"value\":\"125.00\"}}",
            CreatedAt = CommandTime.AddMinutes(-5),
        };
        scope.Db.AddRange(file, draft);
        await scope.Db.SaveChangesAsync();
        RememberPreparedFingerprint(draft);
        return draft.Id;
    }

    private async Task<int> SeedReviewingDraftWithoutSourceAsync()
    {
        await using var scope = Scope();
        var draft = new ScanDraft
        {
            PortfolioId = _portfolioId,
            FilePath = $"voice://{Guid.NewGuid():N}",
            SourceStoredFileId = null,
            TargetEntityType = "Expense",
            Status = "Reviewing",
            ExtractedFields = "{\"total\":{\"value\":\"125.00\"}}",
            CreatedAt = CommandTime.AddMinutes(-5),
        };
        scope.Db.ScanDrafts.Add(draft);
        await scope.Db.SaveChangesAsync();
        RememberPreparedFingerprint(draft);
        return draft.Id;
    }

    private void RememberPreparedFingerprint(ScanDraft draft) =>
        _preparedFingerprints[draft.Id] = ScanConfirmationDraftFingerprint.Create(
            draft.TargetEntityType, draft.SourceStoredFileId, draft.ExtractedFields);

    private IAtomicUnitOfWork UnitOfWork => Services.GetRequiredService<IAtomicUnitOfWork>();
    private IServiceProvider Services => _services ?? throw new InvalidOperationException();
    private TestScope Scope() => TestScope.Create(Services);

    private void SkipIfDockerUnavailable() =>
        Skip.IfNot(_dockerAvailable, "Docker socket preflight failed; PostgreSQL scan tests skipped.");

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
                // Try the next well-known socket.
            }
        }
        return false;
    }

    private sealed class TestExpenseTargetWriter : IScanConfirmationTargetWriter
    {
        private readonly TestWriterProbe _probe;
        public TestExpenseTargetWriter(TestWriterProbe probe) => _probe = probe;
        public bool Supports(ScanConfirmationTargetKind kind) => kind == ScanConfirmationTargetKind.Expense;

        public async Task<ScanConfirmationTargetWriteResult> WriteAsync(
            ConfirmScanDraftCommand command,
            string? extractedFieldsJson,
            IAtomicWriteAttempt attempt,
            CancellationToken ct)
        {
            await _probe.BeforeWriteAsync(command.DraftId, ct);
            var data = command.Target.Expense ?? throw new InvalidOperationException();
            var expense = new Expense
            {
                PortfolioId = command.PortfolioId,
                Category = data.Receipt.Category ?? ScheduleECategory.Other,
                Description = data.Receipt.VendorName ?? "Scanned receipt",
                Status = data.IsPaid ? ExpenseStatus.Paid : ExpenseStatus.Pending,
                Amount = data.Receipt.Total ?? data.Receipt.Subtotal ?? 0m,
                IncurredAt = data.Receipt.TransactionDate ?? command.ConfirmedAtUtc,
                PaidAt = data.IsPaid ? data.Receipt.TransactionDate ?? command.ConfirmedAtUtc : null,
                ReceiptData = extractedFieldsJson,
                CreatedAt = command.ConfirmedAtUtc,
                UpdatedAt = command.ConfirmedAtUtc,
            };
            attempt.Persistence.Add(expense);
            attempt.BindSemanticAudit(expense, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Expense),
                0,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Test target created from scan draft #{command.DraftId}."));
            await attempt.FlushBusinessAsync(ct);
            return new ScanConfirmationTargetWriteResult(expense.Id, data.UnitId);
        }
    }

    private sealed class TestWriterProbe : IAtomicTransactionSafeDependency
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pausedDraftId;

        public TestWriterProbe() { }
        public void PauseDraft(int draftId) => Volatile.Write(ref _pausedDraftId, draftId);
        public Task WaitUntilEnteredAsync() => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task BeforeWriteAsync(int draftId, CancellationToken ct)
        {
            if (Volatile.Read(ref _pausedDraftId) != draftId)
            {
                return;
            }
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:scan-confirm";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class InjectedScanWriterFailure : Exception;

    private sealed class AtomicCompanionFailureInterceptor : SaveChangesInterceptor
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
                throw new InjectedScanWriterFailure();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestScope(AsyncServiceScope scope, RentalCommandDbContext db) : IAsyncDisposable
    {
        public RentalCommandDbContext Db { get; } = db;
        public static TestScope Create(IServiceProvider services)
        {
            var scope = services.CreateAsyncScope();
            return new TestScope(scope, scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>());
        }
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
