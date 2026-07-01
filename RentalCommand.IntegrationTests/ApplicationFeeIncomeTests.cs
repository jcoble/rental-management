using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// gap5 (application/screening-fee income) end-to-end against a real Postgres: recording a fee against a
/// rental application — with NO lease — creates a Paid <see cref="PaymentType.ApplicationFee"/> payment
/// attributed to the application's property, and that income surfaces on Schedule E and in the
/// <c>vw_accounting_transactions</c> ledger view (which maps 5 → 'ApplicationFee' and COALESCEs the
/// payment's own PropertyId). Also proves the "exactly one of lease / application" create-time guard.
///
/// <para>Requires Docker (same Testcontainers pattern as <see cref="AccountingTransactionsViewTests"/>);
/// tests report as skipped when the daemon is unavailable.</para>
/// </summary>
public sealed class ApplicationFeeIncomeTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _conn = string.Empty;
    private int _portfolioId;
    private int _propertyId;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _conn = _pg.GetConnectionString();
        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();

        var portfolio = new Portfolio
        {
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = "Maple Street",
            AddressLine1 = "1 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Properties.Add(property);
        await ctx.SaveChangesAsync();
        _propertyId = property.Id;
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    // ── (a) Create path: an application fee is a lease-less, property-attributed, ApplicationFee payment ──
    [SkippableFact]
    public async Task CreateAsync_ApplicationFee_IsLeaseless_TypedAndAttributedToApplicationProperty()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var appId = await SeedApplicationAsync(db, _propertyId);

        // No PropertyType/PropertyId supplied — both must be defaulted from the application.
        var created = await NewPaymentService(db).CreateAsync(_portfolioId, new CreatePaymentRequest
        {
            ApplicationId = appId,
            Status = PaymentStatus.Paid,
            Amount = 50m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        created.Should().NotBeNull();
        created!.LeaseId.Should().BeNull("an application fee has no lease");
        created.ApplicationId.Should().Be(appId);
        created.PaymentType.Should().Be(PaymentType.ApplicationFee, "an application-linked payment defaults to ApplicationFee");
        created.PropertyId.Should().Be(_propertyId, "the fee attributes to the application's property by default");

        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        stored.LeaseId.Should().BeNull();
        stored.PropertyId.Should().Be(_propertyId);
        stored.PaymentType.Should().Be(PaymentType.ApplicationFee);
    }

    // ── (b) Create guard: exactly one of lease / application is required ──
    [SkippableFact]
    public async Task CreateAsync_WithNeitherLeaseNorApplication_ThrowsValidation()
    {
        SkipIfNoDocker();
        await using var db = NewContext();

        var act = async () => await NewPaymentService(db).CreateAsync(_portfolioId, new CreatePaymentRequest
        {
            Status = PaymentStatus.Paid,
            Amount = 50m,
            DueDate = new DateTime(2026, 03, 01, 0, 0, 0, DateTimeKind.Utc),
        });

        await act.Should().ThrowAsync<DomainValidationException>();
    }

    // ── (c) Schedule E counts the application fee as income for its property ──
    [SkippableFact]
    public async Task ScheduleE_CountsApplicationFeeAsIncome_ForItsProperty()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var appId = await SeedApplicationAsync(db, _propertyId);

        await NewPaymentService(db).CreateAsync(_portfolioId, new CreatePaymentRequest
        {
            ApplicationId = appId,
            Status = PaymentStatus.Paid,
            Amount = 50m,
            DueDate = new DateTime(2026, 03, 10, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 03, 10, 0, 0, 0, DateTimeKind.Utc),
        });

        var report = await new ScheduleEService(db).GetReportAsync(_portfolioId, 2026);

        report.TotalRentalIncome.Should().Be(50m, "the application fee is taxable income (SA-STATE §109)");
        report.Properties.Should().ContainSingle(p => p.PropertyId == _propertyId)
            .Which.RentalIncome.Should().Be(50m, "the fee lands on the application's property, not Unassigned");
        report.Properties.Should().NotContain(p => p.PropertyName == "Unassigned",
            "a fee carrying a PropertyId is attributed to that property, not the Unassigned bucket");
    }

    // ── (d) The ledger view maps 5 → 'ApplicationFee' and shows the property via COALESCE ──
    [SkippableFact]
    public async Task TransactionsView_ShowsApplicationFee_WithProperty()
    {
        SkipIfNoDocker();
        await using var db = NewContext();
        var appId = await SeedApplicationAsync(db, _propertyId);

        var created = await NewPaymentService(db).CreateAsync(_portfolioId, new CreatePaymentRequest
        {
            ApplicationId = appId,
            Status = PaymentStatus.Paid,
            Amount = 50m,
            DueDate = new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc),
            PaidDate = new DateTime(2026, 03, 12, 0, 0, 0, DateTimeKind.Utc),
        });

        var accounting = new AccountingService(db, new ScheduleEService(db), new YearEndPacketPdfGenerator(), TimeProvider.System);
        var page = await accounting.GetTransactionsAsync(
            _portfolioId, new AccountingTransactionsQuery { Kind = "Payment", Take = 20 }, CancellationToken.None);

        var row = page.Items.Single(t => t.Kind == "Payment" && t.Id == created!.Id);
        row.Category.Should().Be("ApplicationFee", "the view CASE maps PaymentType 5 → 'ApplicationFee'");
        row.Amount.Should().Be(50m);
        row.PropertyId.Should().Be(_propertyId, "the view COALESCEs the payment's own PropertyId when there is no lease");
        row.PropertyName.Should().Be("Maple Street");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; application-fee income verification skipped.");

    private RentalCommandDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(_conn).Options);

    private static PaymentService NewPaymentService(RentalCommandDbContext db) =>
        new(
            db,
            new NoopDataUpdateService(),
            new AuditTrailService(db, new AuditScope(), TimeProvider.System),
            new NoopFileStorage(),
            TimeProvider.System);

    private async Task<int> SeedApplicationAsync(RentalCommandDbContext db, int propertyId)
    {
        var app = new RentalApplication
        {
            PortfolioId = _portfolioId,
            PropertyId = propertyId,
            FirstName = "Applicant",
            LastName = "One",
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.RentalApplications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
            => Task.FromResult($"noop/{fileName}");

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string path, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
