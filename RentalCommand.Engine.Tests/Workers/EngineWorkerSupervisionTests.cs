using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using RentalCommand.Engine.Services;
using RentalCommand.Engine.Workers;
using RentalCommand.TestCommon;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class EngineWorkerSupervisionTests
{
    [Fact]
    public async Task Watchdog_ErrorThreshold_DoesNotStopHostAndHealthySiblingContinues()
    {
        using var sqlite = new SqliteTestContext();
        sqlite.Db.EngineWorkerHeartbeats.Add(new EngineWorkerHeartbeat
        {
            WorkerName = "OutboxDispatchWorker",
            LastHeartbeatUtc = DateTime.UtcNow,
            LastErrorUtc = DateTime.UtcNow,
            LastErrorMessage = "Expected persisted worker failure.",
            Status = EngineWorkerStatus.Error,
            StartedAtUtc = DateTime.UtcNow,
        });
        await sqlite.Db.SaveChangesAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<RentalCommandDbContext>(options =>
            options.UseSqlite(sqlite.ConnectionString));
        builder.Services.AddSingleton<IEngineWorkerHeartbeatStore, NoopHeartbeatStore>();
        builder.Services.AddScoped<EngineStatusReporter>();
        builder.Services.AddSingleton<HealthyWorker>();
        builder.Services.AddSingleton<WorkerWatchdogService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthyWorker>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerWatchdogService>());

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var watchdog = host.Services.GetRequiredService<WorkerWatchdogService>();
            var healthyWorker = host.Services.GetRequiredService<HealthyWorker>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            await RunWatchdogCheckAsync(watchdog);
            await RunWatchdogCheckAsync(watchdog);
            await RunWatchdogCheckAsync(watchdog);

            var siblingCyclesAtThreshold = healthyWorker.CycleCount;
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();
            healthyWorker.CycleCount.Should().BeGreaterThan(siblingCyclesAtThreshold);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task FailingWorker_RestartsWithBackoff_WithoutStoppingHostOrSibling()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEngineWorkerHeartbeatStore, NoopHeartbeatStore>();
        builder.Services.AddScoped<EngineStatusReporter>();
        builder.Services.AddSingleton<FailingWorker>();
        builder.Services.AddSingleton<HealthyWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FailingWorker>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthyWorker>());

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var failingWorker = host.Services.GetRequiredService<FailingWorker>();
            var healthyWorker = host.Services.GetRequiredService<HealthyWorker>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            await failingWorker.ThirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(6));
            var siblingCyclesBefore = healthyWorker.CycleCount;
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();
            healthyWorker.CycleCount.Should().BeGreaterThan(siblingCyclesBefore);
            failingWorker.AttemptCount.Should().BeGreaterThanOrEqualTo(3);
            (failingWorker.AttemptTimes[1] - failingWorker.AttemptTimes[0])
                .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(750))
                .And.BeLessThan(TimeSpan.FromSeconds(3));
            (failingWorker.AttemptTimes[2] - failingWorker.AttemptTimes[1])
                .Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1750))
                .And.BeLessThan(TimeSpan.FromSeconds(4));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private sealed class FailingWorker : EngineWorkerBase
    {
        private readonly object _gate = new();
        private readonly List<DateTimeOffset> _attemptTimes = [];

        public FailingWorker(IServiceProvider services, ILogger<FailingWorker> logger)
            : base(services, logger)
        {
        }

        public TaskCompletionSource ThirdAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttemptCount
        {
            get
            {
                lock (_gate)
                {
                    return _attemptTimes.Count;
                }
            }
        }

        public IReadOnlyList<DateTimeOffset> AttemptTimes
        {
            get
            {
                lock (_gate)
                {
                    return _attemptTimes.ToArray();
                }
            }
        }

        protected override string WorkerName => nameof(FailingWorker);
        protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
        protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(1);

        protected override Task<int> ExecuteCycleAsync(
            IServiceProvider scopedProvider,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _attemptTimes.Add(DateTimeOffset.UtcNow);
                if (_attemptTimes.Count >= 3)
                {
                    ThirdAttempt.TrySetResult();
                }
            }

            throw new InvalidOperationException("Expected worker failure.");
        }
    }

    private sealed class HealthyWorker : EngineWorkerBase
    {
        private int _cycleCount;

        public HealthyWorker(IServiceProvider services, ILogger<HealthyWorker> logger)
            : base(services, logger)
        {
        }

        public int CycleCount => Volatile.Read(ref _cycleCount);

        protected override string WorkerName => nameof(HealthyWorker);
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(10);
        protected override TimeSpan StepTimeout => TimeSpan.FromMinutes(1);

        protected override Task<int> ExecuteCycleAsync(
            IServiceProvider scopedProvider,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _cycleCount);
            return Task.FromResult(0);
        }
    }

    private sealed class NoopHeartbeatStore : IEngineWorkerHeartbeatStore
    {
        public Task RecordStartAsync(string workerName, DateTime nowUtc, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordHeartbeatAsync(
            string workerName,
            DateTime nowUtc,
            long processedDelta,
            string metadataJson,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordErrorAsync(
            string workerName,
            DateTime nowUtc,
            string errorMessage,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task RunWatchdogCheckAsync(WorkerWatchdogService watchdog)
    {
        var checkWorkers = typeof(WorkerWatchdogService).GetMethod(
            "CheckWorkersAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        checkWorkers.Should().NotBeNull();
        await (Task)checkWorkers!.Invoke(watchdog, [CancellationToken.None])!;
    }
}
