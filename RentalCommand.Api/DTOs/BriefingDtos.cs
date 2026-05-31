namespace RentalCommand.Api.DTOs;

public record BriefingBullet(
    string Title,
    string Detail,
    string Category,   // "RentDue" | "RentLate" | "Maintenance" | "Appointment" | "LeaseExpiring" | "Inspection"
    string Severity,   // "info" | "warning" | "critical"
    string? EntityType,
    int? EntityId);

public class BriefingResponse
{
    public DateTime Date { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? Summary { get; set; }          // LLM prose; null when LLM unavailable
    public bool LlmEnhanced { get; set; }
    public List<BriefingBullet> Bullets { get; set; } = new();
}
