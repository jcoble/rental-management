using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Time;
using RentalCommand.Data.Automation;

namespace RentalCommand.Engine.Services;

/// <summary>
/// Claims due recurring-maintenance schedules, then applies each schedule in its own atomic scope.
/// A failed schedule is recorded and released without blocking the rest of the claimed batch.
/// </summary>
public sealed class RecurringMaintenanceService : IRecurringMaintenanceService
{
    private static readonly AtomicJsonResultCodec<ApplyScheduledFinanceBatchResult> ResultCodec =
        new("scheduled-automation.recurring-maintenance.apply.v1");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<RecurringMaintenanceService> _logger;

    public RecurringMaintenanceService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<RecurringMaintenanceService> logger)
    {
        _scopeFactory = scopeFactory;
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

        var generated = 0;
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            generated += await ApplyClaimAsync(claim, today, now, businessTimeZone.Id, ct);
        }

        if (generated > 0)
            _logger.LogInformation(
                "RecurringMaintenanceService created {Count} work order(s) from recurring maintenance schedules.",
                generated);
        return generated;
    }

    private async Task<int> ApplyClaimAsync(
        ScheduledAutomationClaim claim,
        DateTime today,
        DateTime appliedAtUtc,
        string businessTimeZoneId,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var atomic = scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
            var outcome = await atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "scheduled-automation.recurring-maintenance.apply",
                    $"{claim.ClaimToken:N}:{claim.Id}"),
                new ApplyClaimedRecurringMaintenanceBatchCommand(
                    [claim.Id],
                    claim.ClaimToken,
                    today,
                    appliedAtUtc,
                    businessTimeZoneId),
                ResultCodec,
                ct);
            return outcome.Value.GeneratedRowCount;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var correlationId = BuildFailureCorrelationId();
            _logger.LogError(
                ex,
                "Recurring-maintenance task {RecurringMaintenanceTaskId} failed; isolating it from the remaining claimed schedules. CorrelationId: {FailureCorrelationId}.",
                claim.Id,
                correlationId);
            await TryRecordFailureAsync(claim, appliedAtUtc, ex, correlationId, ct);
            return 0;
        }
    }

    private async Task TryRecordFailureAsync(
        ScheduledAutomationClaim claim,
        DateTime failedAtUtc,
        Exception exception,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var claims = scope.ServiceProvider.GetRequiredService<IScheduledAutomationClaimStore>();
            var recorded = await claims.RecordRecurringMaintenanceFailureAsync(
                claim,
                failedAtUtc,
                BuildFailureReason(exception, correlationId),
                ScheduledAutomationPolicy.RecurringMaintenanceQuarantineAfterAttempts,
                ct);
            if (!recorded)
                _logger.LogWarning(
                    "Recurring-maintenance task {RecurringMaintenanceTaskId} failure could not be recorded because its claim was lost.",
                    claim.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception recordException)
        {
            // Failure recording is deliberately contained: it must not turn one bad row into a
            // batch-wide failure. The claim lease expiry remains the recovery path if this write fails.
            _logger.LogError(
                recordException,
                "Recurring-maintenance failure record could not be persisted for task {RecurringMaintenanceTaskId}; the claim will be recovered after lease expiry.",
                claim.Id);
        }
    }

    internal static string BuildFailureReason(Exception exception, string correlationId)
    {
        var root = exception;
        while (root.InnerException is not null)
            root = root.InnerException;

        var reason = root switch
        {
            InvalidOperationException invalidOperation
                when invalidOperation.Message.StartsWith(
                    "Unsupported recurring-maintenance interval",
                    StringComparison.Ordinal) =>
                "This recurring maintenance schedule uses an unsupported repeat interval. " +
                "Edit it and choose a supported interval.",
            ArgumentException argument
                when argument.Message.Contains("invalid time", StringComparison.OrdinalIgnoreCase) =>
                "The scheduled time falls during a daylight-saving time change. Pick a different time.",
            ScheduledFinanceClaimLostException =>
                "This recurring maintenance schedule changed while it was running. It will be retried automatically.",
            _ => BuildUnknownFailureReason(correlationId),
        };
        return reason.Length <= ScheduledAutomationPolicy.RecurringMaintenanceFailureReasonMaxLength
            ? reason
            : reason[..ScheduledAutomationPolicy.RecurringMaintenanceFailureReasonMaxLength];
    }

    private static string BuildUnknownFailureReason(string correlationId) =>
        $"Recurring maintenance could not be generated. Check the schedule and try again. " +
        $"Reference: {correlationId}";

    private static string BuildFailureCorrelationId() => $"RM-{Guid.NewGuid():N}";
}
