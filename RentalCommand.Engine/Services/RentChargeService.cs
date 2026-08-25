using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Data;
using RentalCommand.Data.Payments;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Posts agreement-backed rent charges to the continuous tenant account. Candidate generation,
/// proration, ordering, paging, and duplicate suppression are performed set-wise by PostgreSQL.
/// </summary>
public sealed class RentChargeService : IRentChargeService
{
    private const int BatchSize = 200;

    private readonly IWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RentChargeService> _logger;

    public RentChargeService(
        IWriteExecutor writes,
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        ILogger<RentChargeService> logger)
    {
        _writes = writes;
        _db = db;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        var runToken = Guid.NewGuid();
        try
        {
            var command = new ApplyScheduledRentChargeBatchCommand(
                runToken,
                _timeProvider.GetUtcNow().UtcDateTime,
                BatchSize);
            var handler = new ApplyScheduledRentChargeBatchRule(_db);
            var outcome = await _writes.ExecuteAsync(
                runToken.ToString("N"),
                TenantMoneyWriteSupport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync),
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
