using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RentalCommand.Api.Services;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Tests.Services;

public sealed class RealtimeInvalidationQueueTests
{
    [Fact]
    public void EnqueueEntityUpdates_WhenBoundedQueueIsFull_DropsBatchWithoutBlocking()
    {
        using var provider = new ServiceCollection()
            .AddScoped<IDataUpdateService, RecordingDataUpdateService>()
            .BuildServiceProvider();
        var logger = new RecordingLogger<RealtimeInvalidationQueue>();
        var queue = new RealtimeInvalidationQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            capacity: 1);
        var update = new EntityUpdateBroadcast(1, "Conversation", 10, new { Id = 10 });

        queue.EnqueueEntityUpdates([update]);
        var elapsed = Stopwatch.StartNew();
        queue.EnqueueEntityUpdates([update]);
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Dropped realtime invalidation batch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WhenBatchFails_ContinuesProcessingLaterBatches()
    {
        var dataUpdate = new RecordingDataUpdateService
        {
            ThrowOnFirstBatch = true,
        };
        using var provider = new ServiceCollection()
            .AddSingleton<IDataUpdateService>(dataUpdate)
            .BuildServiceProvider();
        var logger = new RecordingLogger<RealtimeInvalidationQueue>();
        var queue = new RealtimeInvalidationQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            capacity: 8);

        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.EnqueueEntityUpdates([new EntityUpdateBroadcast(1, "Conversation", 10, new { Id = 10 })]);
            queue.EnqueueEntityUpdates([new EntityUpdateBroadcast(1, "Notification", 20, new { Id = 20 })]);

            await dataUpdate.SecondBatchProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        dataUpdate.Batches.Should().HaveCount(2);
        dataUpdate.Batches[1].Should().ContainSingle(update => update.EntityType == "Notification");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("Failed queued realtime invalidation batch", StringComparison.Ordinal));
    }

    private sealed class RecordingDataUpdateService : IDataUpdateService
    {
        private int _batchCount;

        public bool ThrowOnFirstBatch { get; init; }
        public List<IReadOnlyList<EntityUpdateBroadcast>> Batches { get; } = [];
        public TaskCompletionSource SecondBatchProcessed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BroadcastEntityUpdateAsync(
            int portfolioId,
            string entityType,
            int entityId,
            object data,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityUpdatesAsync(
            IReadOnlyList<EntityUpdateBroadcast> updates,
            CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _batchCount);
            Batches.Add(updates.ToArray());
            if (ThrowOnFirstBatch && count == 1)
            {
                throw new InvalidOperationException("first batch failure");
            }

            if (count == 2)
            {
                SecondBatchProcessed.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task BroadcastEntityDeleteAsync(
            int portfolioId,
            string entityType,
            int entityId,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
