using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

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
    private readonly RentalCommandDbContext _db;
    private readonly RecordingAuditService _audit = new();
    private readonly ApplicationService _sut;

    public ApplicationServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
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

        _sut = new ApplicationService(_db, Mock.Of<IDataUpdateService>(), _audit);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
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

        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.Approved);
        reloaded.ApprovedTenantId.Should().Be(tenant.Id);

        // The PII-touching approval is audited (a Tenant was created).
        _audit.Calls.Should().Contain(c => c.entityType == "Tenant" && c.entityId == tenant.Id
            && c.operation == AuditLogOperation.Created);
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
    public async Task GenerateLinkAsync_RotatesTokenAndReturnsApplyPath()
    {
        var result = await _sut.GenerateLinkAsync(OtherPortfolioId);

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.ApplyPath.Should().Be($"/apply/{result.Token}");

        var portfolio = await _db.Portfolios.SingleAsync(p => p.Id == OtherPortfolioId);
        portfolio.PublicApplicationToken.Should().Be(result.Token);
    }

    [Fact]
    public async Task SaveAndGetFormConfig_RoundTripsAndGeneratesCustomFieldIds()
    {
        // A default property/unit must exist in the portfolio to pass the IDOR guard and be lockable.
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple St",
            City = "Akron",
            State = "OH",
            PostalCode = "44301",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "1A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Units.Add(unit);
        await _db.SaveChangesAsync();

        var request = new SaveFormConfigRequest
        {
            IncomeSources = new IncomeSourcesConfig { Enabled = false },
            Pets = new PetsConfig { Enabled = true, AskDeposit = true },
            CustomFields =
            [
                new CustomFieldInput { Label = "Do you smoke?", Type = "yesno", Required = true },
                new CustomFieldInput
                {
                    Label = "How did you hear about us?",
                    Type = "select",
                    Options = ["Zillow", "Friend"],
                },
            ],
            Defaults = new FormDefaultsConfig
            {
                PropertyId = property.Id,
                UnitId = unit.Id,
                DesiredMoveInDate = "2026-08-01",
            },
            Locked = ["propertyId", "unitId"],
        };

        var saved = await _sut.SaveFormConfigAsync(PortfolioId, userId: 7, request);

        // Returned (normalized) config reflects the input and has server-generated ids.
        saved.IncomeSources.Enabled.Should().BeFalse();
        saved.Pets.Enabled.Should().BeTrue();
        saved.Pets.AskDeposit.Should().BeTrue();
        saved.CustomFields.Should().HaveCount(2);
        saved.CustomFields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Id));
        saved.CustomFields[1].Options.Should().BeEquivalentTo(new[] { "Zillow", "Friend" });
        saved.Defaults.PropertyId.Should().Be(property.Id);
        saved.Defaults.UnitId.Should().Be(unit.Id);
        saved.Defaults.DesiredMoveInDate.Should().Be("2026-08-01");
        saved.Locked.Should().BeEquivalentTo(new[] { "propertyId", "unitId" });

        // The save is audited as a Portfolio update.
        _audit.Calls.Should().Contain(c => c.entityType == "Portfolio" && c.entityId == PortfolioId
            && c.operation == AuditLogOperation.Updated);

        // Reading it back yields the same normalized config (persisted to the portfolio's jsonb column).
        var editor = await _sut.GetFormConfigAsync(PortfolioId);
        editor.Config.Should().BeEquivalentTo(saved);
        editor.Properties.Should().Contain(p => p.Id == property.Id);
    }

    [Fact]
    public async Task SaveFormConfig_DefaultPropertyFromAnotherPortfolio_IsRejected()
    {
        var foreign = new Property
        {
            PortfolioId = OtherPortfolioId,
            Name = "Foreign",
            AddressLine1 = "1 Foreign St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(foreign);
        await _db.SaveChangesAsync();

        var request = new SaveFormConfigRequest
        {
            Defaults = new FormDefaultsConfig { PropertyId = foreign.Id },
        };

        // IDOR guard: a default property from another portfolio is rejected with a validation error.
        var act = async () => await _sut.SaveFormConfigAsync(PortfolioId, userId: 7, request);
        await act.Should().ThrowAsync<ApplicationFormConfigValidationException>();

        // Nothing was persisted.
        var portfolio = await _db.Portfolios.SingleAsync(p => p.Id == PortfolioId);
        portfolio.ApplicationFormConfig.Should().BeNull();
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
        modelBuilder.Entity<RentalApplication>().Property(e => e.IncomeSourcesJson).HasColumnType("TEXT");
        modelBuilder.Entity<RentalApplication>().Property(e => e.PetsJson).HasColumnType("TEXT");
        modelBuilder.Entity<RentalApplication>().Property(e => e.CustomFieldAnswersJson).HasColumnType("TEXT");
        modelBuilder.Entity<Portfolio>().Property(e => e.ApplicationFormConfig).HasColumnType("TEXT");
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
