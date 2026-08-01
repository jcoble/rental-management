using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Time;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class EngineWorkerStartupClockTests
{
    [Fact]
    public async Task StartAsync_InitializesPersistedSimulationClockBeforeFirstCycle()
    {
        var frozen = new ClockState(
            ClockMode.Frozen,
            new DateTime(2027, 1, 26, 5, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 28, 11, 57, 42, DateTimeKind.Utc),
            "America/New_York");
        var clock = new GateClockStateProvider(frozen);
        var services = new ServiceCollection()
            .AddSingleton<IClockStateProvider>(clock)
            .BuildServiceProvider();
        var worker = new RecordingWorker(services);

        await worker.StartAsync(CancellationToken.None);

        await worker.FirstCycle.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, clock.EnsureCallCount);
        Assert.Equal(1, worker.CycleCount);
        Assert.Equal(frozen, worker.ClockAtFirstCycle);
        Assert.True(clock.HasLoadedPersistedState);
    }

    private sealed class RecordingWorker : EngineWorkerBase
    {
        private readonly IServiceProvider _services;
        private int _cycleCount;

        public RecordingWorker(IServiceProvider services)
            : base(services, NullLogger<RecordingWorker>.Instance) =>
            _services = services;

        public TaskCompletionSource FirstCycle { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CycleCount => _cycleCount;

        public ClockState? ClockAtFirstCycle { get; private set; }

        protected override string WorkerName => nameof(RecordingWorker);
        protected override TimeSpan PollInterval => TimeSpan.FromHours(1);
        protected override TimeSpan StepTimeout => TimeSpan.FromSeconds(5);

        protected override Task<int> ExecuteCycleAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
        {
            _cycleCount++;
            ClockAtFirstCycle = _services.GetRequiredService<IClockStateProvider>().Current;
            FirstCycle.TrySetResult();
            return Task.FromResult(0);
        }
    }

    private sealed class GateClockStateProvider : IClockStateProvider
    {
        private readonly ClockState _persistedState;

        public GateClockStateProvider(ClockState persistedState) =>
            _persistedState = persistedState;

        public ClockState Current { get; private set; } = ClockState.RealTime;

        public bool HasLoadedPersistedState { get; private set; }

        public int EnsureCallCount { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Current = _persistedState;
            HasLoadedPersistedState = true;
            return Task.CompletedTask;
        }

        public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            EnsureCallCount++;
            await RefreshAsync(cancellationToken);
        }
    }
}
