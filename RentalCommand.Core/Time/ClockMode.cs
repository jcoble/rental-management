namespace RentalCommand.Core.Time;

/// <summary>
/// How the simulation clock computes "now".
/// <list type="bullet">
/// <item><see cref="Real"/> — the machine clock (production default; the provider is <c>TimeProvider.System</c>).</item>
/// <item><see cref="Frozen"/> — a fixed instant that does not advance until explicitly moved.</item>
/// <item><see cref="Offset"/> — real time shifted by a fixed offset (ticks forward from an anchor).</item>
/// </list>
/// Only ever anything but <see cref="Real"/> in non-production (dev/test), gated by <c>Simulation:Enabled</c>.
/// </summary>
public enum ClockMode
{
    Real,
    Frozen,
    Offset,
}
