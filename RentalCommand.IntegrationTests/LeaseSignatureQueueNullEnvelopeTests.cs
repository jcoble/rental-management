using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Reproduces BUG-2: <c>GET /api/v1/leases/{id}/signature-queue</c> returned HTTP 500 for every
/// lease that was never sent for e-signature (the common case). The queue's raw Npgsql query
/// interpolated a C# <c>null</c> into <c>AND ({param} IS NULL OR …)</c>; with no current signature
/// request the parameter is a typeless NULL and Postgres throws "could not determine data type of
/// parameter". This needs a real Postgres to prove — SQLite (the unit-test surface) runs the LINQ
/// branch and never hits the raw SQL. Before the fix this test throws; after it returns an empty
/// queue (and, when present, the lease's signing emails) without filtering by signature request.
/// </summary>
public sealed class LeaseSignatureQueueNullEnvelopeTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;
    private int _portfolioId;
    private int _noEnvelopeLeaseId;
    private int _emptyLeaseId;

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

        _ownerConnString = _pg.GetConnectionString();

        await using var ctx = NewContext(_ownerConnString);
        await ctx.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = "Esign Queue Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;

        // A lease that was never sent for e-signature → EsignEnvelopeId stays null (the 500 case),
        // and a second one to confirm a no-message lease returns an empty queue rather than throwing.
        _noEnvelopeLeaseId = (await SeedLeaseWithGraphAsync(ctx, "L-NOENV", now)).Id;
        _emptyLeaseId = (await SeedLeaseWithGraphAsync(ctx, "L-EMPTY", now)).Id;

        // One signing-link email for the first lease. Its signatureRequestId must NOT be used to
        // filter when the lease has no current envelope — the post-fix query returns it regardless.
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            PortfolioId = _portfolioId,
            MessageType = "email",
            Payload = JsonSerializer.Serialize(new
            {
                source = OutboxPayloadSources.LeaseEsignSigningLink,
                signatureRequestId = 999,
                leaseId = _noEnvelopeLeaseId,
                to = "tenant@example.com",
                subject = "Lease L-NOENV",
            }),
            CreatedAt = now.AddMinutes(-2),
            SentAt = now.AddMinutes(-1),
        });
        await ctx.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task GetSignatureQueue_LeaseWithNullEnvelope_ReturnsQueueInsteadOfThrowing()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; Postgres signature-queue null-envelope verification skipped.");

        await using var db = NewContext(_ownerConnString);
        var sut = CreateService(db);

        // The lease has no envelope → currentSignatureRequestId is null → before the fix the raw
        // query bound a typeless NULL and Postgres 500'd. It must now return the lease's signing email.
        var queue = await sut.GetSignatureQueueAsync(_portfolioId, _noEnvelopeLeaseId);

        queue.Should().NotBeNull();
        queue!.LeaseId.Should().Be(_noEnvelopeLeaseId);
        queue.Items.Should().ContainSingle("the lease's signing email returns even with no current envelope");
        queue.Items[0].RecipientEmail.Should().Be("tenant@example.com");

        // A no-envelope lease with no signing emails returns an empty queue (200), not a 500.
        var emptyQueue = await sut.GetSignatureQueueAsync(_portfolioId, _emptyLeaseId);
        emptyQueue.Should().NotBeNull();
        emptyQueue!.LeaseId.Should().Be(_emptyLeaseId);
        emptyQueue.Items.Should().BeEmpty();
    }

    private static async Task<Lease> SeedLeaseWithGraphAsync(RentalCommandDbContext ctx, string leaseNumber, DateTime now)
    {
        var portfolioId = await ctx.Portfolios.Select(p => p.Id).FirstAsync();

        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Properties.Add(property);
        await ctx.SaveChangesAsync();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "2B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = portfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            Email = "tenant@example.com",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Tenants.Add(tenant);
        await ctx.SaveChangesAsync();

        var lease = new Lease
        {
            PortfolioId = portfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = leaseNumber,
            Status = LeaseStatus.Draft,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1450m,
            SecurityDeposit = 1450m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Leases.Add(lease);
        await ctx.SaveChangesAsync();
        return lease;
    }

    private static LeaseEsignService CreateService(RentalCommandDbContext db)
    {
        var storage = new NoopFileStorage();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WebBaseUrl"] = "https://localhost:5667" })
            .Build();

        var leaseService = new LeaseService(
            db,
            new NoopDataUpdateService(),
            storage,
            new LeaseAgreementPdfGenerator(),
            new AuditTrailService(db, new RentalCommand.Data.Auditing.AuditScope(), TimeProvider.System),
            NullLogger<LeaseService>.Instance,
            TimeProvider.System,
            new LeaseAgreementRenderer(
                db,
                storage,
                new LeaseAgreementPdfGenerator(),
                NullLogger<LeaseAgreementRenderer>.Instance));

        return new LeaseEsignService(
            db,
            leaseService,
            new NoopEsignProvider(),
            storage,
            new NoopDataUpdateService(),
            new AuditTrailService(db, new RentalCommand.Data.Auditing.AuditScope(), TimeProvider.System),
            config,
            TimeProvider.System,
            NullLogger<LeaseEsignService>.Instance);
    }

    private static RentalCommandDbContext NewContext(string connString) =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().UseNpgsql(connString).Options);

    /// <summary>The signature-queue read never sends, so the provider is an unused stub.</summary>
    private sealed class NoopEsignProvider : IEsignProvider
    {
        public bool IsConfigured => false;

        public Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
            => throw new NotSupportedException();
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
