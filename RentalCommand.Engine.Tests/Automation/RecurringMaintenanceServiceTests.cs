using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Simulation;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Automation;
using RentalCommand.Engine.Services;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Automation;

/// <summary>
/// Covers the recurring-maintenance generator: a due active task produces exactly one work order
/// (with its initial status event) and advances NextDueDate by the interval; running twice in a
/// period does not double-generate; an inactive task generates nothing; the master config flag gates
/// the whole feature.
/// </summary>
public class RecurringMaintenanceServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly ServiceProvider _services;
    private bool _throwOnFailureRecording;
    private int _failureRecordCalls;

    public RecurringMaintenanceServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IScheduledAutomationClaimStore>(provider =>
            new ThrowingFailureRecordingClaimStore(
                provider.GetRequiredService<RentalCommandDbContext>(),
                () => _throwOnFailureRecording,
                () => _failureRecordCalls++));
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(_ctx.ConnectionString).UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _ctx.Dispose();
    }

    // -----------------------------------------------------------------------

    [Fact]
    public async Task DueActiveTask_GeneratesOneWorkOrder_AdvancesNextDueDate_AndIsIdempotent()
    {
        // Seed the due date a day in the past so the test is independent of the service's business
        // timezone (its local "today" can be a calendar day behind UtcNow.Date near midnight UTC).
        var dueDate = DateTime.UtcNow.Date.AddDays(-1);
        var property = SeedProperty();
        var task = SeedTask(
            property.Id,
            interval: RecurrenceInterval.Quarterly,
            nextDueDate: dueDate,
            isActive: true,
            title: "HVAC filter",
            category: "HVAC",
            priority: WorkOrderPriority.High,
            scheduledTime: new TimeOnly(14, 30),
            estimatedCost: 95m);

        var sut = BuildService(enable: true);

        // First run creates exactly one work order.
        var firstResult = await sut.GenerateAsync();
        firstResult.Should().Be(1);
        _ctx.Db.ChangeTracker.Clear();

        var workOrders = _ctx.Db.WorkOrders.ToList();
        workOrders.Should().HaveCount(1);

        var wo = workOrders[0];
        wo.PortfolioId.Should().Be(PortfolioId);
        wo.PropertyId.Should().Be(property.Id);
        wo.Title.Should().Be("HVAC filter");
        wo.Category.Should().Be("HVAC");
        wo.Priority.Should().Be(WorkOrderPriority.High);
        wo.Status.Should().Be(WorkOrderStatus.New);
        wo.RecurringMaintenanceTaskId.Should().Be(task.Id);
        wo.EstimatedCost.Should().Be(95m);
        wo.ScheduledFor.Should().NotBeNull();
        var localScheduled = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(wo.ScheduledFor!.Value, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        localScheduled.Date.Should().Be(dueDate);
        TimeOnly.FromDateTime(localScheduled).Should().Be(new TimeOnly(14, 30));

        // Initial status event written (null → New, System).
        var events = _ctx.Db.WorkOrderStatusEvents.ToList();
        events.Should().HaveCount(1);
        events[0].FromStatus.Should().BeNull();
        events[0].ToStatus.Should().Be(WorkOrderStatus.New);
        events[0].ChangedByLabel.Should().Be("System");

        // NextDueDate advanced by the interval (Quarterly = +3 months) off the due date; stamped.
        var reloaded = _ctx.Db.RecurringMaintenanceTasks.Single(t => t.Id == task.Id);
        reloaded.NextDueDate.Date.Should().Be(dueDate.AddMonths(3));
        reloaded.LastGeneratedAtUtc.Should().NotBeNull();

        // Second run in the same period creates nothing (NextDueDate is now in the future).
        var secondResult = await sut.GenerateAsync();
        secondResult.Should().Be(0);
        _ctx.Db.WorkOrders.Count().Should().Be(1);
    }

    [Fact]
    public async Task InactiveTask_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, interval: RecurrenceInterval.Monthly, nextDueDate: today, isActive: false);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task FutureDueTask_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, interval: RecurrenceInterval.Monthly, nextDueDate: today.AddDays(10), isActive: true);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task MasterFlagDisabled_GeneratesNothing()
    {
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        SeedTask(property.Id, RecurrenceInterval.Monthly, today, isActive: true);

        var sut = BuildService(enable: false);

        var result = await sut.GenerateAsync();

        result.Should().Be(0);
        _ctx.Db.WorkOrders.Should().BeEmpty();
    }

    [Fact]
    public async Task BackloggedTask_GeneratesOnlyOne_AndCatchesUpPastToday()
    {
        // A task that fell several weeks behind (Engine was down) should produce exactly ONE work
        // order this run and roll the schedule forward past "today" rather than flooding the queue.
        var today = DateTime.UtcNow.Date;
        var property = SeedProperty();
        var task = SeedTask(
            property.Id,
            interval: RecurrenceInterval.Weekly,
            nextDueDate: today.AddDays(-30),
            isActive: true);

        var sut = BuildService(enable: true);

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        _ctx.Db.ChangeTracker.Clear();
        _ctx.Db.WorkOrders.Count().Should().Be(1);

        var reloaded = _ctx.Db.RecurringMaintenanceTasks.Single(t => t.Id == task.Id);
        reloaded.NextDueDate.Date.Should().BeAfter(today);
    }

    [Fact]
    public async Task SpringForwardGap_At0230_GeneratesTheShiftedWorkOrder()
    {
        var businessDate = new DateTime(2027, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var property = SeedProperty();
        var task = SeedTask(
            property.Id,
            interval: RecurrenceInterval.Monthly,
            nextDueDate: dueDate,
            isActive: true,
            title: "Spring-forward HVAC check",
            category: "HVAC",
            priority: WorkOrderPriority.Normal,
            scheduledTime: new TimeOnly(2, 30),
            estimatedCost: null);

        var sut = BuildService(true, new FixedTimeProvider(businessDate));

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        var scheduled = _ctx.Db.WorkOrders.Single(row => row.RecurringMaintenanceTaskId == task.Id)
            .ScheduledFor;
        scheduled.Should().Be(new DateTime(2027, 3, 14, 7, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task FailedTask_DoesNotBlockHealthyTaskBehindIt()
    {
        var businessDate = new DateTime(2027, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var poison = SeedTask(
            SeedProperty().Id,
            (RecurrenceInterval)999,
            dueDate,
            isActive: true,
            title: "Unsupported interval");
        var healthy = SeedTask(
            SeedProperty(2).Id,
            RecurrenceInterval.Monthly,
            dueDate,
            isActive: true,
            title: "Healthy schedule",
            category: "General",
            priority: WorkOrderPriority.Normal,
            scheduledTime: null,
            estimatedCost: null,
            portfolioId: 2);

        var sut = BuildService(true, new FixedTimeProvider(businessDate));

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        _ctx.Db.WorkOrders.Single(row => row.RecurringMaintenanceTaskId == healthy.Id).Should().NotBeNull();
        _ctx.Db.WorkOrders.Should().NotContain(row => row.RecurringMaintenanceTaskId == poison.Id);
        _ctx.Db.ChangeTracker.Clear();
        var failed = _ctx.Db.RecurringMaintenanceTasks.Single(row => row.Id == poison.Id);
        failed.WorkerClaimAttemptCount.Should().Be(1);
        failed.WorkerClaimLastFailureReason.Should().Be(
            "This recurring maintenance schedule uses an unsupported repeat interval. " +
            "Edit it and choose a supported interval.");
        failed.WorkerClaimQuarantinedAtUtc.Should().BeNull();

        failed.WorkerClaimLastFailureReason.Should().NotContain("InvalidOperationException");
        failed.WorkerClaimLastFailureReason.Should().NotContain("Unsupported recurring-maintenance interval");
    }

    [Fact]
    public async Task FailedTask_WhenFailureRecordingAlsoThrows_DoesNotBlockLaterPortfolioTask()
    {
        var businessDate = new DateTime(2027, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var poison = SeedTask(
            SeedProperty().Id,
            (RecurrenceInterval)999,
            dueDate,
            isActive: true,
            title: "Failure recorder poison");
        var healthy = SeedTask(
            SeedProperty(2).Id,
            RecurrenceInterval.Monthly,
            dueDate,
            isActive: true,
            title: "Later portfolio schedule",
            category: "General",
            priority: WorkOrderPriority.Normal,
            scheduledTime: null,
            estimatedCost: null,
            portfolioId: 2);

        _throwOnFailureRecording = true;
        var sut = BuildService(true, new FixedTimeProvider(businessDate));

        var result = await sut.GenerateAsync();

        result.Should().Be(1);
        _failureRecordCalls.Should().Be(1);
        _ctx.Db.WorkOrders.Should().ContainSingle(row =>
            row.RecurringMaintenanceTaskId == healthy.Id && row.PortfolioId == 2);
        _ctx.Db.WorkOrders.Should().NotContain(row => row.RecurringMaintenanceTaskId == poison.Id);
    }

    [Fact]
    public void FailureReasons_MapKnownFailures_AndHideUnknownDetails()
    {
        RecurringMaintenanceService.BuildFailureReason(
                new ArgumentException("The supplied DateTime represents an invalid time."),
                "RM-known")
            .Should().Be(
                "The scheduled time falls during a daylight-saving time change. Pick a different time.");

        var generic = RecurringMaintenanceService.BuildFailureReason(
            new InvalidOperationException("provider secret and internal constraint detail"),
            "RM-unknown");
        generic.Should().Be(
            "Recurring maintenance could not be generated. Check the schedule and try again. " +
            "Reference: RM-unknown");
        generic.Should().NotContain("InvalidOperationException");
        generic.Should().NotContain("provider secret and internal constraint detail");
    }

    [Fact]
    public async Task UnknownFailure_StoresSafeReasonWithCorrelationId()
    {
        var businessDate = new DateTime(2027, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var task = SeedTask(
            SeedProperty().Id,
            RecurrenceInterval.Monthly,
            dueDate,
            isActive: true,
            title: "Unknown failure schedule");
        const string privateTimeZoneId = "H5-private-time-zone";
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            privateTimeZoneId,
            TimeSpan.Zero,
            privateTimeZoneId,
            privateTimeZoneId);
        var sut = BuildService(
            true,
            new FixedTimeProvider(businessDate),
            new FixedTimeZoneProvider(timeZone));

        (await sut.GenerateAsync()).Should().Be(0);

        _ctx.Db.ChangeTracker.Clear();
        var reason = _ctx.Db.RecurringMaintenanceTasks.Single(row => row.Id == task.Id)
            .WorkerClaimLastFailureReason;
        reason.Should().NotBeNull();
        reason.Should().MatchRegex(
            "^Recurring maintenance could not be generated\\. Check the schedule and try again\\. " +
            "Reference: RM-[0-9a-f]{32}$");
        reason.Should().NotContain(nameof(TimeZoneNotFoundException));
        reason.Should().NotContain(nameof(InvalidTimeZoneException));
        reason.Should().NotContain(privateTimeZoneId);
    }

    [Fact]
    public async Task FailedTask_IsQuarantinedAfterThreeAttempts_AndLaterHealthyWorkContinues()
    {
        var businessDate = new DateTime(2027, 3, 14, 12, 0, 0, DateTimeKind.Utc);
        var dueDate = new DateTime(2027, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var poison = SeedTask(
            SeedProperty().Id,
            (RecurrenceInterval)999,
            dueDate,
            isActive: true,
            title: "Quarantine me");
        SeedTask(
            SeedProperty(2).Id,
            RecurrenceInterval.Monthly,
            dueDate,
            isActive: true,
            title: "Healthy first",
            category: "General",
            priority: WorkOrderPriority.Normal,
            scheduledTime: null,
            estimatedCost: null,
            portfolioId: 2);
        var sut = BuildService(true, new FixedTimeProvider(businessDate));

        (await sut.GenerateAsync()).Should().Be(1);
        (await sut.GenerateAsync()).Should().Be(0);
        (await sut.GenerateAsync()).Should().Be(0);

        _ctx.Db.ChangeTracker.Clear();
        var quarantined = _ctx.Db.RecurringMaintenanceTasks.Single(row => row.Id == poison.Id);
        quarantined.WorkerClaimAttemptCount.Should().Be(
            ScheduledAutomationPolicy.RecurringMaintenanceQuarantineAfterAttempts);
        quarantined.WorkerClaimQuarantinedAtUtc.Should().NotBeNull();
        quarantined.WorkerClaimLastFailureReason.Should().Be(
            "This recurring maintenance schedule uses an unsupported repeat interval. " +
            "Edit it and choose a supported interval.");
        quarantined.WorkerClaimToken.Should().BeNull();

        var laterHealthy = SeedTask(
            SeedProperty(2).Id,
            RecurrenceInterval.Monthly,
            dueDate,
            isActive: true,
            title: "Healthy after quarantine",
            category: "General",
            priority: WorkOrderPriority.Normal,
            scheduledTime: null,
            estimatedCost: null,
            portfolioId: 2);
        (await sut.GenerateAsync()).Should().Be(1);
        _ctx.Db.WorkOrders.Should().Contain(row => row.RecurringMaintenanceTaskId == laterHealthy.Id);
        _ctx.Db.RecurringMaintenanceTasks.Single(row => row.Id == poison.Id)
            .WorkerClaimAttemptCount.Should().Be(ScheduledAutomationPolicy.RecurringMaintenanceQuarantineAfterAttempts);
    }

    // -----------------------------------------------------------------------
    // Helpers

    private RecurringMaintenanceService BuildService(
        bool enable,
        TimeProvider? timeProvider = null,
        IAppTimeZoneProvider? timeZoneProvider = null)
    {
        var now = DateTime.UtcNow;
        var settings = _ctx.Db.AutomationSettings.SingleOrDefault(row => row.PortfolioId == PortfolioId);
        if (settings is null)
        {
            _ctx.Db.AutomationSettings.Add(new AutomationSettings
            {
                PortfolioId = PortfolioId,
                EnableRecurringMaintenance = enable,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            settings.EnableRecurringMaintenance = enable;
            settings.UpdatedAtUtc = now;
        }
        _ctx.Db.SaveChanges();

        return new RecurringMaintenanceService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider ?? TimeProvider.System,
            timeZoneProvider ?? new AppTimeZoneProvider(new ConfigurationBuilder().Build()),
            _services.GetRequiredService<IScheduledAutomationClaimStore>(),
            NullLogger<RecurringMaintenanceService>.Instance);
    }

    private Property SeedProperty(int portfolioId = PortfolioId)
    {
        EnsurePortfolio(portfolioId);
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = portfolioId,
            Name = "Test Property",
            AddressLine1 = "123 Main St",
            City = "Springfield",
            State = "IL",
            PostalCode = "62701",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        return property;
    }

    private RecurringMaintenanceTask SeedTask(
        int propertyId,
        RecurrenceInterval interval,
        DateTime nextDueDate,
        bool isActive,
        string title = "Recurring chore",
        string? category = "General",
        WorkOrderPriority priority = WorkOrderPriority.Normal)
        => SeedTask(propertyId, interval, nextDueDate, isActive, title, category, priority, null, null);

    private RecurringMaintenanceTask SeedTask(
        int propertyId,
        RecurrenceInterval interval,
        DateTime nextDueDate,
        bool isActive,
        string title,
        string? category,
        WorkOrderPriority priority,
        TimeOnly? scheduledTime,
        decimal? estimatedCost,
        int portfolioId = PortfolioId)
    {
        EnsurePortfolio(portfolioId);
        var now = DateTime.UtcNow;
        var task = new RecurringMaintenanceTask
        {
            PortfolioId = portfolioId,
            PropertyId = propertyId,
            Title = title,
            Category = category,
            RecurrenceInterval = interval,
            NextDueDate = DateTime.SpecifyKind(nextDueDate.Date, DateTimeKind.Utc),
            ScheduledTime = scheduledTime,
            EstimatedCost = estimatedCost,
            IsActive = isActive,
            Priority = priority,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.RecurringMaintenanceTasks.Add(task);
        _ctx.Db.SaveChanges();
        return task;
    }

    private void EnsurePortfolio(int portfolioId)
    {
        if (_ctx.Db.Portfolios.Any(row => row.Id == portfolioId)) return;
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = portfolioId,
            Name = $"Test Portfolio {portfolioId}",
            ManagementCompanyName = "Test Co",
            TimeZone = "America/New_York",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : IAppTimeZoneProvider
    {
        public TimeZoneInfo BusinessTimeZone { get; } = timeZone;
    }

    private sealed class ThrowingFailureRecordingClaimStore(
        RentalCommandDbContext db,
        Func<bool> shouldThrow,
        Action onFailureRecord) : IScheduledAutomationClaimStore
    {
        private readonly TestScheduledAutomationClaimStore _inner = new(db);

        public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimDebtServiceAsync(
            string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default) =>
            _inner.ClaimDebtServiceAsync(owner, todayUtc, leaseDuration, batchSize, ct);

        public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringExpensesAsync(
            string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default) =>
            _inner.ClaimRecurringExpensesAsync(owner, todayUtc, leaseDuration, batchSize, ct);

        public Task<IReadOnlyList<ScheduledAutomationClaim>> ClaimRecurringMaintenanceAsync(
            string owner, DateTime todayUtc, TimeSpan leaseDuration, int batchSize,
            CancellationToken ct = default) =>
            _inner.ClaimRecurringMaintenanceAsync(owner, todayUtc, leaseDuration, batchSize, ct);

        public async Task<bool> RecordRecurringMaintenanceFailureAsync(
            ScheduledAutomationClaim claim,
            DateTime failedAtUtc,
            string failureReason,
            int quarantineAfterAttempts,
            CancellationToken ct = default)
        {
            onFailureRecord();
            if (shouldThrow())
                throw new InvalidOperationException("Injected failure-recording store failure.");

            return await _inner.RecordRecurringMaintenanceFailureAsync(
                claim, failedAtUtc, failureReason, quarantineAfterAttempts, ct);
        }
    }
}
