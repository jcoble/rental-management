using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Posts late-fee debits from canonical open rent-charge balances. It never mutates a rent row or
/// writes a legacy Payment status; PostgreSQL derives delinquency from the append-only ledger.
/// </summary>
public sealed class LateFeeService : ILateFeeService
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledTenantChargeBatchResult> ResultCodec =
        new("scheduled-tenant-charges.late-fee.apply.v1");
    private const int BatchSize = 200;

    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationsConfig _defaults;
    private readonly ILogger<LateFeeService> _logger;

    public LateFeeService(
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        IOptions<NotificationsConfig> options,
        ILogger<LateFeeService> logger)
    {
        _atomic = atomic;
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
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "scheduled-tenant-charges.late-fee.apply",
                    runToken.ToString("N")),
                new ApplyScheduledTenantChargeBatchCommand(
                    runToken,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    BatchSize,
                    IncludeRentCharges: false,
                    IncludeLateFeeCharges: true,
                    StateLateFeeCapsJson: capsJson),
                ResultCodec,
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
