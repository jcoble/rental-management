using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Esign;
using Testcontainers.PostgreSql;

namespace RentalCommand.IntegrationTests;

/// <summary>Real PostgreSQL proof for native e-sign execution leases and fencing.</summary>
public sealed class NativeEsignExecutionClaimStoreTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = string.Empty;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
        }
        catch
        {
            return;
        }

        _dockerAvailable = true;
        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task Claims_are_disjoint_expiry_reclaimable_and_completion_is_fenced()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        await SeedRequestsAsync(now, 4);

        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var batches = await Task.WhenAll(
            new NativeEsignExecutionClaimStore(dbA)
                .ClaimBatchAsync("engine-a", TimeSpan.FromMinutes(10), 2),
            new NativeEsignExecutionClaimStore(dbB)
                .ClaimBatchAsync("engine-b", TimeSpan.FromMinutes(10), 2));

        var claims = batches.SelectMany(batch => batch).ToArray();
        claims.Should().HaveCount(4);
        claims.Select(claim => claim.Id).Should().OnlyHaveUniqueItems();
        claims.Select(claim => claim.ClaimToken).Should().OnlyHaveUniqueItems();
        dbA.Database.CurrentTransaction.Should().BeNull("blob work must start after claim commit");
        dbB.Database.CurrentTransaction.Should().BeNull();

        await using (var cannotSteal = NewContext())
        {
            (await new NativeEsignExecutionClaimStore(cannotSteal)
                .ClaimBatchAsync("engine-c", TimeSpan.FromMinutes(10), 4))
                .Should().BeEmpty("unexpired execution leases cannot be stolen");
        }

        var stale = claims[0];
        await using (var expire = NewContext())
        {
            await expire.SignatureRequests.Where(request => request.Id == stale.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(request => request.ExecutionClaimExpiresAtUtc, now.AddSeconds(-1)));
        }

        NativeEsignExecutionClaim replacement;
        await using (var reclaim = NewContext())
        {
            replacement = (await new NativeEsignExecutionClaimStore(reclaim)
                .ClaimBatchAsync("replacement", TimeSpan.FromMinutes(10), 1)).Single();
        }
        replacement.Id.Should().Be(stale.Id);
        replacement.ClaimToken.Should().NotBe(stale.ClaimToken);

        await using var complete = NewContext();
        var store = new NativeEsignExecutionClaimStore(complete);
        (await store.ReleaseForRetryAsync(stale.Id, stale.ClaimToken, "stale"))
            .Should().Be(0, "an expired worker cannot clear a newer execution lease");
        (await store.ReleaseForRetryAsync(replacement.Id, replacement.ClaimToken, "retry"))
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task Claim_statement_excludes_requests_with_unsigned_signers()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable.");
        var now = DateTime.UtcNow;
        var ids = await SeedRequestsAsync(now, 2);
        await using (var unsigned = NewContext())
        {
            await unsigned.SignatureSigners
                .Where(signer => signer.SignatureRequestId == ids[1])
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(signer => signer.Status, SignatureSignerStatus.Pending));
        }

        await using var db = NewContext();
        var claims = await new NativeEsignExecutionClaimStore(db)
            .ClaimBatchAsync("engine", TimeSpan.FromMinutes(10), 10);
        claims.Should().ContainSingle();
        claims.Single().Id.Should().Be(ids[0]);
    }

    private async Task<int[]> SeedRequestsAsync(DateTime now, int count)
    {
        await using var db = NewContext();
        var portfolio = new Portfolio
        {
            Name = $"E-sign claims {Guid.NewGuid():N}",
            ManagementCompanyName = "Claims",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            Portfolio = portfolio,
            Name = "Claims property",
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
            LastName = "Signer",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            Portfolio = portfolio,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = $"CLAIM-{Guid.NewGuid():N}",
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var original = new StoredFile
        {
            Portfolio = portfolio,
            FileName = "original.pdf",
            FilePath = $"original/{Guid.NewGuid():N}.pdf",
            ContentType = "application/pdf",
            UploadedAt = now,
        };
        db.AddRange(lease, original);
        await db.SaveChangesAsync();

        var requests = Enumerable.Range(0, count).Select(index => new SignatureRequest
        {
            PortfolioId = portfolio.Id,
            PublicId = Guid.NewGuid().ToString("N"),
            LeaseId = lease.Id,
            DocumentName = "lease.pdf",
            OriginalStoredFileId = original.Id,
            Status = SignatureRequestStatus.ExecutionPending,
            CreatedAtUtc = now.AddMinutes(index),
            Signers =
            [
                new SignatureSigner
                {
                    Name = "Test Signer",
                    Email = "signer@example.test",
                    Token = Guid.NewGuid().ToString("N"),
                    ExpiresAtUtc = now.AddDays(1),
                    Status = SignatureSignerStatus.Signed,
                    SignatureType = SignatureSignatureType.Typed,
                    TypedName = "Test Signer",
                    ConsentGiven = true,
                    SignedAtUtc = now,
                },
            ],
        }).ToArray();
        db.SignatureRequests.AddRange(requests);
        await db.SaveChangesAsync();
        return requests.Select(request => request.Id).ToArray();
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }
}
