using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Hourly engine façade for recurring tenant-charge occurrences. The atomic command owns the
/// schedule, tenant entry, journal, audit, and outbox transaction.
/// </summary>
public sealed class RecurringTenantChargeGenerationService
    : IRecurringTenantChargeGenerationService
{
    private static readonly AtomicJsonResultCodec<ApplyRecurringTenantChargeBatchResult> ResultCodec =
        new("scheduled-finance.recurring-tenant-charge.apply.v1");
    private const int BatchSize = 200;

    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecurringTenantChargeGenerationService> _logger;

    public RecurringTenantChargeGenerationService(
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        ILogger<RecurringTenantChargeGenerationService> logger)
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
                    "scheduled-finance.recurring-tenant-charge.apply",
                    runToken.ToString("N")),
                new ApplyRecurringTenantChargeBatchCommand(
                    runToken,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    BatchSize),
                ResultCodec,
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
