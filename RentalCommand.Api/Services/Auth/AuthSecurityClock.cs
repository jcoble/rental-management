namespace RentalCommand.Api.Services.Auth;

/// <summary>
/// Real security clock for custom authentication. Simulation can move business time, but login
/// eligibility, challenges, sessions, and bearer expirations must stay on wall-clock time.
/// </summary>
public interface IAuthSecurityClock
{
    DateTime UtcNow();
}

public sealed class SystemAuthSecurityClock : IAuthSecurityClock
{
    public DateTime UtcNow() => TimeProvider.System.GetUtcNow().UtcDateTime;
}
