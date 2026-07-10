namespace RentalCommand.Api.Data;

/// <summary>
/// Server-owned actor mode for the RLS connection interceptor. Customer roles and absent request
/// context never set this value; only an explicit platform/background scope may bypass workspace RLS.
/// </summary>
public enum RlsActorMode
{
    Workspace = 0,
    Platform = 1,
    Background = 2,
}

public interface IRlsActorModeAccessor
{
    RlsActorMode Current { get; }
    IDisposable Begin(RlsActorMode mode);
}

public sealed class RlsActorModeAccessor : IRlsActorModeAccessor
{
    private readonly AsyncLocal<RlsActorMode?> _current = new();

    public RlsActorMode Current => _current.Value ?? RlsActorMode.Workspace;

    public IDisposable Begin(RlsActorMode mode)
    {
        if (mode == RlsActorMode.Workspace)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Only an explicit bypass mode opens a scope.");
        }

        var prior = _current.Value;
        _current.Value = mode;
        return new RestoreScope(() => _current.Value = prior);
    }

    private sealed class RestoreScope : IDisposable
    {
        private Action? _restore;

        public RestoreScope(Action restore) => _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
