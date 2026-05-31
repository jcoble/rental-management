using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>
/// Wire shape returned for an <see cref="ActivityLog"/> entry. The activity feed is append-only, so there
/// is no create/update/delete via the API; this is a read-only projection.
/// </summary>
public class ActivityResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public RentalActivityType Type { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public int? EntityId { get; set; }
    public string? Action { get; set; }
    public string? Description { get; set; }
    public string? Actor { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>activity-1</c>.</summary>
    public string TestId => $"activity-{Id}";

    public static ActivityResponse FromEntity(ActivityLog e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        Type = e.Type,
        TypeName = e.Type.ToString(),
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Action = e.Action,
        Description = e.Description,
        Actor = e.Actor,
        CreatedAt = e.CreatedAt,
    };
}
