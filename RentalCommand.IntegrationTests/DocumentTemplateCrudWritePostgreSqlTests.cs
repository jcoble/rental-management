using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Data;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection2.Name)]
public sealed class DocumentTemplateCrudWritePostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNow =
        new(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;

    public DocumentTemplateCrudWritePostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync();

    public async Task DisposeAsync() =>
        await _context.DisposeAsync();

    [Fact]
    public void CreateCommand_PreservesFrozenLegacyFingerprint()
    {
        var command = new CreateDocumentTemplateCommand(
            1, Actor(7), BusinessNow, DocumentTemplateKind.Lease,
            DocumentTemplateRenderMode.Overlay, "Frozen lease", "Frozen description",
            42, 43, 44, true, "<p>Lease</p>", "ignored-delivery-key");

        AtomicCommandFingerprint.Create(command).Should().Be(
            "f5b3009190480ab2e9db8caedf5ef26b502fb475c1d707baf95aa50dd6803eec");
    }

    [Fact]
    public async Task FiveNonUploadMutations_ReplayExactly_PreserveAuditClockAndOutboxKinds()
    {
        var scope = await SeedScopeAsync();
        await using var services = BuildServices(scope);

        var createRequest = new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Restyle,
            Name = "Executor lease",
            Description = "Shared executor family",
            DraftHtml = "<p>Lease</p>",
        };
        DocumentTemplateOperationResult<DocumentTemplateResponse> created;
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
            created = await service.CreateAsync(scope, createRequest, "template-create");
            created.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success, created.Error);
            (await service.CreateAsync(scope, createRequest, "template-create"))
                .Should().BeEquivalentTo(created);
        }

        var updateRequest = new UpdateDocumentTemplateRequest
        {
            Name = "Updated executor lease",
            Status = DocumentTemplateStatus.Active,
        };
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
            var updated = await service.UpdateAsync(
                scope, created.Value!.Id, updateRequest, "template-update");
            updated.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success, updated.Error);
            (await service.UpdateAsync(scope, created.Value.Id, updateRequest, "template-update"))
                .Should().BeEquivalentTo(updated);
        }

        var addRequest = new CreateDocumentTemplateFieldRequest
        {
            FieldKey = "tenant.fullName",
            Label = "Tenant name",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.1,
            YPct = 0.2,
            WidthPct = 0.3,
            HeightPct = 0.05,
        };
        DocumentTemplateOperationResult<DocumentTemplateFieldResponse> added;
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
            added = await service.AddFieldAsync(
                scope, created.Value!.Id, addRequest, "template-field-add");
            added.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success, added.Error);
            (await service.AddFieldAsync(scope, created.Value.Id, addRequest, "template-field-add"))
                .Should().BeEquivalentTo(added);
        }

        var fieldUpdate = new UpdateDocumentTemplateFieldRequest { WidthPct = 0.4 };
        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
            var fieldUpdated = await service.UpdateFieldAsync(
                scope, created.Value!.Id, added.Value!.Id, fieldUpdate, "template-field-update");
            fieldUpdated.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success, fieldUpdated.Error);
            (await service.UpdateFieldAsync(
                    scope, created.Value.Id, added.Value.Id, fieldUpdate, "template-field-update"))
                .Should().BeEquivalentTo(fieldUpdated);
        }

        await using (var serviceScope = services.CreateAsyncScope())
        {
            var service = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
            var deleted = await service.DeleteFieldAsync(
                scope, created.Value!.Id, added.Value!.Id, "template-field-delete");
            (await service.DeleteFieldAsync(
                    scope, created.Value.Id, added.Value.Id, "template-field-delete"))
                .Should().BeEquivalentTo(deleted);
        }

        _context.Db.ChangeTracker.Clear();
        var receipt = await _context.Db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == "document-template.create");
        receipt.ResultContract.Should().Be(DocumentTemplateWriteSupport.ResultContract);
        var digest = Digest("template-create");
        receipt.IdempotencyKey.Should().Be($"{scope.PortfolioId}:{digest}");
        receipt.RequestFingerprint.Should().Be(AtomicCommandFingerprint.Create(
            new CreateDocumentTemplateCommand(
                scope.PortfolioId, Actor(scope), BusinessNow, createRequest.Kind,
                createRequest.RenderMode, createRequest.Name, createRequest.Description,
                null, null, null, false, createRequest.DraftHtml, digest)));

        var audit = await _context.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == receipt.CommandType
            && row.CommandIdempotencyKey == receipt.IdempotencyKey
            && row.EntityType == nameof(DocumentTemplate));
        audit.Timestamp.Should().Be(BusinessNow);
        audit.Timestamp.Should().NotBeCloseTo(DateTime.UtcNow, TimeSpan.FromDays(1));

        var payloads = await _context.Db.OutboxMessages.AsNoTracking()
            .Where(row => row.IdempotencyKey.StartsWith("document-template:"))
            .Select(row => row.Payload)
            .ToArrayAsync();
        payloads.Should().HaveCount(5);
        payloads.Select(payload => JsonDocument.Parse(payload).RootElement
                .GetProperty("operation").GetString())
            .Should().BeEquivalentTo("create", "update", "field-add", "field-update", "field-delete");
    }

    [Fact]
    public async Task FinalizeUpload_UploadsBlobBeforeCommand_AndLocksPreparedRowsInsideCommand()
    {
        var scope = await SeedScopeAsync();
        var events = new List<string>();
        await using var services = BuildServices(scope, events);
        await using var serviceScope = services.CreateAsyncScope();
        var sut = serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>();
        await using var content = new MemoryStream("%PDF-1.7 executor upload"u8.ToArray());

        var result = await sut.UploadPdfAsync(
            scope, content, "executor.pdf", "application/pdf", content.Length,
            "Executor PDF", null, false, null, "template-upload");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        var uploadIndex = events.IndexOf("blob-upload");
        var lockIndex = events.FindIndex(item => item.Contains(
            "scan-upload-admission-lock", StringComparison.Ordinal));
        uploadIndex.Should().BeGreaterThanOrEqualTo(0);
        lockIndex.Should().BeGreaterThan(uploadIndex);
        (await _context.Db.PendingFileUploads.AsNoTracking().SingleAsync())
            .State.Should().Be(PendingFileUploadState.Finalized);
    }

    [Fact]
    public async Task ExactReplay_RejectsStaleAuthorization()
    {
        var scope = await SeedScopeAsync();
        var request = new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Restyle,
            Name = "Stale replay lease",
        };
        const string key = "template-stale-replay";
        await using (var services = BuildServices(scope))
        await using (var serviceScope = services.CreateAsyncScope())
        {
            await serviceScope.ServiceProvider.GetRequiredService<DocumentTemplateService>()
                .CreateAsync(scope, request, key);
        }

        var session = await _context.Db.AuthSessions.SingleAsync(row => row.Id == scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        await using var staleServices = BuildServices(scope);
        await using var staleScope = staleServices.CreateAsyncScope();
        Func<Task> replay = () => staleScope.ServiceProvider
            .GetRequiredService<DocumentTemplateService>().CreateAsync(scope, request, key);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The active assignment cannot create this document template.");
    }

    [Fact]
    public void WritesUseAuthorizationScopeOnly()
    {
        var actor = Actor(7);
        var create = new CreateDocumentTemplateCommand(
            1, actor, BusinessNow, DocumentTemplateKind.Lease, DocumentTemplateRenderMode.Restyle,
            "Lease", null, null, null, null, false, null, "create");
        var upload = new FinalizeDocumentTemplateUploadCommand(
            1, actor, BusinessNow, Guid.NewGuid(), "document-template-pdf", "hash", "fingerprint",
            "path", "lease.pdf", "application/pdf", 10, "sha", "Lease", null, false, null, "upload");
        var update = new UpdateDocumentTemplateCommand(
            1, actor, BusinessNow, 2, null, null, "Updated", null, null, null, null, null, null, "update");
        var add = new AddDocumentTemplateFieldCommand(
            1, actor, BusinessNow, 2, "tenant.name", "Tenant", DocumentTemplateFieldKind.Text,
            DocumentTemplateSignerRole.None, 1, .1, .1, .2, .1, false, false, 1, null, "add");
        var fieldUpdate = new UpdateDocumentTemplateFieldCommand(
            1, actor, BusinessNow, 2, 3, null, null, null, null, null, null, null, .2, null,
            null, null, null, null, "field-update");
        var delete = new DeleteDocumentTemplateFieldCommand(1, actor, BusinessNow, 2, 3, "delete");

        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.create", create, Applied, Authorized));
        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.upload.finalize", upload, Applied, Authorized));
        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.update", update, Applied, Authorized));
        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.field.add", add, Applied, Authorized));
        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.field.update", fieldUpdate, Applied, Authorized));
        AssertPlan(DocumentTemplateWriteSupport.Write("document-template.field.delete", delete, Applied, Authorized));

        static Task<DocumentTemplateMutationResult> Applied<T>(
            T _, IAtomicCommandContext __, CancellationToken ___) where T : IAtomicCommandData =>
            Task.FromResult(new DocumentTemplateMutationResult(DocumentTemplateMutationOutcome.Applied, 1));
        static Task Authorized<T>(
            T _, IAtomicCommandContext __, CancellationToken ___) where T : IAtomicCommandData =>
            Task.CompletedTask;
    }

    private static void AssertPlan<TCommand>(
        TransactionalWrite<TCommand, DocumentTemplateMutationResult> write)
        where TCommand : notnull, IAtomicCommandData
    {
        write.LockPlan.Protocol.Should().Be(WriteLockProtocol.AuthorizationScope);
        write.LockPlan.Locks.Select(row => row.LockNamespace)
            .Should().Equal("AuthSession", "WorkspaceAccessContext", "Portfolio");
    }

    private async Task<WorkspaceReadScope> SeedScopeAsync()
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
        _context.Db.AddRange(accessContext, membership, assignment, session);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        var scope = new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
        await _context.ActivateApiScopeAsync(scope);
        return scope;
    }

    private ServiceProvider BuildServices(WorkspaceReadScope scope, List<string>? events = null)
    {
        var orderedEvents = events ?? [];
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            scope.SessionId, scope.UserId, scope.AccessContextId, scope.PortfolioId,
            scope.AccessRevision, WorkspaceExperience.Management, null, WorkspaceExperience.Management);
        var rls = new RlsConnectionInterceptor(new HttpContextAccessor { HttpContext = httpContext });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDocumentTemplateFieldCatalog, DocumentTemplateFieldCatalog>();
        services.AddSingleton<IFileStorage>(new OrderedFileStorage(orderedEvents));
        services.AddSingleton(new SqlOrderProbe(orderedEvents));
        services.AddSingleton<ApiRoleConnectionInterceptor>();
        services.AddSingleton(rls);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
        services.AddScoped<DocumentTemplateService>(provider => new DocumentTemplateService(
            provider.GetRequiredService<RentalCommandDbContext>(),
            provider.GetRequiredService<IDocumentTemplateFieldCatalog>(),
            provider.GetRequiredService<IFileStorage>(),
            provider.GetRequiredService<IPendingFileUploadStore>(),
            new FixedTimeProvider(BusinessNow),
            provider.GetRequiredService<IWriteExecutor>()));
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(provider.GetRequiredService<ApiRoleConnectionInterceptor>())
                .AddInterceptors(provider.GetRequiredService<RlsConnectionInterceptor>())
                .AddInterceptors(provider.GetRequiredService<SqlOrderProbe>()));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static StaffOperationActor Actor(int userId) => new(
        userId, Guid.Parse("11111111-1111-1111-1111-111111111111"), 9, 3);

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:document-template-crud";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class SqlOrderProbe(List<string> events) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            events.Add(command.CommandText);
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

    private sealed class OrderedFileStorage(List<string> events) : IFileStorage
    {
        public Task<string> UploadAsync(
            Stream content, string fileName, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UploadAtAsync(
            Stream content, string storagePath, string fileName, string contentType,
            CancellationToken ct = default)
        {
            events.Add("blob-upload");
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
