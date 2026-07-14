using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Time;
using RentalCommand.Data.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Claims due recurring-maintenance schedules, then atomically creates their work orders and advances
/// every claimed schedule. A failed batch leaves its claim leases intact for a safe later retry.
/// </summary>
public sealed class RecurringMaintenanceService : IRecurringMaintenanceService
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> ResultCodec =
        new("scheduled-automation.recurring-maintenance.apply.v1");
    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<RecurringMaintenanceService> _logger;

    public RecurringMaintenanceService(
        IAtomicUnitOfWork atomic,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<RecurringMaintenanceService> logger)
    {
        _atomic = atomic;
        _timeProvider = timeProvider;
        _tz = tz;
        _claims = claims;
        _logger = logger;
    }

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var businessTimeZone = _tz.BusinessTimeZone;
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(now, businessTimeZone).Date,
            DateTimeKind.Utc);
        var claims = await _claims.ClaimRecurringMaintenanceAsync(
            $"{Environment.MachineName}:{Environment.ProcessId}:recurring-maintenance",
            today,
            TimeSpan.FromMinutes(6),
            25,
            ct);
        if (claims.Count == 0) return 0;

        try
        {
            var token = RequireSingleToken(claims);
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "scheduled-automation.recurring-maintenance.apply",
                    token.ToString("N")),
                new ApplyClaimedRecurringMaintenanceBatchCommand(
                    claims.Select(claim => claim.Id).ToArray(),
                    token,
                    today,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    businessTimeZone.Id),
                ResultCodec,
                ct);
            if (outcome.Value.GeneratedRowCount > 0)
            {
                _logger.LogInformation(
                    "RecurringMaintenanceService created {Count} work order(s) from recurring maintenance schedules.",
                    outcome.Value.GeneratedRowCount);
            }
            return outcome.Value.GeneratedRowCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recurring-maintenance atomic batch failed; claims remain leased for safe retry");
            return 0;
        }
    }

    private static Guid RequireSingleToken(IReadOnlyList<ScheduledAutomationClaim> claims)
    {
        var token = claims[0].ClaimToken;
        if (claims.Any(claim => claim.ClaimToken != token))
            throw new InvalidOperationException("A recurring-maintenance claim batch must share one token.");
        return token;
    }
}
