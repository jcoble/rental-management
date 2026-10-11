using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>Append-only legal signature evidence.</summary>
public class SignatureAuditEvent : IPortfolioScoped
{
    public long Id { get; set; }
    public int PortfolioId { get; set; }
    public int SignatureRequestId { get; set; }
    public int? SignatureSignerId { get; set; }
    public SignatureAuditEventType Type { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Detail { get; set; }

    public SignatureRequest? SignatureRequest { get; set; }
    public SignatureSigner? SignatureSigner { get; set; }
}
