using Microsoft.Extensions.Logging;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Automation;

namespace RentalCommand.Engine.Services;

public sealed class DebtServiceService : IDebtServiceService
{
    private readonly IWriteExecutor _writes;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IAppTimeZoneProvider _tz;
    private readonly IScheduledAutomationClaimStore _claims;
    private readonly ILogger<DebtServiceService> _logger;

    public DebtServiceService(
        IWriteExecutor writes,
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IAppTimeZoneProvider tz,
        IScheduledAutomationClaimStore claims,
        ILogger<DebtServiceService> logger)
    {
        _writes = writes;
        _db = db;
        _timeProvider = timeProvider;
        _tz = tz;
        _claims = claims;
        _logger = logger;
    }

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var today = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(now, _tz.BusinessTimeZone).Date,
            DateTimeKind.Utc);
        var claims = await _claims.ClaimDebtServiceAsync(
            $"{Environment.MachineName}:{Environment.ProcessId}:debt-service",
            today,
            TimeSpan.FromMinutes(3),
            25,
            ct);
        if (claims.Count == 0) return 0;

        try
        {
            var token = RequireSingleToken(claims);
            var command = new ApplyClaimedDebtServiceBatchCommand(
                    claims.Select(claim => claim.Id).ToArray(),
                    token,
                    today,
                    _timeProvider.GetUtcNow().UtcDateTime);
            var handler = new ApplyClaimedDebtServiceBatchRule(_db);
            var outcome = await _writes.ExecuteAsync(
                token.ToString("N"),
                ScheduledFinanceWriteSupport.Write(
                    command, handler.ExecuteAsync, handler.AuthorizeAsync),
                ct);
            if (outcome.Value.GeneratedRowCount > 0)
            {
                _logger.LogInformation(
                    "DebtServiceService created {Count} loan payment(s)",
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
            _logger.LogError(ex, "Debt-service atomic batch failed; claims remain leased for safe retry");
            return 0;
        }
    }

    private static Guid RequireSingleToken(IReadOnlyList<ScheduledAutomationClaim> claims)
    {
        var token = claims[0].ClaimToken;
        if (claims.Any(claim => claim.ClaimToken != token))
            throw new InvalidOperationException("A debt-service claim batch must share one token.");
        return token;
    }
}
