namespace RentalCommand.Core.Entities;

/// <summary>
/// A landlord-authored, reusable notice template for one notice type within a portfolio. The
/// <see cref="Subject"/> and <see cref="Body"/> hold <c>{{merge_token}}</c> fields that are filled
/// deterministically at generation time. At most one row per (PortfolioId, NoticeType) has
/// <see cref="IsActive"/> = true; that is the template used for generation.
/// </summary>
public class NoticeTemplate
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>The canonical tenant-notice automation key used by drafts and policies.</summary>
    public string NoticeType { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
