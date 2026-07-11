using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Esign;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class NativeEsignReconciliationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly StubExecutionService _execution = new();
    private readonly StubClaimStore _claims = new();

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
        services.AddSingleton<INativeEsignExecutionClaimStore>(_claims);
        services.AddSingleton(TimeProvider.System);
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

        await using (var claimScope = _provider.CreateAsyncScope())
        {
            var claimDb = claimScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            var ids = await claimDb.SignatureRequests.AsNoTracking()
                .Where(request => request.Status == SignatureRequestStatus.ExecutionPending)
                .OrderBy(request => request.CreatedAtUtc)
                .ThenBy(request => request.Id)
                .Select(request => request.Id)
                .Take(NativeEsignReconciliationService.BatchSize)
                .ToArrayAsync();
            _claims.Claims = ids.Select(id => new NativeEsignExecutionClaim(
                id, $"request-{id}", Guid.NewGuid())).ToArray();
        }

        var service = new NativeEsignReconciliationService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NativeEsignReconciliationService>.Instance);

        var completed = await service.ReconcileAsync();

        completed.Should().Be(NativeEsignReconciliationService.BatchSize);
        _execution.RequestIds.Should().HaveCount(NativeEsignReconciliationService.BatchSize);
        _execution.RequestIds.Should().BeInAscendingOrder();
        _execution.RequestIds.Should().Equal(_claims.Claims.Select(claim => claim.Id));
        _claims.LastBatchSize.Should().Be(NativeEsignReconciliationService.BatchSize);
        _claims.LastLeaseDuration.Should().Be(NativeEsignReconciliationService.ClaimLease);
    }

    [Fact]
    public void ClaimSql_FiltersOrdersPagesLocksAndLeasesInOneStatement()
    {
        var sql = (string)typeof(NativeEsignExecutionClaimStore)
            .GetField("BatchClaimSql", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        sql.Should().Contain("WHERE");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("FOR UPDATE OF request SKIP LOCKED");
        sql.Should().Contain("pg_try_advisory_xact_lock");
        sql.Should().Contain("UPDATE \"SignatureRequests\"");
        sql.Should().Contain("RETURNING");
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
        _claims.Claims =
        [
            new NativeEsignExecutionClaim(firstId, "first", Guid.NewGuid()),
            new NativeEsignExecutionClaim(secondId, "second", Guid.NewGuid()),
        ];
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

        public Task<bool> FinalizePendingAsync(int signatureRequestId, CancellationToken ct = default) =>
            FinalizeClaimedAsync(signatureRequestId, Guid.Empty, ct);

        public Task<bool> FinalizeClaimedAsync(
            int signatureRequestId, Guid claimToken, CancellationToken ct = default)
        {
            RequestIds.Add(signatureRequestId);
            if (signatureRequestId == ThrowForId)
            {
                throw new InvalidOperationException("Injected failure");
            }

            return Task.FromResult(true);
        }
    }

    private sealed class StubClaimStore : INativeEsignExecutionClaimStore
    {
        public IReadOnlyList<NativeEsignExecutionClaim> Claims { get; set; } = [];
        public int LastBatchSize { get; private set; }
        public TimeSpan LastLeaseDuration { get; private set; }

        public Task<IReadOnlyList<NativeEsignExecutionClaim>> ClaimBatchAsync(
            string claimOwner, DateTime nowUtc, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default)
        {
            LastBatchSize = batchSize;
            LastLeaseDuration = leaseDuration;
            return Task.FromResult(Claims);
        }

        public Task<NativeEsignExecutionClaim?> TryClaimAsync(
            int signatureRequestId, string claimOwner, DateTime nowUtc, TimeSpan leaseDuration,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> ReleaseForRetryAsync(
            int signatureRequestId, Guid claimToken, string? error,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ReconciliationTestDbContext(DbContextOptions<RentalCommandDbContext> options)
        : RentalCommandDbContext(options);
}
