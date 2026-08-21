using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Configuration;
using RentalCommand.Data;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Posts late-fee debits from canonical open rent-charge balances. It never mutates a rent row or
/// writes a legacy Payment status; PostgreSQL derives delinquency from the append-only ledger.
/// </summary>
public sealed class LateFeeService : ILateFeeService
{
    private const int BatchSize = 200;

    private readonly IJobStepWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationsConfig _defaults;
    private readonly ILogger<LateFeeService> _logger;

    public LateFeeService(
        IJobStepWriteExecutor writes,
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IOptions<NotificationsConfig> options,
        ILogger<LateFeeService> logger)
    {
        _writes = writes;
        _db = db;
        _timeProvider = timeProvider;
        _defaults = options.Value;
        _logger = logger;
    }

    public async Task<int> AssessAsync(CancellationToken ct = default)
    {
        var runToken = Guid.NewGuid();
        var capsJson = JsonSerializer.Serialize(_defaults.StateLateFeeCaps.Select(pair => new
        {
            State = pair.Key,
            pair.Value.MaxFlat,
            pair.Value.MaxPercentOfRent,
        }));

        try
        {
            var command = new ApplyScheduledLateFeeChargeBatchCommand(
                runToken,
                _timeProvider.GetUtcNow().UtcDateTime,
                BatchSize,
                capsJson);
            var handler = new ApplyScheduledLateFeeChargeBatchHandler(_db);
            var outcome = await _writes.ExecuteAsync(
                runToken.ToString("N"),
                TenantMoneyWriteSupport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync),
                ct);

            _logger.LogInformation(
                "LateFeeService posted {Count} canonical tenant-account late-fee charge(s)",
                outcome.Value.LateFeeChargeCount);
            return outcome.Value.LateFeeChargeCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Canonical late-fee atomic batch {RunToken} failed; deterministic business keys make retry safe",
                runToken);
            return 0;
        }
    }
}
