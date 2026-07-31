using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Posts agreement-backed rent charges to the continuous tenant account. Candidate generation,
/// proration, ordering, paging, and duplicate suppression are performed set-wise by PostgreSQL.
/// </summary>
public sealed class RentChargeService : IRentChargeService
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledRentChargeBatchResult> ResultCodec =
        new("scheduled-tenant-charges.rent.apply.v1");
    private const int BatchSize = 200;

    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentChargeService> _logger;

    public RentChargeService(
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        ILogger<RentChargeService> logger)
    {
        _atomic = atomic;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        var runToken = Guid.NewGuid();
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "scheduled-tenant-charges.rent.apply",
                    runToken.ToString("N")),
                new ApplyScheduledRentChargeBatchCommand(
                    runToken,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    BatchSize),
                ResultCodec,
                ct);

            _logger.LogInformation(
                "RentChargeService posted {Count} canonical tenant-account rent charge(s)",
                outcome.Value.RentChargeCount);
            return outcome.Value.RentChargeCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Canonical scheduled-rent atomic batch {RunToken} failed; deterministic business keys make retry safe",
                runToken);
            throw;
        }
    }
}
