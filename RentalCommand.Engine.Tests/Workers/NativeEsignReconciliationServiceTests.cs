using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Data.Esign;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class NativeEsignReconciliationServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly StubExecutionService _execution = new();
    private readonly StubClaimStore _claims = new();

    public NativeEsignReconciliationServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INativeEsignExecutionService>(_execution);
        services.AddSingleton<INativeEsignExecutionClaimStore>(_claims);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReconcileAsync_ClaimsBoundedBatchAndFinalizesEachRequest()
    {
        _claims.Claims = Enumerable.Range(1, NativeEsignReconciliationService.BatchSize + 2)
            .Select(id => new NativeEsignExecutionClaim(id, Guid.NewGuid(), Guid.NewGuid()))
            .ToArray();
        var service = NewService();

        var completed = await service.ReconcileAsync();

        completed.Should().Be(NativeEsignReconciliationService.BatchSize);
        _execution.RequestIds.Should().Equal(
            Enumerable.Range(1, NativeEsignReconciliationService.BatchSize));
        _claims.LastBatchSize.Should().Be(NativeEsignReconciliationService.BatchSize);
        _claims.LastLeaseDuration.Should().Be(NativeEsignReconciliationService.ClaimLease);
    }

    [Fact]
    public void ClaimSql_FiltersOrdersPagesLocksAndLeasesInOneStatement()
    {
        var sql = (string)typeof(NativeEsignExecutionClaimStore)
            .GetField("BatchClaimSql", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetRawConstantValue()!;

        sql.Should().Contain("INNER JOIN \"LeaseAgreements\"");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("ORDER BY request.\"PreparedAtUtc\", request.\"Id\"");
        sql.Should().Contain("LIMIT");
        sql.Should().Contain("FOR UPDATE OF request SKIP LOCKED");
        sql.Should().Contain("pg_try_advisory_xact_lock");
        sql.Should().Contain("UPDATE \"SignatureRequests\"");
        sql.Should().Contain("RETURNING");
        sql.Should().Contain("clock AS MATERIALIZED");
        sql.Should().Contain("clock_timestamp()");
        sql.Should().Contain("gen_random_uuid()");
        sql.Should().Contain("@leaseDuration");
        sql.Should().NotContain("\"Leases\"");
        sql.Should().NotContain("@now");
    }

    [Fact]
    public async Task ReconcileAsync_OneFailure_DoesNotBlockRemainingRequests()
    {
        _execution.ThrowForId = 1;
        _claims.Claims =
        [
            new NativeEsignExecutionClaim(1, Guid.NewGuid(), Guid.NewGuid()),
            new NativeEsignExecutionClaim(2, Guid.NewGuid(), Guid.NewGuid()),
        ];
        var service = NewService();

        var completed = await service.ReconcileAsync();

        completed.Should().Be(1);
        _execution.RequestIds.Should().Equal(1, 2);
    }

    public void Dispose() => _provider.Dispose();

    private NativeEsignReconciliationService NewService() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<NativeEsignReconciliationService>.Instance);

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
            string claimOwner, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default)
        {
            LastBatchSize = batchSize;
            LastLeaseDuration = leaseDuration;
            return Task.FromResult<IReadOnlyList<NativeEsignExecutionClaim>>(
                Claims.Take(batchSize).ToArray());
        }

        public Task<NativeEsignExecutionClaim?> TryClaimAsync(
            int signatureRequestId, string claimOwner, TimeSpan leaseDuration,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> ReleaseForRetryAsync(
            int signatureRequestId, Guid claimToken, string? error,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
