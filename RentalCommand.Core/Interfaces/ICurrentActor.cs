namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Resolves who/where for a write, used by the audit interceptor to attribute each
/// <see cref="Entities.AuditLog"/> row. HTTP requests supply the authenticated user; Engine
/// workers (no HttpContext) supply a system label. All members are null-safe.
/// </summary>
public interface ICurrentActor
{
    /// <summary>Authenticated user id (FK → ApplicationUser), or null for system/AI actors.</summary>
    int? UserId { get; }

    /// <summary>Display label for the actor (e.g. a name/email, or "system"); null when unknown.</summary>
    string? ActorLabel { get; }

    /// <summary>Client IP address (honoring a reverse proxy's forwarded header), or null.</summary>
    string? IpAddress { get; }
}
