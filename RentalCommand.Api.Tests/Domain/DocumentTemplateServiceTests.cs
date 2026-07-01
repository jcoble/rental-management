using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class DocumentTemplateServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly DocumentTemplateFieldCatalog _catalog = new();
    private readonly InMemoryFileStorage _files = new();
    private readonly DocumentTemplateService _sut;

    public DocumentTemplateServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new DocumentTemplateService(_ctx.Db, _catalog, _files, TimeProvider.System);
    }

    public void Dispose() => _ctx.Dispose();

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
            PortfolioId,
            DocumentTemplateKind.Lease,
            status: null,
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
    public async Task AddFieldAsync_UsesCatalogLabelAndRequiredFlag_AndBumpsTemplateVersion()
    {
        var template = (await _sut.CreateAsync(PortfolioId, new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Dad's lease",
        })).Value!;

        var result = await _sut.AddFieldAsync(PortfolioId, template.Id, new CreateDocumentTemplateFieldRequest
        {
            FieldKey = "lease.signature.tenant",
            Kind = DocumentTemplateFieldKind.Signature,
            SignerRole = DocumentTemplateSignerRole.Tenant,
            PageNumber = 2,
            XPct = 0.62,
            YPct = 0.72,
            WidthPct = 0.2,
            HeightPct = 0.04,
        });

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.Label.Should().Be("Tenant signature");
        result.Value.Required.Should().BeTrue();

        var storedTemplate = await _ctx.Db.DocumentTemplates.FirstAsync(t => t.Id == template.Id);
        storedTemplate.Version.Should().Be(2);
    }

    [Fact]
    public async Task AddFieldAsync_RejectsCoordinateExtentPastRightEdge()
    {
        var template = (await _sut.CreateAsync(PortfolioId, new CreateDocumentTemplateRequest
        {
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Overlay lease",
        })).Value!;

        var result = await _sut.AddFieldAsync(PortfolioId, template.Id, new CreateDocumentTemplateFieldRequest
        {
            FieldKey = "tenant.fullName",
            Label = "Tenant",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.95,
            YPct = 0.1,
            WidthPct = 0.1,
            HeightPct = 0.03,
        });

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Invalid);
        result.Error.Should().Contain("right edge");
        (await _ctx.Db.DocumentTemplateFields.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UploadPdfAsync_StoresSourceFileAndCreatesDraftOverlayLeaseTemplate()
    {
        await using var content = new MemoryStream("%PDF-1.7 sample"u8.ToArray());

        var result = await _sut.UploadPdfAsync(
            PortfolioId,
            content,
            "dad-lease.pdf",
            "application/pdf",
            content.Length,
            "Dad's lease",
            "Use this for the main portfolio.",
            defaultForPortfolio: true,
            propertyId: null);

        result.Outcome.Should().Be(DocumentTemplateOperationOutcome.Success);
        result.Value!.Name.Should().Be("Dad's lease");
        result.Value.Kind.Should().Be(DocumentTemplateKind.Lease);
        result.Value.RenderMode.Should().Be(DocumentTemplateRenderMode.Overlay);
        result.Value.Status.Should().Be(DocumentTemplateStatus.Draft);
        result.Value.OriginalStoredFileId.Should().NotBeNull();
        result.Value.DefaultForPortfolio.Should().BeTrue();

        var stored = await _ctx.Db.StoredFiles.SingleAsync(f => f.Id == result.Value.OriginalStoredFileId);
        stored.PortfolioId.Should().Be(PortfolioId);
        stored.EntityType.Should().Be("DocumentTemplate");
        stored.EntityId.Should().Be(result.Value.Id);
        stored.FileName.Should().Be("dad-lease.pdf");
        stored.ContentType.Should().Be("application/pdf");
        stored.FileSize.Should().Be(content.Length);
        _files.Contains(stored.FilePath).Should().BeTrue();
    }

    private void SeedTemplate(string name, DocumentTemplateKind kind)
    {
        _ctx.Db.DocumentTemplates.Add(new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            Kind = kind,
            Status = DocumentTemplateStatus.Draft,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = name,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
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
    }
}
