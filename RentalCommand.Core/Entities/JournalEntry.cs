using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>One immutable, posted, portfolio-scoped journal entry.</summary>
public sealed class JournalEntry : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }
    public int PortfolioId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JournalSourceType SourceType { get; set; }
    public long SourceId { get; set; }
    public string SourceBusinessKey { get; set; } = string.Empty;
    public string IdempotencyDigest { get; set; } = string.Empty;
    public int PostingRuleVersion { get; set; }
    public int? ReversesJournalEntryId { get; set; }

    // The names and nullable shapes mirror the Atomic audit and receipt records.
    public Guid AttemptId { get; set; }
    public int? UserId { get; set; }
    public string? ActorLabel { get; set; }
    public Guid? AuthSessionId { get; set; }
    public int? AccessContextId { get; set; }
    public Guid AtomicReceiptId { get; set; }

    public Portfolio? Portfolio { get; set; }
    public JournalEntry? ReversedJournalEntry { get; set; }
    public List<JournalEntry> ReversalEntries { get; set; } = [];
    public List<JournalLine> Lines { get; set; } = [];
}
