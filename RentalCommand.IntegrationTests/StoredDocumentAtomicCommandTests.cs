using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for general StoredFile create/delete command boundaries.</summary>
public sealed class StoredDocumentAtomicCommandTests : IAsyncLifetime
{
    private const string ContentHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _unitId;
    private int _otherUnitId;

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
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandProbe>();
        services.AddSingleton<AuditFailureInterceptor>();
        services.AddSingleton<CapturingFileStorage>();
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<CapturingFileStorage>());
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            CreateStoredDocumentCommand,
            CreateStoredDocumentResult,
            CreateStoredDocumentHandler>();
        services.AddAtomicCommandHandler<
            DeleteStoredDocumentCommand,
            DeleteStoredDocumentResult,
            DeleteStoredDocumentHandler>();
        services.AddScoped<IPendingFileUploadStore, PendingFileUploadStore>();
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
            _portfolioId, created!.Id, 73, null, true, "delete-one");
        var replay = await documents.DeleteAsync(
            _portfolioId, created.Id, 73, null, true, "delete-one");

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
    public async Task Authorization_queries_are_server_translated_and_handlers_have_no_storage_dependency()
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
        typeof(CreateStoredDocumentHandler).GetConstructors().Single().GetParameters().Should().BeEmpty();
        typeof(DeleteStoredDocumentHandler).GetConstructors().Single().GetParameters().Should().BeEmpty();
    }

    private CapturingFileStorage Storage => _services!.GetRequiredService<CapturingFileStorage>();
    private CommandProbe Probe => _services!.GetRequiredService<CommandProbe>();
    private AuditFailureInterceptor Failure => _services!.GetRequiredService<AuditFailureInterceptor>();

    private async Task<RentalCommand.Api.DTOs.DocumentDto?> CreateAsync(
        string operationId,
        string storagePath,
        int? entityId = null)
    {
        var pendingUploadId = Guid.NewGuid();
        var fingerprint = ContentHash;
        await using (var db = NewContext())
        {
            var operationHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();
            var existing = await db.PendingFileUploads.AsNoTracking()
                .SingleOrDefaultAsync(upload => upload.PortfolioId == _portfolioId
                    && upload.ActorScopeId == 73
                    && upload.Purpose == "stored-document"
                    && upload.OperationKeyHash == operationHash);
            if (existing is not null)
            {
                pendingUploadId = existing.Id;
            }
            else
            {
                db.PendingFileUploads.Add(new PendingFileUpload
                {
                    Id = pendingUploadId,
                    PortfolioId = _portfolioId,
                    ActorScopeId = 73,
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
            }
        }
        await using var scope = _services!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDocumentService>().CreateAsync(
            pendingUploadId,
            _portfolioId,
            StoredDocumentTarget.Unit,
            entityId ?? _unitId,
            73,
            null,
            true,
            operationId,
            fingerprint,
            ContentHash,
            "lease.pdf",
            "application/pdf",
            42,
            storagePath);
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

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; StoredFile PostgreSQL proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 73;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
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
