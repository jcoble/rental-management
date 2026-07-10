using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class NativeEsignReconciliationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly StubExecutionService _execution = new();

    public NativeEsignReconciliationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_connection)
            .Options;
        var services = new ServiceCollection();
        services.AddScoped<RentalCommandDbContext>(_ => new ReconciliationTestDbContext(options));
        services.AddSingleton<INativeEsignExecutionService>(_execution);
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        db.Database.EnsureCreated();
        SeedGraph(db);
    }

    [Fact]
    public async Task ReconcileAsync_SelectsOldestPendingBatch_ServerSide()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var originalFileId = await db.StoredFiles.Select(file => file.Id).SingleAsync();
            var leaseId = await db.Leases.Select(lease => lease.Id).SingleAsync();
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < NativeEsignReconciliationService.BatchSize + 2; index++)
            {
                db.SignatureRequests.Add(NewRequest(
                    leaseId,
                    originalFileId,
                    SignatureRequestStatus.ExecutionPending,
                    start.AddMinutes(index)));
            }

            db.SignatureRequests.Add(NewRequest(
                leaseId,
                originalFileId,
                SignatureRequestStatus.Sent,
                start.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        var service = new NativeEsignReconciliationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NativeEsignReconciliationService>.Instance);

        var completed = await service.ReconcileAsync();

        completed.Should().Be(NativeEsignReconciliationService.BatchSize);
        _execution.RequestIds.Should().HaveCount(NativeEsignReconciliationService.BatchSize);
        _execution.RequestIds.Should().BeInAscendingOrder();
        await using var assertionScope = _provider.CreateAsyncScope();
        var assertionDb = assertionScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var expected = await assertionDb.SignatureRequests.AsNoTracking()
            .Where(request => request.Status == SignatureRequestStatus.ExecutionPending)
            .OrderBy(request => request.CreatedAtUtc)
            .ThenBy(request => request.Id)
            .Select(request => request.Id)
            .Take(NativeEsignReconciliationService.BatchSize)
            .ToArrayAsync();
        _execution.RequestIds.Should().Equal(expected);
    }

    [Fact]
    public void PendingRequestIdsQuery_TranslatesFilteringOrderingAndPagingToOneSqlStatement()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();

        var sql = NativeEsignReconciliationService.PendingRequestIdsQuery(db).ToQueryString();

        sql.Should().Contain("WHERE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain(".param set");
        sql.Should().Contain(NativeEsignReconciliationService.BatchSize.ToString());
    }

    [Fact]
    public async Task ReconcileAsync_OneFailure_DoesNotBlockRemainingRequests()
    {
        int firstId;
        int secondId;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var originalFileId = await db.StoredFiles.Select(file => file.Id).SingleAsync();
            var leaseId = await db.Leases.Select(lease => lease.Id).SingleAsync();
            var first = NewRequest(leaseId, originalFileId, SignatureRequestStatus.ExecutionPending, DateTime.UtcNow);
            var second = NewRequest(leaseId, originalFileId, SignatureRequestStatus.ExecutionPending, DateTime.UtcNow.AddMinutes(1));
            db.AddRange(first, second);
            await db.SaveChangesAsync();
            firstId = first.Id;
            secondId = second.Id;
        }
        _execution.ThrowForId = firstId;
        var service = new NativeEsignReconciliationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NativeEsignReconciliationService>.Instance);

        var completed = await service.ReconcileAsync();

        completed.Should().Be(1);
        _execution.RequestIds.Should().Equal(firstId, secondId);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private static SignatureRequest NewRequest(
        int leaseId,
        int originalFileId,
        SignatureRequestStatus status,
        DateTime createdAtUtc) =>
        new()
        {
            PortfolioId = 1,
            PublicId = Guid.NewGuid().ToString("N"),
            LeaseId = leaseId,
            DocumentName = "lease.pdf",
            OriginalStoredFileId = originalFileId,
            Status = status,
            CreatedAtUtc = createdAtUtc,
        };

    private static void SeedGraph(RentalCommandDbContext db)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Id = 1,
            Name = "Test",
            ManagementCompanyName = "Test",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = "Test",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            Portfolio = portfolio,
            FirstName = "Test",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Leases.Add(new Lease
        {
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "TEST-1",
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.StoredFiles.Add(new StoredFile
        {
            Portfolio = portfolio,
            FileName = "original.pdf",
            FilePath = "original.pdf",
            ContentType = "application/pdf",
            UploadedAt = now,
        });
        db.SaveChanges();
    }

    private sealed class StubExecutionService : INativeEsignExecutionService
    {
        public List<int> RequestIds { get; } = [];
        public int? ThrowForId { get; set; }

        public Task<bool> FinalizePendingAsync(int signatureRequestId, CancellationToken ct = default)
        {
            RequestIds.Add(signatureRequestId);
            if (signatureRequestId == ThrowForId)
            {
                throw new InvalidOperationException("Injected failure");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class ReconciliationTestDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommandDbContext(options);
}
