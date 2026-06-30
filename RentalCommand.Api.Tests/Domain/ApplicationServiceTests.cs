using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="ApplicationService"/> using SQLite in-memory (the service uses provider
/// features unsupported by the EF InMemory provider). Verifies the IDOR-safe public submit (portfolio
/// resolved by token), bad-token rejection, and approve → Tenant creation.
/// </summary>
public class ApplicationServiceTests : IDisposable
{
    private const int PortfolioId = 1;
    private const int OtherPortfolioId = 2;
    private const string Token = "good-token-abc";

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly Mock<IFileStorage> _files = new();
    private readonly RecordingAuditService _audit = new();
    private readonly ApplicationService _sut;

    public ApplicationServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new ApplicationTestDbContext(options);
        _db.Database.EnsureCreated();

        // The portfolio reachable by the public token, plus an unrelated portfolio used to prove
        // cross-tenant isolation.
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Frank's Rentals",
            ManagementCompanyName = "Frank Property Management",
            TimeZone = "UTC",
            PublicApplicationToken = Token,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Portfolios.Add(new Portfolio
        {
            Id = OtherPortfolioId,
            Name = "Other Co",
            ManagementCompanyName = "Other Management",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _sut = new ApplicationService(
            _db, _files.Object, Mock.Of<IDataUpdateService>(), _audit,
            new NoopTenantPortalProvisioningService(), NullLogger<ApplicationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedApplication("Ada", "Alpha", ApplicationStatus.Submitted);
        SeedApplication("Bea", "Bravo", ApplicationStatus.Submitted);
        SeedApplication("Cora", "Cedar", ApplicationStatus.Submitted);
        SeedApplication("Dee", "Delta", ApplicationStatus.Approved);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, "Submitted", new ListQuery
        {
            Sort = "lastName",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(3);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(a => a.LastName).Should().Equal("Bravo", "Cedar");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"RentalApplications\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_UnitIdFilter_ReturnsOnlyThatUnitsApplicationsDbSide()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove",
            AddressLine1 = "1100 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();

        var unitA = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "A",
            Status = UnitStatus.Vacant,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var unitB = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "B",
            Status = UnitStatus.Vacant,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Units.AddRange(unitA, unitB);
        await _db.SaveChangesAsync();

        SeedApplicationForUnit("Ada", "Alpha", unitA.Id, property.Id);
        SeedApplicationForUnit("Bea", "Bravo", unitA.Id, property.Id);
        SeedApplicationForUnit("Cora", "Cedar", unitB.Id, property.Id);
        SeedApplicationForUnit("Dee", "Delta", unitId: null, propertyId: property.Id);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, status: null, new ListQuery(), unitId: unitA.Id);

        result.TotalCount.Should().Be(2, "only the two applications tied to unit A are in scope");
        result.Items.Select(a => a.LastName).Should().BeEquivalentTo(["Alpha", "Bravo"]);

        // The unit scope must run as a SQL WHERE on UnitId (DB-side), never an in-memory filter.
        _commands.Should().Contain(sql =>
            sql.Contains("\"UnitId\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SubmitAsync_GoodToken_CreatesApplicationInTokensPortfolio()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "Dana",
            LastName = "Lopez",
            Email = "dana@example.com",
            ConsentGiven = true,
        };

        var result = await _sut.SubmitAsync(Token, request, "203.0.113.7");

        result.Should().NotBeNull();
        result!.Status.Should().Be("Submitted");

        var saved = await _db.RentalApplications.SingleAsync();
        saved.PortfolioId.Should().Be(PortfolioId, "the portfolio is resolved from the token, never the client");
        saved.FirstName.Should().Be("Dana");
        saved.Status.Should().Be(ApplicationStatus.Submitted);
        // FCRA consent provenance is captured server-side.
        saved.ConsentGiven.Should().BeTrue();
        saved.ConsentAtUtc.Should().NotBeNull();
        saved.ConsentIpAddress.Should().Be("203.0.113.7");
    }

    [Fact]
    public async Task SubmitAsync_DuplicateOpenApplicationEmail_ThrowsConflictAndCreatesNothing()
    {
        _db.RentalApplications.Add(new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Lopez",
            Email = "Dana@Example.com",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var request = new SubmitApplicationRequest
        {
            FirstName = "Dana",
            LastName = "Lopez",
            Email = " dana@example.com ",
            ConsentGiven = true,
        };

        var act = () => _sut.SubmitAsync(Token, request, "203.0.113.7");

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("application #1");
        (await _db.RentalApplications.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_BadToken_ReturnsNullAndCreatesNothing()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "Mallory",
            LastName = "Bad",
            ConsentGiven = true,
        };

        var result = await _sut.SubmitAsync("not-a-real-token", request, "203.0.113.9");

        result.Should().BeNull();
        (await _db.RentalApplications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetPublicFormInfoAsync_BadToken_ReturnsNull()
    {
        var info = await _sut.GetPublicFormInfoAsync("nope");
        info.Should().BeNull();
    }

    [Fact]
    public async Task GetPublicFormInfoAsync_ReturnsUnitStatusesAndFiltersOfflineUnitsDbSide()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove",
            Status = PropertyStatus.Active,
            AddressLine1 = "1100 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();

        _db.Units.AddRange(
            new Unit
            {
                PropertyId = property.Id,
                UnitNumber = "1A",
                Status = UnitStatus.Vacant,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new Unit
            {
                PropertyId = property.Id,
                UnitNumber = "2B",
                Status = UnitStatus.Occupied,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new Unit
            {
                PropertyId = property.Id,
                UnitNumber = "3C",
                Status = UnitStatus.Offline,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        _commands.Clear();
        var info = await _sut.GetPublicFormInfoAsync(Token);

        info.Should().NotBeNull();
        var units = info!.Properties.Should().ContainSingle().Subject.Units;
        units.Select(u => u.UnitNumber).Should().Equal("1A", "2B");
        units.Single(u => u.UnitNumber == "1A").Status.Should().Be(UnitStatus.Vacant);
        units.Single(u => u.UnitNumber == "2B").Status.Should().Be(UnitStatus.Occupied);

        _commands.Should().HaveCountLessThanOrEqualTo(3);
        _commands.Should().Contain(sql =>
            sql.Contains("\"Units\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Status\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("<>") || sql.Contains("!=")));
    }

    [Fact]
    public async Task SubmitAsync_ForeignPropertyId_IsDroppedNotLeaked()
    {
        // A property in a DIFFERENT portfolio must never attach to this submission (IDOR guard).
        _db.Properties.Add(new Property
        {
            Id = 99,
            PortfolioId = OtherPortfolioId,
            Name = "Foreign Bldg",
            AddressLine1 = "1 Foreign St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var request = new SubmitApplicationRequest
        {
            FirstName = "Sam",
            LastName = "Cross",
            PropertyId = 99,
            ConsentGiven = true,
        };

        var result = await _sut.SubmitAsync(Token, request, null);

        result.Should().NotBeNull();
        var saved = await _db.RentalApplications.SingleAsync();
        saved.PropertyId.Should().BeNull("a property from another portfolio must be dropped");
    }

    [Fact]
    public async Task ApproveAsync_CreatesTenantFromApplication()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Lopez",
            Email = "dana@example.com",
            Phone = "555-0100",
            Employer = "Acme Co",
            MonthlyIncome = 5200m,
            CurrentAddressLine2 = "Apt 3",
            CurrentCity = "Columbus",
            CurrentState = "OH",
            CurrentPostalCode = "43215",
            CurrentAddress = "44 Cedar Bend Apt 3, Columbus, OH 43215",
            ConsentGiven = true,
            ConsentAtUtc = DateTime.UtcNow,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var result = await _sut.ApproveAsync(PortfolioId, app.Id, userId: 7);

        result.Should().NotBeNull();
        result!.Status.Should().Be("Approved");
        result.TenantId.Should().BeGreaterThan(0);

        var tenant = await _db.Tenants.SingleAsync();
        tenant.Id.Should().Be(result.TenantId);
        tenant.PortfolioId.Should().Be(PortfolioId);
        tenant.FirstName.Should().Be("Dana");
        tenant.LastName.Should().Be("Lopez");
        tenant.Email.Should().Be("dana@example.com");
        tenant.Notes.Should().Contain("Acme Co");
        tenant.Notes.Should().Contain("Prior address: 44 Cedar Bend Apt 3, Columbus, OH 43215.");
        tenant.Notes.Should().NotContain("43215, Apt 3");

        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.Approved);
        reloaded.ApprovedTenantId.Should().Be(tenant.Id);

        // The PII-touching approval is audited (a Tenant was created).
        _audit.Calls.Should().Contain(c => c.entityType == "Tenant" && c.entityId == tenant.Id
            && c.operation == AuditLogOperation.Created);
    }

    [Fact]
    public async Task SubmitAsync_FullScannedAddress_DoesNotDuplicateStructuredParts()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "Jordan",
            LastName = "Ellis",
            CurrentAddressLine1 = "44 Cedar Bend Apt 3, Columbus, OH 43215",
            CurrentAddressLine2 = "Apt 3",
            CurrentCity = "Columbus",
            CurrentState = "OH",
            CurrentPostalCode = "43215",
            ConsentGiven = true,
        };

        var result = await _sut.SubmitAsync(Token, request, "203.0.113.7");

        result.Should().NotBeNull();
        var saved = await _db.RentalApplications.SingleAsync();
        saved.CurrentAddress.Should().Be("44 Cedar Bend Apt 3, Columbus, OH 43215");
    }

    [Fact]
    public async Task ListAndGetAsync_ReturnRequestedHomeNamesWithApplication()
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove Duplex",
            AddressLine1 = "1100 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
            Status = UnitStatus.Vacant,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Units.Add(unit);
        await _db.SaveChangesAsync();

        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            FirstName = "Jordan",
            LastName = "Ellis",
            Status = ApplicationStatus.Submitted,
            ConsentGiven = true,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var list = await _sut.ListAsync(PortfolioId, status: null, new ListQuery());
        var detail = await _sut.GetAsync(PortfolioId, app.Id);

        list.Should().ContainSingle();
        list[0].PropertyName.Should().Be("Maple Grove Duplex");
        list[0].UnitNumber.Should().Be("B");
        detail.Should().NotBeNull();
        detail!.PropertyName.Should().Be("Maple Grove Duplex");
        detail.UnitNumber.Should().Be("B");
    }

    [Fact]
    public async Task GetAsync_ExposesAttachedScannedApplicationImage()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Gray",
            LastName = "Johnson",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = PortfolioId,
            EntityType = "Application",
            EntityId = app.Id,
            FileName = "application-001.jpg",
            FilePath = "applications/application-001.jpg",
            ContentType = "image/jpeg",
            FileSize = 12345,
            UploadedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _files
            .Setup(f => f.DownloadAsync("applications/application-001.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));

        var detail = await _sut.GetAsync(PortfolioId, app.Id);

        detail.Should().NotBeNull();
        detail!.HasScan.Should().BeTrue();
        detail.ScanIsImage.Should().BeTrue();
    }

    [Fact]
    public async Task ApproveAsync_AlreadyApproved_Throws()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Already",
            LastName = "Approved",
            Status = ApplicationStatus.Approved,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var act = async () => await _sut.ApproveAsync(PortfolioId, app.Id, userId: 7);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateFromScanAsync_OpenApplicationWithSameEmail_ThrowsAndDoesNotDuplicate()
    {
        var existing = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Quinn",
            LastName = "Applicant",
            Email = "qa.applicant.001@example.local",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
        _db.RentalApplications.Add(existing);
        await _db.SaveChangesAsync();

        var request = new CreateApplicationRequest
        {
            FirstName = "Quinn",
            LastName = "Applicant",
            Email = " QA.Applicant.001@EXAMPLE.local ",
            CurrentAddress = "110 Cedar St, Columbus, OH 43215",
        };

        var act = async () => await _sut.CreateFromScanAsync(PortfolioId, request, userId: 7);

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("qa.applicant.001@example.local");
        ex.Which.Message.Should().Contain($"application #{existing.Id}");
        (await _db.RentalApplications.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(ApplicationStatus.Declined)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task CreateFromScanAsync_TerminalApplicationWithSameEmail_CreatesNewApplication(ApplicationStatus status)
    {
        var existing = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Quinn",
            LastName = "Applicant",
            Email = "qa.applicant.001@example.local",
            Status = status,
            SubmittedAtUtc = DateTime.UtcNow.AddDays(-10),
            ReviewedAtUtc = DateTime.UtcNow.AddDays(-9),
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-9),
        };
        _db.RentalApplications.Add(existing);
        await _db.SaveChangesAsync();

        var request = new CreateApplicationRequest
        {
            FirstName = "Quinn",
            LastName = "Applicant",
            Email = "qa.applicant.001@example.local",
        };

        var result = await _sut.CreateFromScanAsync(PortfolioId, request, userId: 7);

        result.Id.Should().NotBe(existing.Id);
        result.Status.Should().Be(ApplicationStatus.Submitted.ToString());
        (await _db.RentalApplications.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GenerateLinkAsync_RotatesTokenAndReturnsApplyPath()
    {
        var result = await _sut.GenerateLinkAsync(OtherPortfolioId);

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.ApplyPath.Should().Be($"/apply/{result.Token}");

        var portfolio = await _db.Portfolios.SingleAsync(p => p.Id == OtherPortfolioId);
        portfolio.PublicApplicationToken.Should().Be(result.Token);
    }

    private void SeedApplication(string firstName, string lastName, ApplicationStatus status)
    {
        _db.RentalApplications.Add(new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName}.{lastName}@example.local".ToLowerInvariant(),
            Phone = "555-0100",
            MonthlyIncome = 4_000m,
            Status = status,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void SeedApplicationForUnit(string firstName, string lastName, int? unitId, int propertyId)
    {
        _db.RentalApplications.Add(new RentalApplication
        {
            PortfolioId = PortfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            FirstName = firstName,
            LastName = lastName,
            Email = $"{firstName}.{lastName}@example.local".ToLowerInvariant(),
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private sealed class RecordingAuditService : IAuditTrailService
    {
        public List<(int portfolioId, string entityType, int entityId, AuditLogOperation operation)> Calls { get; } = [];

        public Task LogAsync(
            int portfolioId, string entityType, int entityId, AuditLogOperation operation,
            int? userId = null, string? actorLabel = null, string? oldValues = null,
            string? newValues = null, string? changeReason = null, string? ipAddress = null,
            CancellationToken ct = default)
        {
            Calls.Add((portfolioId, entityType, entityId, operation));
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
    }
}

/// <summary>
/// Derived DbContext that remaps Postgres-specific column types to SQLite-friendly ones for tests.
/// </summary>
internal sealed class ApplicationTestDbContext : RentalCommandDbContext
{
    public ApplicationTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // jsonb is not understood by SQLite — remap those columns to plain text.
        modelBuilder.Entity<RentalApplication>().Property(e => e.IdExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<ScreeningResult>().Property(e => e.RawResultJson).HasColumnType("TEXT");
        modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
        modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
        modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
        modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");

        // Drop the Postgres check constraints SQLite can't execute.
        modelBuilder.Entity<Lease>().ToTable("Leases");
    }
}
