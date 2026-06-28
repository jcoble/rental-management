using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class DocumentTemplateServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly DocumentTemplateFieldCatalog _catalog = new();
    private readonly DocumentTemplateService _sut;

    public DocumentTemplateServiceTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new DocumentTemplateService(_ctx.Db, _catalog);
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
}

