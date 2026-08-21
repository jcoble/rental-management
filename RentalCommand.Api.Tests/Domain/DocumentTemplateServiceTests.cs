using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Documents;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class DocumentTemplateServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _ctx = null!;
    private readonly DocumentTemplateFieldCatalog _catalog = new();
    private readonly InMemoryFileStorage _files = new();
    private DocumentTemplateService _sut = null!;
    private WorkspaceReadScope _scope;
    private ServiceProvider _services = null!;
    private readonly MigratedPostgreSqlFixture _postgres;

    public DocumentTemplateServiceTests(MigratedPostgreSqlFixture postgres)
    {
        _postgres = postgres;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _postgres.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(DocumentTemplateServiceTests));
        await _ctx.ActivateApiScopeAsync(_scope);
        _services = CreateAtomicServices(builder =>
            builder.UseNpgsql(_ctx.Db.Database.GetDbConnection())
                .AddInterceptors(new RecordingCommandInterceptor(_commands)));
        _sut = new DocumentTemplateService(
            _services.GetRequiredService<RentalCommandDbContext>(), _catalog, _files,
            _services.GetRequiredService<IPendingFileUploadStore>(), TimeProvider.System,
            _services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public void LeaseCatalog_IncludesRentAndRequiredTenantSigningFields()
    {
        var catalog = _catalog.GetCatalog(DocumentTemplateKind.Lease);

        catalog.Should().Contain(f =>
            f.FieldKey == "lease.monthlyRent" &&
            f.Kind == DocumentTemplateFieldKind.Currency &&
            f.SignerRole == DocumentTemplateSignerRole.None);

        catalog.Should().Contain(f =>
            f.FieldKey == "lease.signature.tenant" &&
            f.Kind == DocumentTemplateFieldKind.Signature &&
            f.SignerRole == DocumentTemplateSignerRole.Tenant &&
            f.RequiredForSignature);
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedTemplate("Alpha lease", DocumentTemplateKind.Lease);
        SeedTemplate("Bravo lease", DocumentTemplateKind.Lease);
        SeedTemplate("Application template", DocumentTemplateKind.RentalApplication);
        SeedTemplate("Cedar lease", DocumentTemplateKind.Lease);

        _commands.Clear();
        var result = await _sut.ListPageAsync(
            _scope,
            DocumentTemplateKind.Lease,
            status: null,
            propertyId: null,
            new ListQuery { Sort = "name", Skip = 1, Take = 1 });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(1);
        result.Items.Select(t => t.Name).Should().Equal("Bravo lease");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"DocumentTemplates\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_FiltersPropertyCompatibilityAndCountsFieldsInPagedSql()
    {
        var selectedProperty = SeedProperty("Selected");
        var otherProperty = SeedProperty("Other");
        var global = SeedTemplate("Global lease", DocumentTemplateKind.Lease);
        var selected = SeedTemplate("Selected lease", DocumentTemplateKind.Lease, selectedProperty.Id);
        SeedTemplate("Other lease", DocumentTemplateKind.Lease, otherProperty.Id);
        _ctx.Db.DocumentTemplateFields.Add(new DocumentTemplateField
        {
            PortfolioId = PortfolioId,
            DocumentTemplateId = selected.Id,
            FieldKey = "lease.monthlyRent",
            Label = "Monthly rent",
            Kind = DocumentTemplateFieldKind.Currency,
            SignerRole = DocumentTemplateSignerRole.None,
            PageNumber = 1,
            WidthPct = 0.1,
            HeightPct = 0.1,
        });
        _ctx.Db.SaveChanges();

        _commands.Clear();
        var result = await _sut.ListPageAsync(
            _scope,
            DocumentTemplateKind.Lease,
            status: null,
            propertyId: selectedProperty.Id,
            new ListQuery { Sort = "name", Take = 20 });

        result.Items.Select(t => t.Id).Should().BeEquivalentTo([global.Id, selected.Id]);
        result.Items.Single(t => t.Id == selected.Id).FieldCount.Should().Be(1);
        result.Items.Should().NotContain(t => t.PropertyId == otherProperty.Id);
        _commands.Should().HaveCount(2);
        _commands.Should().Contain(sql =>
            sql.Contains("PropertyId", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("DocumentTemplateFields", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddFieldAsync_UsesCatalogLabelAndRequiredFlag_AndBumpsTemplateVersion()
    {
        var template = (await _sut.CreateAsync(_scope, new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Dad's lease",
        }, "create-dads-lease")).Value!;

        var result = await _sut.AddFieldAsync(_scope, template.Id, new CreateDocumentTemplateFieldRequest
        {
            FieldKey = "lease.signature.tenant",
            Kind = DocumentTemplateFieldKind.Signature,
            SignerRole = DocumentTemplateSignerRole.Tenant,
            PageNumber = 2,
            XPct = 0.62,
            YPct = 0.72,
            WidthPct = 0.2,
            HeightPct = 0.04,
        }, "add-dads-signature");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.Label.Should().Be("Tenant signature");
        result.Value.Required.Should().BeTrue();

        var storedTemplate = await _ctx.Db.DocumentTemplates.FirstAsync(t => t.Id == template.Id);
        storedTemplate.Version.Should().Be(2);
        var storedField = await _ctx.Db.DocumentTemplateFields.SingleAsync(
            field => field.DocumentTemplateId == template.Id);
        storedField.PortfolioId.Should().Be(PortfolioId);
        var fieldAudit = await _ctx.Db.AtomicAuditLogs.SingleAsync(audit =>
            audit.EntityType == nameof(DocumentTemplateField) && audit.EntityId == storedField.Id);
        fieldAudit.PortfolioId.Should().Be(PortfolioId);
        using var auditValues = JsonDocument.Parse(fieldAudit.NewValues!);
        auditValues.RootElement.GetProperty(nameof(DocumentTemplateField.PortfolioId))
            .GetInt32().Should().Be(PortfolioId);
        auditValues.RootElement.GetProperty(nameof(DocumentTemplateField.DocumentTemplateId))
            .GetInt32().Should().Be(template.Id);
    }

    [Fact]
    public async Task AddFieldAsync_RejectsCoordinateExtentPastRightEdge()
    {
        var template = (await _sut.CreateAsync(_scope, new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Overlay lease",
        }, "create-overlay-lease")).Value!;

        var result = await _sut.AddFieldAsync(_scope, template.Id, new CreateDocumentTemplateFieldRequest
        {
            FieldKey = "tenant.fullName",
            Label = "Tenant",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.95,
            YPct = 0.1,
            WidthPct = 0.1,
            HeightPct = 0.03,
        }, "add-invalid-overlay-field");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Invalid);
        result.Error.Should().Contain("right edge");
        (await _ctx.Db.DocumentTemplateFields.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UploadPdfAsync_StoresSourceFileAndCreatesDraftOverlayLeaseTemplate()
    {
        await using var postgres = await _postgres.CreateContextAsync();
        var scope = postgres.Db.SeedAdministratorScope(
            PortfolioId, nameof(UploadPdfAsync_StoresSourceFileAndCreatesDraftOverlayLeaseTemplate));
        await postgres.ActivateApiScopeAsync(scope);
        await using var services = CreateAtomicServices(builder =>
            builder.UseNpgsql(postgres.Db.Database.GetDbConnection()));
        var files = new InMemoryFileStorage();
        var sut = new DocumentTemplateService(
            services.GetRequiredService<RentalCommandDbContext>(), _catalog, files,
            services.GetRequiredService<IPendingFileUploadStore>(), TimeProvider.System,
            services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());
        await using var content = new MemoryStream("%PDF-1.7 sample"u8.ToArray());

        var result = await sut.UploadPdfAsync(
            scope,
            content,
            "dad-lease.pdf",
            "application/pdf",
            content.Length,
            "Dad's lease",
            "Use this for the main portfolio.",
            defaultForPortfolio: true,
            propertyId: null,
            idempotencyKey: "upload-dads-lease");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.Name.Should().Be("Dad's lease");
        result.Value.Kind.Should().Be(DocumentTemplateKind.Lease);
        result.Value.RenderMode.Should().Be(DocumentTemplateRenderMode.Overlay);
        result.Value.Status.Should().Be(DocumentTemplateStatus.Draft);
        result.Value.OriginalStoredFileId.Should().NotBeNull();
        result.Value.DefaultForPortfolio.Should().BeTrue();

        var stored = await postgres.Db.StoredFiles.SingleAsync(
            f => f.Id == result.Value.OriginalStoredFileId);
        stored.PortfolioId.Should().Be(PortfolioId);
        stored.EntityType.Should().Be("DocumentTemplate");
        stored.EntityId.Should().Be(result.Value.Id);
        stored.FileName.Should().Be("dad-lease.pdf");
        stored.ContentType.Should().Be("application/pdf");
        stored.FileSize.Should().Be(content.Length);
        files.Contains(stored.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task UploadPdfAsync_FirstEmptyPortfolioUpload_IsIdempotentWithoutDuplicateTemplate()
    {
        await using var postgres = await _postgres.CreateContextAsync();
        var scope = postgres.Db.SeedAdministratorScope(
            PortfolioId, nameof(UploadPdfAsync_FirstEmptyPortfolioUpload_IsIdempotentWithoutDuplicateTemplate));
        await postgres.ActivateApiScopeAsync(scope);
        await using var services = CreateAtomicServices(builder =>
            builder.UseNpgsql(postgres.Db.Database.GetDbConnection()));
        var files = new InMemoryFileStorage();
        var sut = new DocumentTemplateService(
            services.GetRequiredService<RentalCommandDbContext>(), _catalog, files,
            services.GetRequiredService<IPendingFileUploadStore>(), TimeProvider.System,
            services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());

        await using var firstContent = new MemoryStream("%PDF-1.7 first empty portfolio lease"u8.ToArray());
        var first = await sut.UploadPdfAsync(
            scope,
            firstContent,
            "empty-portfolio-lease.pdf",
            "application/pdf",
            firstContent.Length,
            "First empty portfolio lease",
            "Regression for the first reusable lease template.",
            defaultForPortfolio: false,
            propertyId: null,
            idempotencyKey: "upload-first-empty-portfolio-lease");

        await using var retryContent = new MemoryStream("%PDF-1.7 first empty portfolio lease"u8.ToArray());
        var retry = await sut.UploadPdfAsync(
            scope,
            retryContent,
            "empty-portfolio-lease.pdf",
            "application/pdf",
            retryContent.Length,
            "First empty portfolio lease",
            "Regression for the first reusable lease template.",
            defaultForPortfolio: false,
            propertyId: null,
            idempotencyKey: "upload-first-empty-portfolio-lease");

        first.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        retry.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        retry.Value!.Id.Should().Be(first.Value!.Id);
        (await postgres.Db.Properties.CountAsync()).Should().Be(0);
        (await postgres.Db.DocumentTemplates.CountAsync()).Should().Be(1);
        (await postgres.Db.StoredFiles.CountAsync(file => file.EntityType == nameof(DocumentTemplate))).Should().Be(1);
        (await postgres.Db.PendingFileUploads.CountAsync()).Should().Be(1);
        files.Count.Should().Be(1);
    }

    [Fact]
    public async Task UploadPdfAsync_SelectedPropertyManagerScopeCreatesPortfolioTemplateWithNullTarget()
    {
        await using var postgres = await _postgres.CreateContextAsync();
        var selectedProperty = SeedProperty(postgres.Db, "Morgan selected property");
        SeedProperty(postgres.Db, "Morgan unselected property");
        var scope = postgres.Db.SeedPropertyManagerScope(
            PortfolioId,
            selectedProperty.Id,
            nameof(UploadPdfAsync_SelectedPropertyManagerScopeCreatesPortfolioTemplateWithNullTarget));
        await postgres.ActivateApiScopeAsync(scope);
        await using var services = CreateAtomicServices(builder =>
            builder.UseNpgsql(postgres.Db.Database.GetDbConnection()));
        var files = new InMemoryFileStorage();
        var sut = new DocumentTemplateService(
            services.GetRequiredService<RentalCommandDbContext>(), _catalog, files,
            services.GetRequiredService<IPendingFileUploadStore>(), TimeProvider.System,
            services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());
        await using var content = new MemoryStream("%PDF-1.7 morgan selected scope lease"u8.ToArray());

        var result = await sut.UploadPdfAsync(
            scope,
            content,
            "scn-0001-lease.pdf",
            "application/pdf",
            content.Length,
            "SCN-0001 Lease",
            "Uploaded by a selected-property property manager.",
            defaultForPortfolio: false,
            propertyId: null,
            idempotencyKey: "upload-scn-0001-selected-scope-lease");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.PropertyId.Should().BeNull();
        (await postgres.Db.DocumentTemplates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpdateFieldAsync_SelectedPropertyManagerScopeUpdatesFieldWithServerSideJoin()
    {
        await using var postgres = await _postgres.CreateContextAsync();
        var commands = new List<string>();
        var selectedProperty = SeedProperty(postgres.Db, "Designer selected property");
        var otherProperty = SeedProperty(postgres.Db, "Designer other property");
        var template = SeedTemplate(postgres.Db, "Designer lease", DocumentTemplateKind.Lease, selectedProperty.Id);
        var field = SeedTemplateField(postgres.Db, template.Id, "lease.monthlyRent", "Monthly rent", widthPct: 0.18);
        var scope = postgres.Db.SeedPropertyManagerScope(
            PortfolioId,
            selectedProperty.Id,
            nameof(UpdateFieldAsync_SelectedPropertyManagerScopeUpdatesFieldWithServerSideJoin));
        var unauthorizedScope = postgres.Db.SeedPropertyManagerScope(
            PortfolioId,
            otherProperty.Id,
            "unauthorized-designer-update");
        await postgres.ActivateApiScopeAsync(scope);
        await using var services = CreateAtomicServices(builder =>
            builder.UseNpgsql(postgres.Db.Database.GetDbConnection())
                .AddInterceptors(new RecordingCommandInterceptor(commands)));
        var sut = new DocumentTemplateService(
            services.GetRequiredService<RentalCommandDbContext>(), _catalog, new InMemoryFileStorage(),
            services.GetRequiredService<IPendingFileUploadStore>(), TimeProvider.System,
            services.GetRequiredService<RentalCommand.Api.Writes.IRequestWriteExecutor>());

        var result = await sut.UpdateFieldAsync(
            scope,
            template.Id,
            field.Id,
            new UpdateDocumentTemplateFieldRequest
            {
                WidthPct = 0.24,
                HeightPct = 0.05,
            },
            "update-designer-field-width");
        var retry = await sut.UpdateFieldAsync(
            scope,
            template.Id,
            field.Id,
            new UpdateDocumentTemplateFieldRequest
            {
                WidthPct = 0.24,
                HeightPct = 0.05,
            },
            "update-designer-field-width");
        var denied = await sut.UpdateFieldAsync(
            unauthorizedScope,
            template.Id,
            field.Id,
            new UpdateDocumentTemplateFieldRequest
            {
                WidthPct = 0.32,
            },
            "update-designer-field-width-denied");

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.WidthPct.Should().Be(0.24);
        result.Value.HeightPct.Should().Be(0.05);
        retry.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        retry.Value!.WidthPct.Should().Be(0.24);
        denied.Outcome.Should().Be(DocumentTemplateOperationOutcome.NotFound);

        var storedField = await postgres.Db.DocumentTemplateFields
            .AsNoTracking()
            .SingleAsync(item => item.Id == field.Id);
        storedField.WidthPct.Should().Be(0.24);
        storedField.HeightPct.Should().Be(0.05);
        var storedTemplate = await postgres.Db.DocumentTemplates
            .AsNoTracking()
            .SingleAsync(item => item.Id == template.Id);
        storedTemplate.Version.Should().Be(2);
        (await postgres.Db.AtomicAuditLogs.CountAsync(audit =>
            audit.EntityType == nameof(DocumentTemplateField) &&
            audit.EntityId == field.Id &&
            audit.Operation == AuditLogOperation.Updated)).Should().Be(1);
        commands.Should().Contain(sql =>
            sql.Contains("DocumentTemplates", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("DocumentTemplateFields", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("JOIN", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase));
    }

    private static ServiceProvider CreateAtomicServices(
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<RentalCommand.Api.Writes.IRequestWriteExecutor,
            RentalCommand.Api.Writes.RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            configureDatabase(builder);
            builder.UseAtomicPersistenceKernel(provider);
        });
        services.AddPendingFileUploadStore();
        return services.BuildServiceProvider();
    }

    private Property SeedProperty(string name)
        => SeedProperty(_ctx.Db, name);

    private static Property SeedProperty(RentalCommandDbContext db, string name)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = "1 Test St",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        db.SaveChanges();
        return property;
    }

    private DocumentTemplate SeedTemplate(
        string name,
        DocumentTemplateKind kind,
        int? propertyId = null)
        => SeedTemplate(_ctx.Db, name, kind, propertyId);

    private static DocumentTemplate SeedTemplate(
        RentalCommandDbContext db,
        string name,
        DocumentTemplateKind kind,
        int? propertyId = null)
    {
        var template = new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            Kind = kind,
            Status = DocumentTemplateStatus.Draft,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = name,
            PropertyId = propertyId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.DocumentTemplates.Add(template);
        db.SaveChanges();
        return template;
    }

    private static DocumentTemplateField SeedTemplateField(
        RentalCommandDbContext db,
        int templateId,
        string fieldKey,
        string label,
        double widthPct)
    {
        var field = new DocumentTemplateField
        {
            PortfolioId = PortfolioId,
            DocumentTemplateId = templateId,
            FieldKey = fieldKey,
            Label = label,
            Kind = DocumentTemplateFieldKind.Currency,
            SignerRole = DocumentTemplateSignerRole.None,
            PageNumber = 1,
            XPct = 0.12,
            YPct = 0.2,
            WidthPct = widthPct,
            HeightPct = 0.04,
            SortOrder = 10,
        };
        db.DocumentTemplateFields.Add(field);
        db.SaveChanges();
        return field;
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            var key = $"{Guid.NewGuid():N}_{fileName}";
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            _files[key] = ms.ToArray();
            return key;
        }

        public async Task UploadAtAsync(
            Stream content, string storagePath, string fileName, string contentType,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            _files[storagePath] = ms.ToArray();
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            Stream stream = new MemoryStream(_files[path]);
            return Task.FromResult(stream);
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }

        public bool Contains(string path) => _files.ContainsKey(path);

        public int Count => _files.Count;
    }
}
