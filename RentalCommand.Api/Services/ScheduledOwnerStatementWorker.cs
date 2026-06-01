using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Data;

namespace RentalCommand.Api.Services;

/// <summary>
/// API-hosted background service that emails monthly owner statements.
/// Polls once per hour; fires when ALL conditions are true:
///   1. <c>Reports:EmailOwnerStatementsMonthly</c> is <c>true</c>.
///   2. Today's UTC day-of-month == <c>Reports:StatementDayOfMonth</c>.
///   3. It has not already run today (guarded by an in-memory date tracker).
///
/// DEFAULT OFF — nothing sends unless the feature flag is explicitly enabled.
/// </summary>
public sealed class ScheduledOwnerStatementWorker : BackgroundService
{
    // Check once per hour; fine-grained enough for a day-of-month trigger.
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledOwnerStatementWorker> _logger;

    // Tracks the last UTC date on which the batch ran so we don't double-send within the same day.
    private DateOnly? _lastRunDate;

    public ScheduledOwnerStatementWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledOwnerStatementWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledOwnerStatementWorker starting (poll interval {Interval})", PollInterval);

        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScheduledOwnerStatementWorker: unhandled error in cycle");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ScheduledOwnerStatementWorker stopped");
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider
            .GetRequiredService<IOptionsSnapshot<ReportsConfig>>()
            .Value;

        // Feature-flag gate — exit early and don't log noise when disabled.
        if (!config.EmailOwnerStatementsMonthly)
            return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var targetDay = Math.Clamp(config.StatementDayOfMonth, 1, 28);

        // Not the right day of month.
        if (today.Day != targetDay)
            return;

        // Already ran today.
        if (_lastRunDate == today)
        {
            _logger.LogDebug("ScheduledOwnerStatementWorker: already ran today ({Date}), skipping", today);
            return;
        }

        _logger.LogInformation(
            "ScheduledOwnerStatementWorker: triggering monthly owner statements for date {Date}", today);

        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IOwnerStatementEmailService>();

        // Mark before iterating so a partial run doesn't retry on restart during the same day.
        _lastRunDate = today;

        var year = today.Year;

        // Fetch all (portfolioId, ownerId) pairs where the owner has an email address.
        var owners = await db.OwnerEntities
            .AsNoTracking()
            .Where(o => o.DeletedAt == null && o.Email != null && o.Email != "")
            .Select(o => new { o.PortfolioId, OwnerId = o.Id, o.Name })
            .ToListAsync(ct);

        if (owners.Count == 0)
        {
            _logger.LogInformation("ScheduledOwnerStatementWorker: no owners with email addresses found");
            return;
        }

        _logger.LogInformation(
            "ScheduledOwnerStatementWorker: sending statements to {Count} owner(s) for year {Year}",
            owners.Count, year);

        var sent = 0;
        var failed = 0;

        foreach (var owner in owners)
        {
            // Each owner gets its own scope so one failure doesn't poison the DbContext.
            using var ownerScope = _scopeFactory.CreateScope();
            var ownerEmailService = ownerScope.ServiceProvider.GetRequiredService<IOwnerStatementEmailService>();

            try
            {
                var result = await ownerEmailService.SendOwnerStatementAsync(
                    owner.PortfolioId, owner.OwnerId, year, ct);

                if (result.Sent)
                {
                    sent++;
                }
                else
                {
                    _logger.LogWarning(
                        "ScheduledOwnerStatementWorker: skipped owner {OwnerId} ({Name}): {Reason}",
                        owner.OwnerId, owner.Name, result.Reason);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "ScheduledOwnerStatementWorker: failed to send statement for owner {OwnerId} ({Name})",
                    owner.OwnerId, owner.Name);
            }
        }

        _logger.LogInformation(
            "ScheduledOwnerStatementWorker: batch complete — {Sent} sent, {Failed} failed",
            sent, failed);
    }
}
