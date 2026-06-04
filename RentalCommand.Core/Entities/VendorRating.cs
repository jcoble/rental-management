namespace RentalCommand.Core.Entities;

/// <summary>
/// A landlord-supplied 1–5 star rating of a vendor, optionally tied to the work order it followed.
/// Ratings accrue into the cached aggregates on <see cref="Vendor"/> (<c>AverageRating</c>,
/// <c>RatingCount</c>) so Frank can see at a glance who is reliable.
/// </summary>
public class VendorRating
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int VendorId { get; set; }

    /// <summary>The work order this rating is for, when the rating followed a specific job.</summary>
    public int? WorkOrderId { get; set; }

    /// <summary>Star rating, 1 (worst) to 5 (best).</summary>
    public int Stars { get; set; }

    public string? Comment { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Vendor? Vendor { get; set; }
    public WorkOrder? WorkOrder { get; set; }
}
