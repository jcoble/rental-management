namespace RentalCommand.Core.Entities;

/// <summary>
/// An FCRA adverse-action notice generated when an application is declined based (wholly or partly) on
/// a screening report. The notice states the action taken (denied), the principal reason(s), names the
/// credit-reporting agency (CRA) with its address/phone, states that the CRA did not make the decision,
/// and informs the applicant of their right to a free copy of the report within 60 days and to dispute
/// its accuracy. The rendered PDF is stored as a <see cref="StoredFile"/> and may be emailed to the
/// applicant via the outbox.
/// </summary>
public class AdverseActionNotice
{
    public int Id { get; set; }

    /// <summary>Owning portfolio (scoped from the caller's JWT; never trusted from the client).</summary>
    public int PortfolioId { get; set; }

    /// <summary>The application that was declined.</summary>
    public int ApplicationId { get; set; }

    /// <summary>The principal reason(s) the application was declined (printed on the notice).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Name/address/phone of the credit-reporting agency that supplied the report (one block).</summary>
    public string CreditReportingAgency { get; set; } = string.Empty;

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The generated notice PDF (StoredFile with EntityType=Application). Null only if storage failed.</summary>
    public int? StoredFileId { get; set; }

    /// <summary>When the notice was enqueued to the applicant (email outbox); null when not sent.</summary>
    public DateTime? SentAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Portfolio? Portfolio { get; set; }
    public RentalApplication? Application { get; set; }
    public StoredFile? StoredFile { get; set; }
}
