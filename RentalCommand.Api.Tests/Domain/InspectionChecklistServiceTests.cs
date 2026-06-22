using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the smart-checklist inspection workflow: built-in templates surface from the templates
/// endpoint, starting an inspection from a template materializes Pending items, and completing an
/// inspection spawns a work order per Fail item, flips status to Completed, and records a report file.
/// </summary>
public class InspectionChecklistServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _executedSql = [];
    private readonly RentalCommandDbContext _db;
    private readonly InspectionService _service;

    public InspectionChecklistServiceTests()
    {
        // The real QuestPDF generator runs in the completion test; license must be set once.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new InspectionTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _service = new InspectionService(
            _db,
            new NoopInspectionDataUpdate(),
            new InMemoryFileStorage(),
            new InspectionReportPdfGenerator(),
            NullLogger<InspectionService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ListTemplates_ReturnsBuiltIns_WithItems()
    {
        var templates = await _service.ListTemplatesAsync(PortfolioId);

        templates.Should().HaveCountGreaterThanOrEqualTo(3);
        templates.Where(t => t.IsBuiltIn).Should().HaveCount(3);
        templates.Select(t => t.InspectionType).Should().Contain(new[]
        {
            InspectionType.MoveIn, InspectionType.MoveOut, InspectionType.AnnualSafety
        });
        // Built-in ids are negative so they never collide with DB-generated custom ids.
        templates.Where(t => t.IsBuiltIn).Should().OnlyContain(t => t.Id < 0 && t.PortfolioId == null);
        templates.First(t => t.InspectionType == InspectionType.AnnualSafety).Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_FromTemplate_MaterializesPendingItems()
    {
        var property = SeedProperty();
        var annualTemplate = InspectionTemplateCatalog.BuiltIns
            .First(t => t.InspectionType == InspectionType.AnnualSafety);

        var created = await _service.CreateAsync(PortfolioId, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.AnnualSafety,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = annualTemplate.Id, // negative built-in id
        });

        created.Should().NotBeNull();
        created!.TemplateId.Should().Be(annualTemplate.Id);
        created.Items.Should().HaveCount(annualTemplate.Items.Count);
        created.Items.Should().OnlyContain(i => i.Result == InspectionItemResult.Pending);
        created.Items.Select(i => i.SortOrder).Should().BeInAscendingOrder();

        // Items are persisted and reachable via GET detail.
        var detail = await _service.GetAsync(PortfolioId, created.Id);
        detail!.Items.Should().HaveCount(annualTemplate.Items.Count);
    }

    [Fact]
    public async Task Create_WithUnknownTemplate_ReturnsNull()
    {
        var property = SeedProperty();

        var created = await _service.CreateAsync(PortfolioId, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            ScheduledFor = DateTime.UtcNow,
            TemplateId = 99999, // not a built-in (positive) and not an in-portfolio custom template
        });

        created.Should().BeNull("an unknown template id must be rejected");
    }

    [Fact]
    public async Task Complete_SpawnsWorkOrderPerFail_SetsCompleted_AndReport()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAsync(PortfolioId, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        });
        created.Should().NotBeNull();

        // Mark two items Fail, one Pass — leave the rest Pending.
        var items = created!.Items.OrderBy(i => i.SortOrder).ToList();
        await _service.UpdateItemAsync(PortfolioId, created.Id, items[0].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Faucet leaks badly" });
        await _service.UpdateItemAsync(PortfolioId, created.Id, items[1].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = "Burner won't ignite" });
        await _service.UpdateItemAsync(PortfolioId, created.Id, items[2].Id,
            new UpdateInspectionItemRequest { Result = InspectionItemResult.Pass });

        var (summary, error) = await _service.CompleteAsync(PortfolioId, created.Id, userId: 7);

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.Status.Should().Be(InspectionStatus.Completed);
        summary.FailCount.Should().Be(2);
        summary.PassCount.Should().Be(1);
        summary.CreatedWorkOrderIds.Should().HaveCount(2);
        summary.ReportStoredFileId.Should().NotBeNull();

        // Inspection row is Completed and carries the report file id.
        var inspection = await _db.Inspections.AsNoTracking().FirstAsync(i => i.Id == created.Id);
        inspection.Status.Should().Be(InspectionStatus.Completed);
        inspection.CompletedAt.Should().NotBeNull();
        inspection.ReportStoredFileId.Should().Be(summary.ReportStoredFileId);

        // A real work order exists for each Fail item, linked both ways.
        var failedItems = await _db.InspectionItems.AsNoTracking()
            .Where(i => i.InspectionId == created.Id && i.Result == InspectionItemResult.Fail)
            .ToListAsync();
        failedItems.Should().OnlyContain(i => i.SpawnedWorkOrderId != null);

        foreach (var woId in summary.CreatedWorkOrderIds)
        {
            var wo = await _db.WorkOrders.AsNoTracking().FirstAsync(w => w.Id == woId);
            wo.PropertyId.Should().Be(property.Id);
            wo.Category.Should().Be("Inspection");
            // Initial status event written in the same save as the work order.
            (await _db.WorkOrderStatusEvents.CountAsync(e => e.WorkOrderId == woId))
                .Should().BeGreaterThanOrEqualTo(1);
        }

        // The generated report is a StoredFile linked to the inspection.
        var report = await _db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == summary.ReportStoredFileId);
        report.EntityType.Should().Be("Inspection");
        report.EntityId.Should().Be(created.Id);
        report.ContentType.Should().Be("application/pdf");
        report.FileSize.Should().BeGreaterThan(0);

        // The report is downloadable.
        var download = await _service.GetReportAsync(PortfolioId, created.Id);
        download.Should().NotBeNull();
        download!.Value.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task Complete_BatchesFailedItemWorkOrderCreationWithoutPerItemScopeQueries()
    {
        var property = SeedProperty();
        var moveIn = InspectionTemplateCatalog.BuiltIns.First(t => t.InspectionType == InspectionType.MoveIn);

        var created = await _service.CreateAsync(PortfolioId, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            Type = InspectionType.MoveIn,
            ScheduledFor = DateTime.UtcNow,
            Inspector = "Jane Doe",
            TemplateId = moveIn.Id,
        });
        created.Should().NotBeNull();

        var items = created!.Items.OrderBy(i => i.SortOrder).Take(3).ToList();
        foreach (var item in items)
        {
            await _service.UpdateItemAsync(PortfolioId, created.Id, item.Id,
                new UpdateInspectionItemRequest { Result = InspectionItemResult.Fail, Note = $"Fail {item.Id}" });
        }

        _executedSql.Clear();
        var (summary, error) = await _service.CompleteAsync(PortfolioId, created.Id, userId: 7);

        error.Should().BeNull();
        summary.Should().NotBeNull();
        summary!.CreatedWorkOrderIds.Should().HaveCount(3);

        var propertyScopeChecks = _executedSql.Count(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
        propertyScopeChecks.Should().BeLessThanOrEqualTo(1,
            "inspection completion should not revalidate the same property once per failed checklist item");

        var workOrderHydrationReads = _executedSql.Count(sql =>
            sql.Contains("FROM \"WorkOrders\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase));
        workOrderHydrationReads.Should().BeLessThanOrEqualTo(1,
            "broadcast payloads should be hydrated with one projection instead of one read per created work order");
    }

    [Fact]
    public async Task Complete_AlreadyCompleted_ReturnsError()
    {
        var property = SeedProperty();
        var created = await _service.CreateAsync(PortfolioId, new CreateInspectionRequest
        {
            PropertyId = property.Id,
            ScheduledFor = DateTime.UtcNow,
        });

        await _service.CompleteAsync(PortfolioId, created!.Id, userId: 1);
        var (summary, error) = await _service.CompleteAsync(PortfolioId, created.Id, userId: 1);

        summary.Should().BeNull();
        error.Should().NotBeNull();
    }

    private Property SeedProperty()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "1 Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private sealed class NoopInspectionDataUpdate : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>In-memory <see cref="IFileStorage"/> so report generation works without disk.</summary>
    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var key = $"{Guid.NewGuid():N}_{fileName}";
            _files[key] = ms.ToArray();
            return key;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
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

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>SQLite-compatible context: strips Postgres-only DDL the same way other suites do.</summary>
    private sealed class InspectionTestDbContext : RentalCommandDbContext
    {
        public InspectionTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
            modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
            modelBuilder.Entity<BankTransaction>().Property(e => e.RawData).HasColumnType("TEXT");
            modelBuilder.Entity<SecurityDepositHolding>().Property(e => e.DeductionsJson).HasColumnType("TEXT");
            modelBuilder.Entity<RentalApplication>().Property(e => e.IdExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<Lease>().ToTable("Leases");
            modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
        }
    }
}
