using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Tests;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
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
    private readonly ServiceProvider _services;
    private readonly ApplicationService _sut;
    private readonly WorkspaceReadScope _scope;

    public ApplicationServiceTests()
    {
        _conn = new SqliteConnection($"Data Source=application-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _conn.Open();

        _services = AtomicDomainTestKernel.CreateForApplications(
            _conn.ConnectionString,
            [new RecordingCommandInterceptor(_commands)]);
        _db = _services.GetRequiredService<RentalCommandDbContext>();
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
        _scope = _db.SeedAdministratorScope(PortfolioId, nameof(ApplicationServiceTests));
        _sut = new ApplicationService(
            _db, _files.Object, Mock.Of<IDataUpdateService>(), _audit,
            TimeProvider.System,
            _services.GetRequiredService<IRequestWriteExecutor>());
    }

    public void Dispose()
    {
        _services.Dispose();
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
        var result = await _sut.ListPageAuthorizedAsync(_scope, "Submitted", new ListQuery
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
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var unitB = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "B",
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
        var result = await _sut.ListPageAuthorizedAsync(_scope, status: null, new ListQuery(), unitId: unitA.Id);

        result.TotalCount.Should().Be(2, "only the two applications tied to unit A are in scope");
        result.Items.Select(a => a.LastName).Should().BeEquivalentTo(["Alpha", "Bravo"]);
        result.Items.Should().OnlyContain(a =>
            a.PropertyName == "Maple Grove" && (a.UnitNumber == "A"));

        _commands.Should().HaveCount(2, "the application page is one count plus one page statement");
        var countSql = _commands.Single(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        var pageSql = _commands.Single(sql =>
            !sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        countSql.Should().Contain("\"UnitId\"");
        pageSql.Should().Contain("\"UnitId\"");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("LEFT JOIN");
        pageSql.Should().Contain("\"PropertyName\"");
        pageSql.Should().Contain("\"UnitNumber\"");
        pageSql.Should().Contain("\"FirstName\"");
        pageSql.Should().Contain("\"ApprovedTenantId\"");
        pageSql.Should().NotContain("SELECT *");
        _commands.Should().OnlyContain(sql =>
            sql.Contains("\"UnitId\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("RentalApplications", StringComparison.OrdinalIgnoreCase));
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

        var result = await _sut.SubmitAsync(Token, request, "203.0.113.7", Guid.NewGuid().ToString("N"));

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

        var act = () => _sut.SubmitAsync(Token, request, "203.0.113.7", Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.StatusCode.Should().Be(409);
        ex.Which.Message.Should().Contain("already exists");
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

        var result = await _sut.SubmitAsync("not-a-real-token", request, "203.0.113.9", Guid.NewGuid().ToString("N"));

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
    public async Task SubmitAsync_ForeignPropertyId_IsRejectedWithoutLeakingOrPersisting()
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

        var act = () => _sut.SubmitAsync(Token, request, null, Guid.NewGuid().ToString("N"));

        var error = await act.Should().ThrowAsync<DomainValidationException>();
        error.Which.Message.Should().Be("Selected property was not found.");
        (await _db.RentalApplications.CountAsync()).Should().Be(0,
            "a property from another portfolio must reject the complete submission");
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

        var result = await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, Guid.NewGuid().ToString("N"));

        result.Should().NotBeNull();
        result!.Status.Should().Be("Approved");
        result.TenantId.Should().BeGreaterThan(0);

        _db.ChangeTracker.Clear();
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
        (await _db.AtomicAuditLogs.AsNoTracking().AnyAsync(c =>
            c.EntityType == "Tenant" && c.EntityId == tenant.Id
            && c.Operation == AuditLogOperation.Created)).Should().BeTrue();
    }

    [Fact]
    public async Task ApproveAsync_CreatesSyntheticScreeningAndApplicantCommunicationOnce()
    {
        var now = DateTime.UtcNow;
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Hector",
            LastName = "Reed",
            Email = "tenant.043@example.local",
            Phone = "555-0143",
            ConsentGiven = true,
            ConsentAtUtc = now.AddMinutes(-5),
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now.AddMinutes(-10),
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-10),
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var first = await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, "approve-hector-primary");
        var second = await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, "approve-hector-recovery");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.TenantId.Should().Be(first!.TenantId);

        _db.ChangeTracker.Clear();
        (await _db.Tenants.CountAsync(t => t.Email == "tenant.043@example.local")).Should().Be(1);

        var screening = await _db.ApplicantScreenings.SingleAsync(s => s.ApplicationId == app.Id);
        screening.Mode.Should().Be(ScreeningMode.External);
        screening.Status.Should().Be(ApplicantScreeningStatus.Completed);
        screening.ProviderDisplayName.Should().Be("Synthetic test screening");
        screening.Decision.Should().Be(ScreeningDecision.Accept);
        screening.ConsumerReportUsedForDecision.Should().BeFalse();
        screening.CreditReportingAgencyName.Should().BeNull();

        (await _db.ApplicantScreeningMilestones.CountAsync(m =>
            m.ApplicantScreeningId == screening.Id
            && m.Source == "application-approval"
            && m.EventType == "synthetic.completed")).Should().Be(1);

        var conversation = await _db.Conversations
            .Include(c => c.Messages)
            .SingleAsync(c => c.TenantId == first.TenantId && c.Subject == $"Rental application #{app.Id}");
        conversation.PortfolioId.Should().Be(PortfolioId);
        conversation.WorkOrderId.Should().BeNull();
        conversation.TenantUnreadCount.Should().Be(1);
        conversation.Messages.Should().ContainSingle();
        conversation.Messages.Single().SenderRole.Should().Be(ConversationSenderRole.Landlord);
        conversation.Messages.Single().Channels.Should().Be("Portal");
    }

    [Fact]
    public async Task ApproveAsync_ApprovedApplicationRecoversMissingArtifactsWithoutDuplicateTenant()
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Existing",
            LastName = "Applicant",
            Email = "existing.approved@example.local",
            CreatedAt = now.AddMinutes(-20),
            UpdatedAt = now.AddMinutes(-20),
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();

        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = tenant.FirstName,
            LastName = tenant.LastName,
            Email = tenant.Email,
            ConsentGiven = true,
            ConsentAtUtc = now.AddMinutes(-30),
            Status = ApplicationStatus.Approved,
            SubmittedAtUtc = now.AddMinutes(-40),
            ReviewedAtUtc = now.AddMinutes(-20),
            ApprovedTenantId = tenant.Id,
            CreatedAt = now.AddMinutes(-40),
            UpdatedAt = now.AddMinutes(-20),
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var result = await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, "recover-approved-application");
        var replay = await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, "recover-approved-application-retry");

        result.Should().NotBeNull();
        replay.Should().NotBeNull();
        result!.TenantId.Should().Be(tenant.Id);
        replay!.TenantId.Should().Be(tenant.Id);

        _db.ChangeTracker.Clear();
        (await _db.Tenants.CountAsync(t => t.Email == tenant.Email)).Should().Be(1);
        (await _db.ApplicantScreenings.CountAsync(s => s.ApplicationId == app.Id)).Should().Be(1);
        (await _db.Conversations.CountAsync(c =>
            c.TenantId == tenant.Id && c.Subject == $"Rental application #{app.Id}")).Should().Be(1);
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

        var result = await _sut.SubmitAsync(Token, request, "203.0.113.7", Guid.NewGuid().ToString("N"));

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
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
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

        var list = await _sut.ListAuthorizedAsync(_scope, status: null, new ListQuery());
        var detail = await _sut.GetAsync(PortfolioId, app.Id);

        list.Should().ContainSingle();
        list[0].PropertyName.Should().Be("Maple Grove Duplex");
        list[0].UnitNumber.Should().Be("B");
        detail.Should().NotBeNull();
        detail!.PropertyName.Should().Be("Maple Grove Duplex");
        detail.UnitNumber.Should().Be("B");
    }

    [Fact]
    public async Task UpdateAsync_CorrectsOpenApplicationAndAuditsUpdate()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Jesse",
            LastName = "Coble",
            Email = "old@example.test",
            Phone = "555-0000",
            DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CurrentAddress = "Old address",
            Employer = "Old Employer",
            MonthlyIncome = 2500m,
            DesiredMoveInDate = DateTime.UtcNow.AddDays(15),
            Notes = "Original note",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow.AddDays(-2),
            CreatedAt = DateTime.UtcNow.AddDays(-2),
            UpdatedAt = DateTime.UtcNow.AddDays(-2),
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var result = await _sut.UpdateAuthorizedAsync(
            _scope,
            app.Id,
            new UpdateApplicationRequest
            {
                FirstName = "JESSE",
                LastName = "NATHANIEL COBLE",
                Email = "updated@example.test",
                Phone = "555-0101",
                DateOfBirth = new DateTime(1988, 5, 4),
                CurrentAddress = "123 Updated Ave",
                Employer = "Updated Employer",
                MonthlyIncome = 6100m,
                ClearDesiredMoveInDate = true,
                Notes = "Corrected by landlord",
            },
            userId: 7,
            operationKey: Guid.NewGuid().ToString("N"));

        result.Should().NotBeNull();
        result!.FirstName.Should().Be("JESSE");
        result.LastName.Should().Be("NATHANIEL COBLE");
        result.Email.Should().Be("updated@example.test");
        result.Phone.Should().Be("555-0101");
        result.DateOfBirth.Should().Be(new DateTime(1988, 5, 4, 0, 0, 0, DateTimeKind.Utc));
        result.CurrentAddress.Should().Be("123 Updated Ave");
        result.Employer.Should().Be("Updated Employer");
        result.MonthlyIncome.Should().Be(6100m);
        result.DesiredMoveInDate.Should().BeNull();
        result.Notes.Should().Be("Corrected by landlord");

        _db.ChangeTracker.Clear();
        var reloaded = await _db.RentalApplications.SingleAsync(a => a.Id == app.Id);
        reloaded.Status.Should().Be(ApplicationStatus.Submitted);
        reloaded.UpdatedAt.Should().BeAfter(app.CreatedAt);
        (await _db.AtomicAuditLogs.AsNoTracking().AnyAsync(c =>
            c.EntityType == "RentalApplication" && c.EntityId == app.Id
            && c.Operation == AuditLogOperation.Updated)).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_CorrectsUnderReviewApplication()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Under",
            LastName = "Review",
            Status = ApplicationStatus.UnderReview,
            SubmittedAtUtc = DateTime.UtcNow.AddDays(-3),
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            UpdatedAt = DateTime.UtcNow.AddDays(-2),
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var result = await _sut.UpdateAuthorizedAsync(
            _scope,
            app.Id,
            new UpdateApplicationRequest { LastName = "Corrected" },
            userId: 7,
            operationKey: Guid.NewGuid().ToString("N"));

        result.Should().NotBeNull();
        result!.Status.Should().Be(ApplicationStatus.UnderReview.ToString());
        result.LastName.Should().Be("Corrected");
    }

    [Fact]
    public async Task UpdateAsync_RejectsForeignProperty()
    {
        var foreignProperty = new Property
        {
            PortfolioId = OtherPortfolioId,
            Name = "Foreign Property",
            AddressLine1 = "1 Foreign St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(foreignProperty);
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Local",
            LastName = "Applicant",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var act = async () => await _sut.UpdateAuthorizedAsync(
            _scope,
            app.Id,
            new UpdateApplicationRequest { PropertyId = foreignProperty.Id },
            userId: 7,
            operationKey: Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("Selected property was not found");
        (await _db.RentalApplications.SingleAsync(a => a.Id == app.Id)).PropertyId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_RejectsApprovedApplications()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Already",
            LastName = "Approved",
            Status = ApplicationStatus.Approved,
            SubmittedAtUtc = DateTime.UtcNow.AddDays(-5),
            ReviewedAtUtc = DateTime.UtcNow.AddDays(-4),
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = DateTime.UtcNow.AddDays(-4),
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var act = async () => await _sut.UpdateAuthorizedAsync(
            _scope,
            app.Id,
            new UpdateApplicationRequest { FirstName = "Changed" },
            userId: 7,
            operationKey: Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("Only submitted or under-review applications");
        (await _db.RentalApplications.SingleAsync(a => a.Id == app.Id)).FirstName.Should().Be("Already");
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

        var act = async () => await _sut.ApproveAuthorizedAsync(_scope, app.Id, 7, Guid.NewGuid().ToString("N"));
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

        var act = async () => await _sut.CreateFromScanAsync(_scope, request, Guid.NewGuid().ToString("N"));

        var ex = await act.Should().ThrowAsync<DomainValidationException>();
        ex.Which.Message.Should().Contain("qa.applicant.001@example.local");
        ex.Which.Message.Should().Contain("already exists");
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

        var result = await _sut.CreateFromScanAsync(_scope, request, Guid.NewGuid().ToString("N"));

        result.Id.Should().NotBe(existing.Id);
        result.Status.Should().Be(ApplicationStatus.Submitted.ToString());
        (await _db.RentalApplications.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GenerateLinkAsync_RotatesTokenAndReturnsApplyPath()
    {
        var result = await _sut.GenerateLinkAsync(_scope, Guid.NewGuid().ToString("N"));

        result.Token.Should().NotBeNullOrWhiteSpace();
        result.ApplyPath.Should().Be($"/apply/{result.Token}");

        _db.ChangeTracker.Clear();
        var portfolio = await _db.Portfolios.AsNoTracking().SingleAsync(p => p.Id == PortfolioId);
        portfolio.PublicApplicationToken.Should().Be(result.Token);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesPortfolioApplicationAndHidesItFromReads()
    {
        var app = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Rina",
            LastName = "Park",
            Email = "rina@example.local",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.RentalApplications.Add(app);
        await _db.SaveChangesAsync();

        var deleted = await _sut.DeleteAuthorizedAsync(_scope, app.Id, 42, Guid.NewGuid().ToString("N"));

        deleted.Should().BeTrue();
        _db.ChangeTracker.Clear();
        (await _sut.GetAsync(PortfolioId, app.Id)).Should().BeNull();
        var stored = await _db.RentalApplications
            .IgnoreQueryFilters()
            .SingleAsync(a => a.Id == app.Id);
        stored.DeletedAt.Should().NotBeNull();
        stored.UpdatedAt.Should().Be(stored.DeletedAt);
        (await _db.AtomicAuditLogs.AsNoTracking().AnyAsync(c =>
            c.EntityType == "RentalApplication" && c.EntityId == app.Id
            && c.Operation == AuditLogOperation.Deleted)).Should().BeTrue();
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

        public void EnsureAtomicCommand() { }

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
/// SQLite application context using the shared test-only compatibility model.
/// </summary>
internal sealed class ApplicationTestDbContext : SqliteCompatibleRentalCommandDbContext
{
    public ApplicationTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}

[Collection(MigratedPostgreSqlCollection.Name3)]
public sealed class ApplicationServicePostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int ActorId = 1;
    private const string Token = "good-token-abc";

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ApplicationService _sut = null!;

    public ApplicationServicePostgreSqlTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);

        var portfolio = await _ctx.Db.Portfolios.SingleAsync(p => p.Id == PortfolioId);
        portfolio.PublicApplicationToken = Token;
        await _ctx.Db.SaveChangesAsync();

        _sut = new ApplicationService(
            _ctx.Db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            Mock.Of<IRequestWriteExecutor>());
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task GetPublicFormInfoAsync_ReturnsDerivedAvailabilityAndFiltersOfflineUnitsDbSide()
    {
        var now = DateTime.UtcNow;
        var emptyProperty = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Aspen House",
            Status = PropertyStatus.Active,
            AddressLine1 = "900 Aspen Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove",
            Status = PropertyStatus.Active,
            AddressLine1 = "1100 Maple Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.AddRange(emptyProperty, property);
        await _ctx.Db.SaveChangesAsync();

        var vacant = Unit(property.Id, "1A", now);
        var occupied = Unit(property.Id, "2B", now);
        var offline = Unit(property.Id, "3C", now);
        _ctx.Db.Units.AddRange(vacant, occupied, offline);
        await _ctx.Db.SaveChangesAsync();

        _ctx.Db.LeaseManagements.Add(new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = occupied.Id,
            RelationshipNumber = "LM-APPLICATION-PUBLIC-OCCUPIED",
            PossessionGivenAtUtc = now.AddDays(-1),
            PossessionAgreementExceptionReason = "Test fixture proves occupancy independently of legal status.",
            PossessionAgreementExceptionAuthorizedByUserId = ActorId,
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = ActorId,
            UpdatedAtUtc = now.AddDays(-1),
        });
        _ctx.Db.UnitOperationalPeriods.Add(new UnitOperationalPeriod
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = offline.Id,
            Type = UnitOperationalPeriodType.OutOfService,
            StartedAtUtc = now.AddDays(-1),
            Reason = "Offline public-application fixture",
            CreatedAtUtc = now.AddDays(-1),
            CreatedByUserId = ActorId,
        });
        await _ctx.Db.SaveChangesAsync();

        _commands.Clear();
        var info = await _sut.GetPublicFormInfoAsync(Token);

        info.Should().NotBeNull();
        info!.Properties.Select(propertyOption => propertyOption.Name)
            .Should().Equal("Aspen House", "Maple Grove");
        info.Properties.Single(propertyOption => propertyOption.Id == emptyProperty.Id).Units
            .Should().BeEmpty();

        var units = info.Properties.Single(propertyOption => propertyOption.Id == property.Id).Units;
        units.Select(u => u.UnitNumber).Should().Equal("1A", "2B");
        units.Single(u => u.UnitNumber == "1A").Status.Should().Be(DerivedUnitStatus.Vacant);
        units.Single(u => u.UnitNumber == "2B").Status.Should().Be(DerivedUnitStatus.Occupied);

        _commands.Should().HaveCountLessThanOrEqualTo(3);
        _commands.Should().Contain(sql =>
            sql.Contains("\"Units\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("vw_unit_occupancy", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"IsOutOfService\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("jsonb_agg", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FILTER", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    private static Unit Unit(int propertyId, string unitNumber, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        PropertyId = propertyId,
        UnitNumber = unitNumber,
        CreatedAt = now,
        UpdatedAt = now,
    };

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
