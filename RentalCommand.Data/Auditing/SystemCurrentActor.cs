using RentalCommand.Core.Interfaces;

namespace RentalCommand.Data.Auditing;

/// <summary>
/// Ambient actor used by non-HTTP hosts (the Engine workers) where there is no authenticated user.
/// Attributes audit rows to "system" with no user id or IP. The API supplies its own
/// HTTP-backed <see cref="ICurrentActor"/> instead.
/// </summary>
public sealed class SystemCurrentActor : ICurrentActor
{
    public int? UserId => null;
    public string? ActorLabel => "system";
    public string? IpAddress => null;
}
