using System.Threading.Channels;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Api.Services;

public sealed class RealtimeInvalidationQueue : BackgroundService, IRealtimeInvalidationQueue
{
    private const int DefaultCapacity = 512;
    private readonly Channel<IReadOnlyList<EntityUpdateBroadcast>> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RealtimeInvalidationQueue> _logger;

    public RealtimeInvalidationQueue(
        IServiceScopeFactory scopeFactory,
        ILogger<RealtimeInvalidationQueue> logger,
        int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _queue = Channel.CreateBounded<IReadOnlyList<EntityUpdateBroadcast>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void EnqueueEntityUpdates(IReadOnlyList<EntityUpdateBroadcast> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }

        if (!_queue.Writer.TryWrite(updates.ToArray()))
        {
            _logger.LogWarning(
                "Dropped realtime invalidation batch with {UpdateCount} updates because the queue is full or closed",
                updates.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var updates in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(updates, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(
        IReadOnlyList<EntityUpdateBroadcast> updates,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dataUpdate = scope.ServiceProvider.GetRequiredService<IDataUpdateService>();
            await dataUpdate.BroadcastEntityUpdatesAsync(updates, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed queued realtime invalidation batch with {UpdateCount} updates",
                updates.Count);
        }
    }
}
