using Microsoft.Extensions.Logging;
using RentalCommand.Core.Automation;
using RentalCommand.Data;
using RentalCommand.Data.Payments;
using RentalCommand.Engine.Writes;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Hourly engine façade for recurring tenant-charge occurrences. The atomic command owns the
/// schedule, tenant entry, journal, audit, and outbox transaction.
/// </summary>
public sealed class RecurringTenantChargeGenerationService
    : IRecurringTenantChargeGenerationService
{
    private const int BatchSize = 200;

    private readonly IJobStepWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecurringTenantChargeGenerationService> _logger;

    public RecurringTenantChargeGenerationService(
        IJobStepWriteExecutor writes,
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        ILogger<RecurringTenantChargeGenerationService> logger)
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
            var command = new ApplyRecurringTenantChargeBatchCommand(
                    runToken,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    BatchSize);
            var handler = new ApplyRecurringTenantChargeBatchRule(_db);
            var outcome = await _writes.ExecuteAsync(
                runToken.ToString("N"),
                TenantMoneyWriteSupport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync),
                ct);

            _logger.LogInformation(
                "RecurringTenantChargeGenerationService posted {Count} tenant-charge occurrence(s)",
                outcome.Value.ChargeCount);
            return outcome.Value.ChargeCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recurring tenant-charge atomic batch {RunToken} failed; deterministic occurrence keys make retry safe",
                runToken);
            throw;
        }
    }
}
