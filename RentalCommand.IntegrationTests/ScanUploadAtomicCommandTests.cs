using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Scanning;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL crash/replay/concurrency proof for scan blob admission and atomic metadata finalization.</summary>
public sealed class ScanUploadAtomicCommandTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private WorkspaceReadScope _scope;
    private static readonly Guid SessionId = Guid.Parse("85efadce-c871-4436-89bc-a959ccfa1d16");

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_scan_upload")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.Configure<UploadSettings>(settings =>
        {
            settings.MaxFileSizeBytes = 10 * 1024 * 1024;
            settings.AllowedMimeTypes = ["application/pdf", "image/jpeg", "image/png"];
        });
        services.AddSingleton<TestFileStorage>();
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<TestFileStorage>());
        services.AddSingleton<SqlProbe>();
        services.AddSingleton<AuditFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            FinalizeScanUploadCommand,
            FinalizeScanUploadResult,
            FinalizeScanUploadHandler>();
        services.AddScoped<IPendingFileUploadStore, PendingFileUploadStore>();
        services.AddScoped<IScanUploadService, ScanUploadService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<SqlProbe>(),
                    provider.GetRequiredService<AuditFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = "Scan upload tests",
            ManagementCompanyName = "Scan upload tests",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Portfolios.Add(portfolio);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        var user = new ApplicationUser
        {
            Id = 73,
            UserName = "scan-upload@example.test",
            NormalizedUserName = "SCAN-UPLOAD@EXAMPLE.TEST",
            Email = "scan-upload@example.test",
            NormalizedEmail = "SCAN-UPLOAD@EXAMPLE.TEST",
            DisplayName = "Scan Upload",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = _portfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = _portfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = _portfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = SessionId,
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        _scope = new WorkspaceReadScope(
            _portfolioId, user.Id, SessionId, accessContext.Id, accessContext.AccessRevision);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Replay_returns_canonical_result_and_changed_payload_conflicts()
    {
        SkipIfNoDocker();
        var first = await UploadAsync("single-replay", [Pdf("lease.pdf", "first")]);
        var replay = await UploadAsync("single-replay", [Pdf("lease.pdf", "first")]);

        replay.Should().BeEquivalentTo(first);
        var changed = () => UploadAsync("single-replay", [Pdf("lease.pdf", "changed")]);
        await changed.Should().ThrowAsync<UploadOperationConflictException>();

        await using var db = NewContext();
        (await db.ScanDrafts.CountAsync()).Should().Be(1);
        (await db.StoredFiles.CountAsync()).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "scan-upload.finalize")).Should().Be(1);
        (await db.OutboxMessages.CountAsync(message =>
            message.MessageType == "data-update")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Storage_failure_after_admission_leaves_no_business_rows_and_retry_recovers()
    {
        SkipIfNoDocker();
        Storage.FailOnUploadNumber = 1;

        var interrupted = () => UploadAsync("before-blob", [Pdf("receipt.pdf", "before")]);
        await interrupted.Should().ThrowAsync<InjectedStorageFailure>();

        await using (var db = NewContext())
        {
            (await db.PendingFileUploads.CountAsync(upload =>
                upload.State == PendingFileUploadState.Prepared)).Should().Be(1);
            (await db.ScanDrafts.CountAsync()).Should().Be(0);
            (await db.StoredFiles.CountAsync()).Should().Be(0);
            (await db.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == "scan-upload.finalize")).Should().Be(0);
        }

        Storage.FailOnUploadNumber = null;
        var recovered = await UploadAsync("before-blob", [Pdf("receipt.pdf", "before")]);
        recovered.Drafts.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Finalize_failure_rolls_back_every_row_while_deterministic_blob_remains_retryable()
    {
        SkipIfNoDocker();
        Failure.FailAtomicAudit = true;

        var interrupted = () => UploadAsync("after-blob", [Pdf("invoice.pdf", "after")]);
        var failure = await interrupted.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InjectedAuditFailure>();
        Failure.FailAtomicAudit = false;

        await using (var db = NewContext())
        {
            var pending = await db.PendingFileUploads.SingleAsync(upload =>
                upload.State == PendingFileUploadState.Prepared);
            Storage.Paths.Should().Contain(pending.StoragePath);
            (await db.ScanDrafts.CountAsync()).Should().Be(0);
            (await db.StoredFiles.CountAsync()).Should().Be(0);
            (await db.AtomicAuditLogs.CountAsync(audit =>
                audit.CommandType == "scan-upload.finalize")).Should().Be(0);
            (await db.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == "scan-upload.finalize")).Should().Be(0);
        }

        var recovered = await UploadAsync("after-blob", [Pdf("invoice.pdf", "after")]);
        recovered.Drafts.Should().ContainSingle();
        await using var finalDb = NewContext();
        (await finalDb.PendingFileUploads.CountAsync(upload =>
            upload.State == PendingFileUploadState.Finalized)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Partial_batch_blob_failure_creates_no_batch_and_retry_finalizes_all_metadata()
    {
        SkipIfNoDocker();
        Storage.ResetUploadCounter();
        Storage.FailOnUploadNumber = 2;
        var files = new[] { Pdf("one.pdf", "one"), Pdf("two.pdf", "two") };

        var interrupted = () => UploadAsync("batch-partial", files, createBatch: true, batchName: "Imports");
        await interrupted.Should().ThrowAsync<InjectedStorageFailure>();

        await using (var db = NewContext())
        {
            (await db.PendingFileUploads.CountAsync(upload =>
                upload.State == PendingFileUploadState.Prepared)).Should().Be(2);
            (await db.ScanBatches.CountAsync()).Should().Be(0);
            (await db.ScanDrafts.CountAsync()).Should().Be(0);
        }

        Storage.FailOnUploadNumber = null;
        var recovered = await UploadAsync(
            "batch-partial", files, createBatch: true, batchName: "Imports");
        recovered.BatchId.Should().NotBeNull();
        recovered.Drafts.Should().HaveCount(2);
        await using var finalDb = NewContext();
        (await finalDb.ScanBatches.CountAsync()).Should().Be(1);
        (await finalDb.ScanDrafts.CountAsync()).Should().Be(2);
        (await finalDb.StoredFiles.CountAsync()).Should().Be(2);
    }

    [SkippableFact]
    public async Task Cross_portfolio_capture_context_is_rejected_before_scan_rows_are_persisted()
    {
        SkipIfNoDocker();
        await using var seed = NewContext();
        var now = DateTime.UtcNow;
        var otherPortfolio = new Portfolio
        {
            Name = "Other workspace",
            ManagementCompanyName = "Other workspace",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var otherProperty = new Property
        {
            Portfolio = otherPortfolio,
            Name = "Outside property",
            AddressLine1 = "1 Outside Way",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = now,
            UpdatedAt = now,
        };
        seed.AddRange(otherPortfolio, otherProperty);
        await seed.SaveChangesAsync();

        var captureContext = new ScanCaptureContextData(
            Experience: null,
            AccessContextId: null,
            AccessRevision: null,
            PropertyId: otherProperty.Id,
            UnitId: null,
            LeaseManagementId: null,
            LeaseAgreementId: null,
            TenantAccountId: null,
            TenantLedgerEntryId: null,
            WorkOrderId: null,
            ApplicationId: null,
            RentalListingId: null,
            SourceLabel: "outside property");

        var rejected = () => UploadAsync(
            "cross-portfolio-context",
            [Pdf("outside.pdf", "outside")],
            captureContext: captureContext);
        await rejected.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*not authorized*");

        await using var verify = NewContext();
        (await verify.ScanDrafts.CountAsync()).Should().Be(0);
        (await verify.StoredFiles.CountAsync()).Should().Be(0);
        (await verify.ScanBatches.CountAsync()).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "scan-upload.finalize")).Should().Be(0);
    }

    [SkippableFact]
    public async Task Revoked_session_is_rejected_inside_finalization_transaction()
    {
        SkipIfNoDocker();
        await using (var revoke = NewContext())
        {
            await revoke.AuthSessions
                .Where(session => session.Id == _scope.SessionId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(session => session.Status, AuthSessionStatus.Revoked)
                    .SetProperty(session => session.RevokedAtUtc, DateTime.UtcNow));
        }

        var rejected = () => UploadAsync(
            "revoked-session",
            [Pdf("revoked.pdf", "revoked")]);
        await rejected.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*not authorized*");

        await using var verify = NewContext();
        (await verify.ScanDrafts.CountAsync()).Should().Be(0);
        (await verify.StoredFiles.CountAsync()).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "scan-upload.finalize")).Should().Be(0);
    }

    [SkippableFact]
    public async Task Concurrent_same_operation_commits_one_set_and_admission_matching_is_one_db_query()
    {
        SkipIfNoDocker();
        Probe.Clear();
        var files = new[] { Pdf("a.pdf", "a"), Pdf("b.pdf", "b") };

        var results = await Task.WhenAll(
            UploadAsync("concurrent", files, createBatch: true),
            UploadAsync("concurrent", files, createBatch: true));

        results[1].Should().BeEquivalentTo(results[0]);
        await using var db = NewContext();
        (await db.ScanBatches.CountAsync()).Should().Be(1);
        (await db.ScanDrafts.CountAsync()).Should().Be(2);
        (await db.StoredFiles.CountAsync()).Should().Be(2);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "scan-upload.finalize")).Should().Be(1);

        Probe.Commands.Count(sql =>
            sql.Contains("scan-upload-admission-lock", StringComparison.Ordinal))
            .Should().Be(1, "the winning finalizer matches and locks the whole admission set in one PostgreSQL statement");
    }

    [SkippableFact]
    public async Task Cleanup_claim_cannot_win_while_finalizer_holds_the_complete_admission_set()
    {
        SkipIfNoDocker();
        Probe.HoldAdmissionLock();
        var finalizing = UploadAsync("cleanup-fence", [Pdf("locked.pdf", "locked")]);

        try
        {
            await Probe.WaitForAdmissionLockAsync();
            await using var scope = _services!.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>();
            var claims = await store.ClaimExpiredAsync(
                "cleanup-lock-test",
                TimeSpan.Zero,
                TimeSpan.FromMinutes(5),
                batchSize: 10);
            claims.Should().BeEmpty("FOR UPDATE SKIP LOCKED must skip admissions owned by the finalizer");
        }
        finally
        {
            Probe.ReleaseAdmissionLock();
        }

        var finalized = await finalizing;
        finalized.Drafts.Should().ContainSingle();

        await using var verifyScope = _services!.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var admission = await verifyDb.PendingFileUploads.SingleAsync();
        admission.State.Should().Be(PendingFileUploadState.Finalized);
        admission.CleanupClaimToken.Should().BeNull();
        var abandoned = await verifyScope.ServiceProvider
            .GetRequiredService<IPendingFileUploadStore>()
            .MarkAbandonedAsync(admission.Id, "not-the-owner", Guid.NewGuid());
        abandoned.Should().Be(0, "a finalized admission cannot later be abandoned");
    }

    [SkippableFact]
    public async Task Cleanup_claim_uses_database_clock_and_stale_owner_token_cannot_mutate_after_takeover()
    {
        SkipIfNoDocker();
        PendingFileUploadAdmission admission;
        await using (var prepareScope = _services!.CreateAsyncScope())
        {
            admission = await prepareScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>().PrepareAsync(
                _portfolioId,
                actorScopeId: 73,
                purpose: "scan-source",
                clientOperationId: "cleanup-db-clock",
                requestFingerprint: "cleanup-db-clock-fingerprint",
                fileName: "orphan.pdf",
                contentType: "application/pdf",
                sizeBytes: 12,
                nowUtc: DateTime.UtcNow.AddDays(-2));
        }

        PendingFileUploadCleanupClaim first;
        await using (var firstScope = _services!.CreateAsyncScope())
        {
            first = (await firstScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>()
                .ClaimExpiredAsync(
                    "cleanup-a", TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), batchSize: 1))
                .Single();
        }

        await using (var expireScope = _services!.CreateAsyncScope())
        {
            var db = expireScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE "PendingFileUploads"
                SET "CleanupClaimExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = @id
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = admission.Id;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync();
        }

        PendingFileUploadCleanupClaim replacement;
        await using (var replacementScope = _services!.CreateAsyncScope())
        {
            replacement = (await replacementScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>()
                .ClaimExpiredAsync(
                    "cleanup-b", TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), batchSize: 1))
                .Single();
            replacement.ClaimToken.Should().NotBe(first.ClaimToken);
        }

        await using var finishScope = _services!.CreateAsyncScope();
        var store = finishScope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>();
        (await store.ReleaseCleanupClaimAsync(
            first.Id, first.ClaimOwner, first.ClaimToken)).Should().Be(0);
        (await store.MarkAbandonedAsync(
            replacement.Id, replacement.ClaimOwner, replacement.ClaimToken)).Should().Be(1);
    }

    [SkippableTheory]
    [InlineData("foreign-actor")]
    [InlineData("wrong-purpose")]
    [InlineData("wrong-operation")]
    public async Task Foreign_or_mismatched_admission_is_rejected_without_atomic_companions(string mismatch)
    {
        SkipIfNoDocker();
        const string operationId = "security-boundary";
        const string fingerprint = "security-fingerprint";
        const string expectedPerFileOperation = operationId + ":0:source";
        var actor = mismatch == "foreign-actor" ? 99 : 73;
        var purpose = mismatch == "wrong-purpose" ? "scan-thumbnail" : "scan-source";
        var actualOperation = mismatch == "wrong-operation"
            ? expectedPerFileOperation + ":foreign"
            : expectedPerFileOperation;

        PendingFileUploadAdmission admission;
        await using (var scope = _services!.CreateAsyncScope())
        {
            admission = await scope.ServiceProvider.GetRequiredService<IPendingFileUploadStore>().PrepareAsync(
                _portfolioId,
                actor,
                purpose,
                actualOperation,
                fingerprint,
                "security.pdf",
                "application/pdf",
                sizeBytes: 24,
                nowUtc: DateTime.UtcNow);
        }

        var command = new FinalizeScanUploadCommand(
            PortfolioId: _portfolioId,
            UploadedByUserId: 73,
            AuthSessionId: _scope.SessionId,
            AccessContextId: _scope.AccessContextId,
            ExpectedAccessRevision: _scope.AccessRevision,
            ClientOperationId: operationId,
            RequestFingerprint: fingerprint,
            TargetEntityType: "LeaseAgreement",
            CreateBatch: false,
            BatchName: null,
            UploadedAtUtc: DateTime.UtcNow,
            Files:
            [
                new FinalizeScanUploadFile(
                    admission.Id,
                    admission.StoragePath,
                    "security.pdf",
                    "application/pdf",
                    24,
                    SourceSha256: "source-sha",
                    ThumbnailPendingUploadId: null,
                    ThumbnailStoragePath: null,
                    ThumbnailFileName: null,
                    ThumbnailSizeBytes: null,
                    ThumbnailSha256: null)
            ],
            CaptureContext: CanonicalCaptureContext());
        var identity = new AtomicCommandIdentity(
            "scan-upload.finalize",
            $"security:{mismatch}");
        var codec = new AtomicJsonResultCodec<FinalizeScanUploadResult>(
            "scan-upload.finalize.result.v1");

        var rejected = () => _services!.GetRequiredService<IAtomicUnitOfWork>()
            .ExecuteAsync(identity, command, codec);
        await rejected.Should().ThrowAsync<InvalidOperationException>();

        await using var db = NewContext();
        (await db.ScanDrafts.CountAsync()).Should().Be(0);
        (await db.ScanBatches.CountAsync()).Should().Be(0);
        (await db.StoredFiles.CountAsync()).Should().Be(0);
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    private async Task<FinalizeScanUploadResult> UploadAsync(
        string operationId,
        IReadOnlyList<ScanUploadFilePayload> files,
        bool createBatch = false,
        string? batchName = null,
        ScanCaptureContextData? captureContext = null)
    {
        await using var scope = _services!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IScanUploadService>();
        var canonicalContext = (captureContext ?? CanonicalCaptureContext()) with
        {
            AccessContextId = _scope.AccessContextId,
            AccessRevision = _scope.AccessRevision,
        };
        return await service.UploadAsync(
            _scope, operationId, "LeaseAgreement", createBatch, batchName,
            canonicalContext, files);
    }

    private ScanCaptureContextData CanonicalCaptureContext() => new(
        WorkspaceExperience.Management,
        _scope.AccessContextId,
        _scope.AccessRevision,
        null, null, null, null, null, null, null, null, null,
        "integration test");

    private static ScanUploadFilePayload Pdf(string fileName, string marker) => new(
        Encoding.ASCII.GetBytes($"%PDF-1.4\n{marker}\n%%EOF"),
        fileName,
        "application/pdf");

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private TestFileStorage Storage => _services!.GetRequiredService<TestFileStorage>();
    private SqlProbe Probe => _services!.GetRequiredService<SqlProbe>();
    private AuditFailureInterceptor Failure => _services!.GetRequiredService<AuditFailureInterceptor>();

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; scan-upload PostgreSQL proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 73;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class TestFileStorage : IFileStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _paths = new(StringComparer.Ordinal);
        private int _uploadCount;
        public int? FailOnUploadNumber { get; set; }
        public IReadOnlyCollection<string> Paths => _paths.Keys.ToArray();
        public void ResetUploadCounter() => Interlocked.Exchange(ref _uploadCount, 0);

        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException("Scan upload must use its reserved path.");

        public async Task UploadAtAsync(
            Stream content,
            string storagePath,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _uploadCount);
            if (FailOnUploadNumber == count) throw new InjectedStorageFailure();
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            _paths[storagePath] = buffer.ToArray();
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(_paths[path], writable: false));

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _paths.TryRemove(path, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class SqlProbe : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
        private TaskCompletionSource<bool>? _admissionLockEntered;
        private TaskCompletionSource<bool>? _releaseAdmissionLock;
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("scan-upload-admission-lock", StringComparison.Ordinal)
                && _admissionLockEntered is { } entered
                && _releaseAdmissionLock is { } release)
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public void HoldAdmissionLock()
        {
            _admissionLockEntered = NewSignal();
            _releaseAdmissionLock = NewSignal();
        }

        public Task WaitForAdmissionLockAsync() =>
            (_admissionLockEntered?.Task
                ?? throw new InvalidOperationException("Admission lock hold was not enabled."))
            .WaitAsync(TimeSpan.FromSeconds(15));

        public void ReleaseAdmissionLock()
        {
            _releaseAdmissionLock?.TrySetResult(true);
            _admissionLockEntered = null;
            _releaseAdmissionLock = null;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AuditFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfNeeded(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfNeeded(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfNeeded(DbCommand command)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InjectedAuditFailure();
            }
        }
    }

    private sealed class InjectedStorageFailure : Exception { }
    private sealed class InjectedAuditFailure : Exception { }
}
