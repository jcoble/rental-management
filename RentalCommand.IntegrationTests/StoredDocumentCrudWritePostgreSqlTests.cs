using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class StoredDocumentCrudWritePostgreSqlTests : IAsyncLifetime
{
    private const string ContentHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTime BusinessNow =
        new(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AuditNow =
        new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public StoredDocumentCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public void Commands_PreserveFrozenFingerprints_UseLegacyLockPlans_AndRetireHandlers()
    {
        var access = new StoredDocumentManagementAccess(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), 7, 8, 9);
        var create = new CreateStoredDocumentCommand(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1, StoredDocumentTarget.Unit, 2, 7, null, true, "create-operation",
            "request-fingerprint", ContentHash, "lease.pdf", "stored/lease.pdf",
            "application/pdf", 42, BusinessNow, access);
        var delete = new DeleteStoredDocumentCommand(
            1, 3, 7, null, true, "delete-operation", BusinessNow, access);

        AtomicCommandFingerprint.Create(create).Should().Be(
            "7bad6fd80b33492f9c9fe080157db2bb77058b0ac4293694125cf137fa34113f");
        AtomicCommandFingerprint.Create(delete).Should().Be(
            "13d18b01d0acfbb30cd117d084653ff0ce3eee2957cc9578a6217cc340523ea4");

        var createWrite = StoredDocumentWriteSupport.Create(create, Created, Authorized);
        createWrite.OperationName.Should().Be("stored-document.create");
        createWrite.ResultContract.Should().Be(StoredDocumentWriteSupport.CreateResultContract);
        createWrite.LockPlan.Protocol.Should().Be(WriteLockProtocol.Portfolio);
        createWrite.LockPlan.Locks.Select(row => row.LockNamespace).Should().Equal("Portfolio");

        var deleteWrite = StoredDocumentWriteSupport.Delete(delete, Deleted, Authorized);
        deleteWrite.OperationName.Should().Be("stored-document.delete");
        deleteWrite.ResultContract.Should().Be(StoredDocumentWriteSupport.DeleteResultContract);
        deleteWrite.LockPlan.Protocol.Should().Be(WriteLockProtocol.StoredFile);
        deleteWrite.LockPlan.Locks.Select(row => row.LockNamespace).Should().Equal("StoredFile");

        AssertRetired(new CreateStoredDocumentHandler(), create);
        AssertRetired(new DeleteStoredDocumentHandler(), delete);

        static Task<CreateStoredDocumentResult> Created(
            CreateStoredDocumentCommand command, IAtomicCommandContext _, CancellationToken __) =>
            Task.FromResult(new CreateStoredDocumentResult(
                StoredDocumentMutationOutcome.Created, 1, command.Target.ToString(),
                command.EntityId, command.FileName, command.StoragePath,
                command.ContentType, command.SizeBytes, command.UploadedAtUtc));
        static Task<DeleteStoredDocumentResult> Deleted(
            DeleteStoredDocumentCommand command, IAtomicCommandContext _, CancellationToken __) =>
            Task.FromResult(new DeleteStoredDocumentResult(
                StoredDocumentMutationOutcome.Deleted, command.StoredFileId,
                nameof(StoredDocumentTarget.Unit), 2, "lease.pdf", "stored/lease.pdf",
                command.DeletedAtUtc));
        static Task Authorized<T>(
            T _, IAtomicCommandContext __, CancellationToken ___) where T : IAtomicCommandData =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task CreateDelete_ReplayExactly_PreserveSaveClock_RejectStaleAccess_AndStageCleanup()
    {
        var scope = await SeedScopeAndUnitAsync();
        var unitId = await _context.Db.Units.Select(row => row.Id).SingleAsync();
        var firstPending = await SeedPendingAsync(scope, "create-original", "stored/original.pdf");
        var probe = new CaptureCommandInterceptor();
        await using var services = BuildServices(
            scope, new FirstThenFixedTimeProvider(BusinessNow, AuditNow), probe);

        RentalCommand.Api.DTOs.DocumentDto original;
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var documents = serviceScope.ServiceProvider.GetRequiredService<DocumentService>();
            original = (await CreateAsync(
                documents, scope, unitId, firstPending, "create-original", "stored/original.pdf"))!;
            (await CreateAsync(
                    documents, scope, unitId, firstPending, "create-original", "stored/original.pdf"))
                .Should().BeEquivalentTo(original);
        }

        var duplicatePending = await SeedPendingAsync(
            scope, "create-duplicate", "stored/duplicate.pdf");
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var documents = serviceScope.ServiceProvider.GetRequiredService<DocumentService>();
            var duplicate = await CreateAsync(
                documents, scope, unitId, duplicatePending,
                "create-duplicate", "stored/duplicate.pdf");
            duplicate.Should().BeEquivalentTo(original);
            (await CreateAsync(
                    documents, scope, unitId, duplicatePending,
                    "create-duplicate", "stored/duplicate.pdf"))
                .Should().BeEquivalentTo(original);

            (await documents.DeleteAsync(
                scope.PortfolioId, original.Id, scope.UserId, null, true, scope,
                "delete-original")).Should().BeTrue();
            (await documents.DeleteAsync(
                scope.PortfolioId, original.Id, scope.UserId, null, true, scope,
                "delete-original")).Should().BeTrue();
        }

        _context.Db.ChangeTracker.Clear();
        var createReceipts = await _context.Db.AtomicCommandReceipts.AsNoTracking()
            .Where(row => row.CommandType == "stored-document.create")
            .OrderBy(row => row.StartedAt)
            .ToArrayAsync();
        createReceipts.Should().HaveCount(2);
        createReceipts.Should().OnlyContain(row =>
            row.ResultContract == StoredDocumentWriteSupport.CreateResultContract);
        JsonSerializer.Deserialize<CreateStoredDocumentResult>(createReceipts[0].ResultJson!)!
            .Outcome.Should().Be(StoredDocumentMutationOutcome.Created);
        JsonSerializer.Deserialize<CreateStoredDocumentResult>(createReceipts[1].ResultJson!)!
            .Outcome.Should().Be(StoredDocumentMutationOutcome.ReusedExisting);

        var stored = await _context.Db.StoredFiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(row => row.Id == original.Id);
        stored.UploadedAt.Should().Be(BusinessNow);
        stored.DeletedAt.Should().Be(AuditNow);
        var createAudit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == "stored-document.create" && row.EntityType == nameof(StoredFile));
        createAudit.Timestamp.Should().Be(AuditNow);
        createAudit.Timestamp.Should().NotBe(stored.UploadedAt);

        var duplicateCleanup = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey.StartsWith("stored-document-duplicate-upload:"));
        duplicateCleanup.MessageType.Should().Be("blob-delete");
        using (var payload = JsonDocument.Parse(duplicateCleanup.Payload))
        {
            payload.RootElement.GetProperty("storedFileId").GetInt32().Should().Be(original.Id);
            payload.RootElement.GetProperty("storagePath").GetString()
                .Should().Be("stored/duplicate.pdf");
        }

        var deleteCleanup = await _context.Db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == $"stored-file-delete:{original.Id}");
        deleteCleanup.MessageType.Should().Be("blob-delete");
        using (var payload = JsonDocument.Parse(deleteCleanup.Payload))
        {
            payload.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo("storedFileId", "storagePath");
            payload.RootElement.GetProperty("storedFileId").GetInt32().Should().Be(original.Id);
            payload.RootElement.GetProperty("storagePath").GetString()
                .Should().Be("stored/original.pdf");
        }

        probe.Commands.Count(command => command.Contains(
            "pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase))
            .Should().BeGreaterThanOrEqualTo(3);

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(scope, new FixedTimeProvider(AuditNow), new());
        await using var staleScope = staleServices.CreateAsyncScope();
        var staleDocuments = staleScope.ServiceProvider.GetRequiredService<DocumentService>();
        await FluentActions.Invoking(() => CreateAsync(
                staleDocuments, scope, unitId, firstPending,
                "create-original", "stored/original.pdf"))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*document upload*");
        await FluentActions.Invoking(() => staleDocuments.DeleteAsync(
                scope.PortfolioId, original.Id, scope.UserId, null, true, scope,
                "delete-original"))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*remove this document*");
    }

    private static void AssertRetired<TCommand, TResult>(
        IAtomicCommandHandler<TCommand, TResult> handler, TCommand command)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        FluentActions.Invoking(() => handler.HandleAsync(command, null!, default))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Stored document writes no longer use the legacy stored document handlers.")
            .GetAwaiter().GetResult();
        FluentActions.Invoking(() => handler.AuthorizeReplayAsync(command, null!, default))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Stored document writes no longer use the legacy stored document handlers.")
            .GetAwaiter().GetResult();
    }

    private async Task<WorkspaceReadScope> SeedScopeAndUnitAsync()
    {
        var now = DateTime.UtcNow;
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            UserId = user.Id, PortfolioId = 1, Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext, PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-5), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active, CreatedAtUtc = now, LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        var property = new Property
        {
            PortfolioId = 1, Name = "Stored document property",
            AddressLine1 = "4 Executor Way", City = "Columbus", State = "OH",
            PostalCode = "43215", CreatedAt = now, UpdatedAt = now,
        };
        var unit = new Unit
        {
            PortfolioId = 1, Property = property, UnitNumber = "4A",
            Bedrooms = 2, Bathrooms = 1, MarketRent = 1400,
            CreatedAt = now, UpdatedAt = now,
        };
        _context.Db.AddRange(accessContext, membership, assignment, session, property, unit);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        var scope = new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
        await _context.ActivateApiScopeAsync(scope);
        return scope;
    }

    private async Task<Guid> SeedPendingAsync(
        WorkspaceReadScope scope, string operationId, string storagePath)
    {
        var pending = new PendingFileUpload
        {
            Id = Guid.NewGuid(), PortfolioId = scope.PortfolioId, ActorScopeId = scope.UserId,
            Purpose = "stored-document", OperationKeyHash = Digest(operationId),
            RequestFingerprint = ContentHash, StoragePath = storagePath, FileName = "lease.pdf",
            ContentType = "application/pdf", SizeBytes = 42,
            State = PendingFileUploadState.Prepared,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        _context.Db.PendingFileUploads.Add(pending);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return pending.Id;
    }

    private static Task<RentalCommand.Api.DTOs.DocumentDto?> CreateAsync(
        DocumentService documents, WorkspaceReadScope scope, int unitId,
        Guid pendingUploadId, string operationId, string storagePath) =>
        documents.CreateAsync(
            pendingUploadId, scope.PortfolioId, StoredDocumentTarget.Unit, unitId,
            scope.UserId, null, true, scope, operationId, ContentHash, ContentHash,
            "lease.pdf", "application/pdf", 42, storagePath);

    private ServiceProvider BuildServices(
        WorkspaceReadScope scope, TimeProvider timeProvider, CaptureCommandInterceptor capture)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            scope.SessionId, scope.UserId, scope.AccessContextId, scope.PortfolioId,
            scope.AccessRevision, WorkspaceExperience.Management, null, WorkspaceExperience.Management);
        var rls = new RlsConnectionInterceptor(new HttpContextAccessor { HttpContext = httpContext });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddSingleton<IFileStorage>(Mock.Of<IFileStorage>());
        services.AddSingleton(capture);
        services.AddSingleton<ApiRoleConnectionInterceptor>();
        services.AddSingleton(rls);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<DocumentService>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ApiRoleConnectionInterceptor>())
                .AddInterceptors(provider.GetRequiredService<RlsConnectionInterceptor>())
                .AddInterceptors(provider.GetRequiredService<CaptureCommandInterceptor>()));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class FirstThenFixedTimeProvider(DateTime first, DateTime subsequent) : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Increment(ref _calls) == 1 ? first : subsequent);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:stored-document-crud";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CaptureCommandInterceptor : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ApiRoleConnectionInterceptor : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET SESSION AUTHORIZATION rentalcommand_api;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
