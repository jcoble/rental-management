using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Outbox;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Outbox;
using RentalCommand.Engine.Workers;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for general StoredFile create/delete command boundaries.</summary>
public sealed class StoredDocumentAtomicCommandTests : IAsyncLifetime
{
    private const int ActorUserId = 73;
    private const string ContentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime BusinessAtUtc =
        new(2027, 1, 14, 5, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _unitId;
    private int _otherUnitId;
    private Guid _sessionId;
    private int _accessContextId;
    private long _accessRevision;

    [Fact]
    public void Document_target_contract_is_canonical_and_supports_bigint_ledger_ids()
    {
        typeof(StoredFile).GetProperty(nameof(StoredFile.EntityId))!.PropertyType
            .Should().Be(typeof(long?));

        Enum.GetNames<StoredDocumentTarget>().Should().Contain([
            nameof(StoredDocumentTarget.LeaseAgreement),
            nameof(StoredDocumentTarget.LegalDocumentArtifact),
            nameof(StoredDocumentTarget.TenantAccount),
            nameof(StoredDocumentTarget.TenantLedgerEntry),
            nameof(StoredDocumentTarget.SecurityDepositAccount),
        ]);
        Enum.GetNames<StoredDocumentTarget>().Should()
            .NotContain("Lease")
            .And.NotContain("Payment")
            .And.NotContain("SecurityDeposit");
    }

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_stored_documents")
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
        services.AddSingleton<TimeProvider>(
            new FixedTimeProvider(new DateTimeOffset(BusinessAtUtc)));
        services.AddSingleton<CommandProbe>();
        services.AddSingleton<AuditFailureInterceptor>();
        services.AddSingleton<CapturingFileStorage>();
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<CapturingFileStorage>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddPendingFileUploadStore();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandProbe>(),
                    provider.GetRequiredService<AuditFailureInterceptor>()));
        services.AddScoped<IDocumentService, DocumentService>();
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        (_portfolioId, _unitId) = await SeedUnitAsync(db, "Primary");
        (_, _otherUnitId) = await SeedUnitAsync(db, "Other");
        (_sessionId, _accessContextId, _accessRevision) = await SeedManagementAccessAsync(db);
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Create_replay_commits_one_row_and_cleans_only_replay_upload()
    {
        SkipIfNoDocker();
        Storage.Add("blob-first");

        var first = await CreateAsync("upload-one", "blob-first");
        var replay = await CreateAsync("upload-one", "blob-first");

        replay.Should().BeEquivalentTo(first);
        first!.Id.Should().BePositive();
        Storage.Deleted.Should().BeEmpty();
        Storage.Contains("blob-first").Should().BeTrue();
        await using var db = NewContext();
        (await db.StoredFiles.CountAsync(file => file.Id == first.Id)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt => receipt.CommandType == "stored-document.create"))
            .Should().Be(1);
        var audits = await db.AtomicAuditLogs
            .Where(audit => audit.CommandType == "stored-document.create")
            .OrderBy(audit => audit.Id)
            .ToListAsync();
        audits.Should().Contain(audit => audit.EntityType == nameof(StoredFile) && audit.EntityId == first.Id);
        audits.Should().Contain(audit => audit.EntityType == nameof(Unit) && audit.EntityId == _unitId);
    }

    [SkippableFact]
    public async Task Create_replay_reauthorizes_and_denies_expired_assignment_before_returning_receipt()
    {
        SkipIfNoDocker();
        var businessAtUtc = DateTime.UtcNow.AddMinutes(-30);
        await SetManagementAccessWindowAsync(businessAtUtc.AddMinutes(-1), effectiveToUtc: null);

        Storage.Add("blob-create-expired-replay");
        var first = await ExecuteCreateCommandAsync(
            "upload-create-expired-replay",
            "blob-create-expired-replay",
            businessAtUtc);
        first.Value.Outcome.Should().Be(StoredDocumentMutationOutcome.Created);

        await SetManagementAccessWindowAsync(
            businessAtUtc.AddMinutes(-1),
            businessAtUtc.AddMinutes(1));

        await FluentActions.Invoking(() =>
                ExecuteCreateCommandAsync(
                    "upload-create-expired-replay",
                    "blob-create-expired-replay",
                    businessAtUtc))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*document upload*");

        await using var verify = NewContext();
        (await verify.StoredFiles.CountAsync(file => file.Id == first.Value.StoredFileId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "stored-document.create")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Create_keeps_business_upload_time_separate_from_live_security_time()
    {
        SkipIfNoDocker();
        Storage.Add("blob-clock-separation");

        var created = await CreateAsync("upload-clock-separation", "blob-clock-separation");

        created.Should().NotBeNull();
        created!.UploadedAt.Should().Be(BusinessAtUtc);
        await using var db = NewContext();
        var membership = await db.WorkspaceMemberships.SingleAsync(
            candidate => candidate.AccessContextId == _accessContextId);
        var session = await db.AuthSessions.SingleAsync(candidate => candidate.Id == _sessionId);
        membership.EffectiveFromUtc.Should().BeBefore(DateTime.UtcNow);
        session.ExpiresAtUtc.Should().BeBefore(BusinessAtUtc);
        (await db.StoredFiles.CountAsync(file =>
            file.PortfolioId == _portfolioId && file.EntityId == _unitId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Different_create_operations_are_deliberate_and_cross_scope_is_denied()
    {
        SkipIfNoDocker();
        Storage.Add("blob-second");
        Storage.Add("blob-cross-scope");

        var second = await CreateAsync("upload-two", "blob-second");
        var crossScope = await CreateAsync(
            "upload-cross-scope",
            "blob-cross-scope",
            entityId: _otherUnitId);

        second.Should().NotBeNull();
        crossScope.Should().BeNull();
        Storage.Deleted.Should().NotContain("blob-cross-scope", "durable pending ownership is scavenged after retention");
        await using var db = NewContext();
        (await db.StoredFiles.CountAsync(file => file.PortfolioId == _portfolioId)).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt => receipt.CommandType == "stored-document.create"))
            .Should().Be(2, "the denied result is also canonical and replay-safe");
    }

    [SkippableFact]
    public async Task Duplicate_content_for_same_active_target_reuses_existing_document_and_cleans_retry_blob()
    {
        SkipIfNoDocker();
        Storage.Add("blob-original");
        Storage.Add("blob-duplicate");

        var original = await CreateAsync("upload-original", "blob-original");
        var duplicate = await CreateAsync("upload-duplicate", "blob-duplicate");

        duplicate.Should().BeEquivalentTo(original);
        await using var db = NewContext();
        (await db.StoredFiles.CountAsync(file =>
            file.PortfolioId == _portfolioId
            && file.EntityType == nameof(StoredDocumentTarget.Unit)
            && file.EntityId == _unitId
            && file.ContentSha256 == ContentHash)).Should().Be(1);
        (await db.PendingFileUploads.CountAsync(upload =>
            upload.PortfolioId == _portfolioId
            && upload.State == PendingFileUploadState.Finalized
            && upload.StoredFileId == original!.Id)).Should().Be(2);
        var cleanup = await db.OutboxMessages.SingleAsync(message =>
            message.IdempotencyKey.StartsWith("stored-document-duplicate-upload:"));
        cleanup.MessageType.Should().Be("blob-delete");
        cleanup.Payload.Should().Contain("blob-duplicate");

        await RunOutboxWorkerAsync();

        await using var dispatched = NewContext();
        var completed = await dispatched.OutboxMessages.SingleAsync(message => message.Id == cleanup.Id);
        completed.FailureKind.Should().BeNull($"dispatcher failure: {completed.LastError}");
        completed.AcceptedAtUtc.Should().NotBeNull();
        Storage.Deleted.Should().ContainSingle().Which.Should().Be("blob-duplicate");
        using var payload = JsonDocument.Parse(cleanup.Payload);
        payload.RootElement.GetProperty("storedFileId").GetInt32().Should().Be(original!.Id);
    }

    [SkippableFact]
    public async Task Audit_failure_rolls_back_row_receipt_and_unit_audit_then_compensates_upload()
    {
        SkipIfNoDocker();
        Storage.Add("blob-rollback");
        int receiptCountBefore;
        int auditCountBefore;
        await using (var before = NewContext())
        {
            receiptCountBefore = await before.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == "stored-document.create");
            auditCountBefore = await before.AtomicAuditLogs.CountAsync(audit =>
                audit.CommandType == "stored-document.create");
        }
        Failure.FailAtomicAudit = true;

        var act = () => CreateAsync("upload-rollback", "blob-rollback");
        await act.Should().ThrowAsync<DbUpdateException>();
        Failure.FailAtomicAudit = false;

        Storage.Deleted.Should().NotContain("blob-rollback", "durable pending ownership survives a crash for retry/scavenging");
        await using var db = NewContext();
        (await db.StoredFiles.CountAsync(file => file.FilePath == "blob-rollback")).Should().Be(0);
        (await db.AtomicCommandReceipts.CountAsync(receipt => receipt.CommandType == "stored-document.create"))
            .Should().Be(receiptCountBefore);
        (await db.AtomicAuditLogs.CountAsync(audit => audit.CommandType == "stored-document.create"))
            .Should().Be(auditCountBefore);
    }

    [SkippableFact]
    public async Task Delete_replay_soft_deletes_once_and_stages_one_recoverable_cleanup_intent()
    {
        SkipIfNoDocker();
        Storage.Add("blob-delete");
        var created = await CreateAsync("upload-for-delete", "blob-delete");
        created.Should().NotBeNull();

        await using var scope = _services!.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();
        var first = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: created!.Id,
            userId: ActorUserId,
            tenantId: null,
            isStaff: true,
            staffScope: ManagementScope(),
            clientOperationId: "delete-one");
        var replay = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: created.Id,
            userId: ActorUserId,
            tenantId: null,
            isStaff: true,
            staffScope: ManagementScope(),
            clientOperationId: "delete-one");

        first.Should().BeTrue();
        replay.Should().BeTrue();
        Storage.Deleted.Should().NotContain("blob-delete", "storage I/O is deferred until after commit");
        await using var db = NewContext();
        (await db.StoredFiles.IgnoreQueryFilters().CountAsync(file =>
            file.Id == created.Id && file.DeletedAt != null)).Should().Be(1);
        var cleanup = await db.OutboxMessages.SingleAsync(message => message.IdempotencyKey == $"stored-file-delete:{created.Id}");
        cleanup.MessageType.Should().Be("blob-delete");
        cleanup.Payload.Should().Contain("blob-delete");
        (await db.AtomicCommandReceipts.CountAsync(receipt => receipt.CommandType == "stored-document.delete"))
            .Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(audit => audit.CommandType == "stored-document.delete"))
            .Should().Be(2, "the file mutation and Unit semantic history commit together");
    }

    [SkippableFact]
    public async Task Delete_replay_reauthorizes_and_denies_expired_assignment_before_returning_receipt()
    {
        SkipIfNoDocker();
        var businessAtUtc = DateTime.UtcNow.AddMinutes(-30);
        await SetManagementAccessWindowAsync(businessAtUtc.AddMinutes(-1), effectiveToUtc: null);

        Storage.Add("blob-delete-expired-replay");
        var created = await ExecuteCreateCommandAsync(
            "upload-delete-expired-replay",
            "blob-delete-expired-replay",
            businessAtUtc);
        created.Value.Outcome.Should().Be(StoredDocumentMutationOutcome.Created);
        var first = await ExecuteDeleteCommandAsync(
            created.Value.StoredFileId,
            "delete-expired-replay",
            businessAtUtc);
        first.Value.Outcome.Should().Be(StoredDocumentMutationOutcome.Deleted);

        await SetManagementAccessWindowAsync(
            businessAtUtc.AddMinutes(-1),
            businessAtUtc.AddMinutes(1));

        await FluentActions.Invoking(() => ExecuteDeleteCommandAsync(
                created.Value.StoredFileId,
                "delete-expired-replay",
                businessAtUtc))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*remove this document*");

        await using var db = NewContext();
        (await db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == $"stored-file-delete:{created.Value.StoredFileId}")).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "stored-document.delete")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Tenant_delete_replay_authorizes_against_ignored_filter_work_order_target()
    {
        SkipIfNoDocker();
        int storedFileId;
        int tenantId;
        await using (var seed = NewContext())
        {
            var propertyId = await seed.Units
                .Where(unit => unit.Id == _unitId)
                .Select(unit => unit.PropertyId)
                .SingleAsync();
            var tenant = new Tenant
            {
                PortfolioId = _portfolioId,
                FirstName = "Tenant",
                LastName = "Replay",
                CreatedAt = BusinessAtUtc,
                UpdatedAt = BusinessAtUtc,
            };
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();

            var workOrder = new WorkOrder
            {
                PortfolioId = _portfolioId,
                PropertyId = propertyId,
                UnitId = _unitId,
                TenantId = tenant.Id,
                Title = "Tenant upload target",
                Description = "Tenant upload target",
                RequestedAt = BusinessAtUtc,
                UpdatedAt = BusinessAtUtc,
            };
            seed.WorkOrders.Add(workOrder);
            await seed.SaveChangesAsync();

            var storedFile = new StoredFile
            {
                PortfolioId = _portfolioId,
                EntityType = nameof(StoredDocumentTarget.WorkOrder),
                EntityId = workOrder.Id,
                FileName = "tenant-work-order.pdf",
                FilePath = "blob-tenant-delete-replay",
                ContentType = "application/pdf",
                FileSize = 42,
                ContentSha256 = ContentHash,
                UploadedAt = BusinessAtUtc,
            };
            seed.StoredFiles.Add(storedFile);
            await seed.SaveChangesAsync();
            storedFileId = storedFile.Id;
            tenantId = tenant.Id;
        }

        await using var scope = _services!.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();
        var first = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: storedFileId,
            userId: ActorUserId,
            tenantId: tenantId,
            isStaff: false,
            staffScope: null,
            clientOperationId: "tenant-delete-replay");
        var replay = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: storedFileId,
            userId: ActorUserId,
            tenantId: tenantId,
            isStaff: false,
            staffScope: null,
            clientOperationId: "tenant-delete-replay");

        first.Should().BeTrue();
        replay.Should().BeTrue();
        await using var db = NewContext();
        (await db.StoredFiles.IgnoreQueryFilters().CountAsync(file =>
            file.Id == storedFileId && file.DeletedAt != null)).Should().Be(1);
        (await db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == $"stored-file-delete:{storedFileId}")).Should().Be(1);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "stored-document.delete")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Different_delete_operation_against_deleted_document_returns_not_found_without_side_effects()
    {
        SkipIfNoDocker();
        Storage.Add("blob-delete-different-key");
        var created = await CreateAsync("upload-for-delete-different-key", "blob-delete-different-key");
        created.Should().NotBeNull();

        await using var scope = _services!.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();
        var deleted = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: created!.Id,
            userId: ActorUserId,
            tenantId: null,
            isStaff: true,
            staffScope: ManagementScope(),
            clientOperationId: "delete-original-key");
        var differentKey = await documents.DeleteAsync(
            portfolioId: _portfolioId,
            id: created.Id,
            userId: ActorUserId,
            tenantId: null,
            isStaff: true,
            staffScope: ManagementScope(),
            clientOperationId: "delete-different-key");

        deleted.Should().BeTrue();
        differentKey.Should().BeFalse();
        await using var db = NewContext();
        (await db.StoredFiles.IgnoreQueryFilters().CountAsync(file =>
            file.Id == created.Id && file.DeletedAt != null)).Should().Be(1);
        (await db.OutboxMessages.CountAsync(message =>
            message.IdempotencyKey == $"stored-file-delete:{created.Id}")).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(audit => audit.CommandType == "stored-document.delete"))
            .Should().Be(2, "the second key records a canonical NotFound receipt without mutating or staging cleanup");
        (await db.AtomicCommandReceipts.CountAsync(receipt => receipt.CommandType == "stored-document.delete"))
            .Should().Be(2, "the deliberate second key records its own NotFound result");
    }

    [SkippableFact]
    public async Task Authorization_queries_are_server_translated()
    {
        SkipIfNoDocker();
        Probe.Clear();
        Storage.Add("blob-sql-proof");

        await CreateAsync("upload-sql-proof", "blob-sql-proof");

        Probe.Commands.Count(sql =>
            sql.Contains("SELECT EXISTS", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("FROM \"Units\"", StringComparison.Ordinal)
            && sql.Contains("\"Properties\"", StringComparison.Ordinal)
            && sql.Contains("\"PortfolioId\"", StringComparison.Ordinal)
            && sql.Contains("@", StringComparison.Ordinal))
            .Should().Be(1, "target eligibility is one parameterized DB-side statement even when EF wraps filtered tables");
    }

    private CapturingFileStorage Storage => _services!.GetRequiredService<CapturingFileStorage>();
    private CommandProbe Probe => _services!.GetRequiredService<CommandProbe>();
    private AuditFailureInterceptor Failure => _services!.GetRequiredService<AuditFailureInterceptor>();

    private async Task<RentalCommand.Api.DTOs.DocumentDto?> CreateAsync(
        string operationId,
        string storagePath,
        int? entityId = null)
    {
        var fingerprint = ContentHash;
        var pendingUploadId = await PreparePendingUploadAsync(operationId, storagePath, fingerprint);
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentService>().CreateAsync(
            pendingUploadId: pendingUploadId,
            portfolioId: _portfolioId,
            target: StoredDocumentTarget.Unit,
            entityId: entityId ?? _unitId,
            userId: ActorUserId,
            tenantId: null,
            isStaff: true,
            staffScope: ManagementScope(),
            clientOperationId: operationId,
            requestFingerprint: fingerprint,
            contentSha256: ContentHash,
            fileName: "lease.pdf",
            contentType: "application/pdf",
            sizeBytes: 42,
            storagePath: storagePath);
    }

    private async Task<AtomicCommandOutcome<CreateStoredDocumentResult>> ExecuteCreateCommandAsync(
        string operationId,
        string storagePath,
        DateTime uploadedAtUtc)
    {
        var fingerprint = ContentHash;
        var pendingUploadId = await PreparePendingUploadAsync(operationId, storagePath, fingerprint);
        var command = new CreateStoredDocumentCommand(
                pendingUploadId,
                _portfolioId,
                StoredDocumentTarget.Unit,
                _unitId,
                ActorUserId,
                null,
                true,
                operationId,
                fingerprint,
                ContentHash,
                "lease.pdf",
                storagePath,
                "application/pdf",
                42,
                uploadedAtUtc,
                ManagementAccess());
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>().ExecuteAsync(
            StoredDocumentWriteSupport.CreateIdempotencyKey(
                _portfolioId, ActorUserId, Digest(operationId)),
            StoredDocumentWriteSupport.Create(
                command,
                (request, context, ct) => CreateStoredDocumentHandler.ExecuteAsync(
                    db, request, context, ct),
                (request, context, ct) => CreateStoredDocumentHandler.AuthorizeAsync(
                    db, request, context, ct)));
    }

    private Task<AtomicCommandOutcome<DeleteStoredDocumentResult>> ExecuteDeleteCommandAsync(
        int storedFileId,
        string operationId,
        DateTime deletedAtUtc) =>
        ExecuteDeleteWriteAsync(
            new DeleteStoredDocumentCommand(
                _portfolioId,
                storedFileId,
                ActorUserId,
                null,
                true,
                operationId,
                deletedAtUtc,
                ManagementAccess()),
            operationId);

    private async Task<AtomicCommandOutcome<DeleteStoredDocumentResult>> ExecuteDeleteWriteAsync(
        DeleteStoredDocumentCommand command,
        string operationId)
    {
        await using var scope = _services!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>().ExecuteAsync(
            StoredDocumentWriteSupport.DeleteIdempotencyKey(
                command.PortfolioId, command.StoredFileId, Digest(operationId)),
            StoredDocumentWriteSupport.Delete(
                command,
                (request, context, ct) => DeleteStoredDocumentHandler.ExecuteAsync(
                    db, request, context, ct),
                (request, context, ct) => DeleteStoredDocumentHandler.AuthorizeAsync(
                    db, request, context, ct)));
    }

    private async Task<Guid> PreparePendingUploadAsync(
        string operationId,
        string storagePath,
        string fingerprint)
    {
        var pendingUploadId = Guid.NewGuid();
        await using var db = NewContext();
        var operationHash = Digest(operationId);
        var existing = await db.PendingFileUploads.AsNoTracking()
            .SingleOrDefaultAsync(upload => upload.PortfolioId == _portfolioId
                && upload.ActorScopeId == ActorUserId
                && upload.Purpose == "stored-document"
                && upload.OperationKeyHash == operationHash);
        if (existing is not null)
        {
            return existing.Id;
        }

        db.PendingFileUploads.Add(new PendingFileUpload
        {
            Id = pendingUploadId,
            PortfolioId = _portfolioId,
            ActorScopeId = ActorUserId,
            Purpose = "stored-document",
            OperationKeyHash = operationHash,
            RequestFingerprint = fingerprint,
            StoragePath = storagePath,
            FileName = "lease.pdf",
            ContentType = "application/pdf",
            SizeBytes = 42,
            State = PendingFileUploadState.Prepared,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return pendingUploadId;
    }

    private async Task SetManagementAccessWindowAsync(DateTime effectiveFromUtc, DateTime? effectiveToUtc)
    {
        await using var db = NewContext();
        var membership = await db.WorkspaceMemberships.SingleAsync(row =>
            row.AccessContextId == _accessContextId);
        var assignment = await db.MembershipRoleAssignments.SingleAsync(row =>
            row.WorkspaceMembership != null &&
            row.WorkspaceMembership.AccessContextId == _accessContextId);
        membership.EffectiveFromUtc = effectiveFromUtc;
        membership.EffectiveToUtc = effectiveToUtc;
        assignment.EffectiveFromUtc = effectiveFromUtc;
        assignment.EffectiveToUtc = effectiveToUtc;
        await db.SaveChangesAsync();
    }

    private WorkspaceReadScope ManagementScope() => new(
        PortfolioId: _portfolioId,
        UserId: ActorUserId,
        SessionId: _sessionId,
        AccessContextId: _accessContextId,
        AccessRevision: _accessRevision);

    private StoredDocumentManagementAccess ManagementAccess() => new(
        _sessionId,
        ActorUserId,
        _accessContextId,
        _accessRevision);

    private static string Digest(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task RunOutboxWorkerAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(BusinessAtUtc)));
        services.AddDbContext<RentalCommandDbContext>(options =>
            options.UseNpgsql(_postgres!.GetConnectionString()));
        services.AddScoped<IOutboxClaimStore, OutboxClaimStore>();
        services.AddSingleton<IFileStorage>(_ => Storage);
        services.AddSingleton<INotificationChannel, NoopNotificationChannel>();
        services.AddSingleton<IPushSender, NoopPushSender>();
        services.AddSingleton<IDataUpdateService, NoopDataUpdateService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await new TestableOutboxWorker(provider).RunCycleAsync(scope.ServiceProvider);
    }

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private static async Task<(int PortfolioId, int UnitId)> SeedUnitAsync(
        RentalCommandDbContext db,
        string name)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = $"{name} Portfolio",
            ManagementCompanyName = $"{name} Management",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = $"{name} Property",
            AddressLine1 = "100 Test Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(unit);
        await db.SaveChangesAsync();
        return (portfolio.Id, unit.Id);
    }

    private async Task<(Guid SessionId, int AccessContextId, long AccessRevision)>
        SeedManagementAccessAsync(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = ActorUserId,
            UserName = "stored-document-atomic-user",
            NormalizedUserName = "STORED-DOCUMENT-ATOMIC-USER",
            Email = "stored-document-atomic@example.test",
            NormalizedEmail = "STORED-DOCUMENT-ATOMIC@EXAMPLE.TEST",
            DisplayName = "Stored Document Atomic User",
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
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };

        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        return (session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; StoredFile PostgreSQL proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => ActorUserId;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CapturingFileStorage : IFileStorage
    {
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public void Add(string path) => _paths.Add(path);
        public bool Contains(string path) => _paths.Contains(path);
        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            Deleted.Add(path);
            _paths.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class TestableOutboxWorker(IServiceProvider services)
        : OutboxDispatchWorker(services, NullLogger<OutboxDispatchWorker>.Instance)
    {
        public Task<int> RunCycleAsync(IServiceProvider scopedProvider) =>
            ExecuteCycleAsync(scopedProvider, CancellationToken.None);
    }

    private sealed class NoopNotificationChannel : INotificationChannel
    {
        public Task<NotificationDeliveryReceipt> SendSmsAsync(
            string toPhoneNumber,
            string message,
            NotificationDeliveryContext delivery,
            int? portfolioId = null,
            CancellationToken ct = default) =>
            Task.FromResult(new NotificationDeliveryReceipt("noop", "noop"));

        public Task<NotificationDeliveryReceipt> SendEmailAsync(
            string toEmail,
            string subject,
            string body,
            NotificationDeliveryContext delivery,
            string? htmlBody = null,
            CancellationToken ct = default) =>
            Task.FromResult(new NotificationDeliveryReceipt("noop", "noop"));
    }

    private sealed class NoopPushSender : IPushSender
    {
        public Task<PushSendResult> SendAsync(
            string deviceToken,
            string title,
            string body,
            IReadOnlyDictionary<string, string>? data,
            NotificationDeliveryContext delivery,
            CancellationToken ct = default) =>
            Task.FromResult(PushSendResult.Ok("noop"));
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(
            int portfolioId,
            string entityType,
            int entityId,
            object data,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(
            int portfolioId,
            string entityType,
            int entityId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();
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

    private sealed class InjectedAuditFailure : Exception { }
}
