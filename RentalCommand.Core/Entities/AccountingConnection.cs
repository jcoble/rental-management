using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A landlord's OAuth connection to an external accounting system. One row per
/// portfolio per provider — a portfolio can connect QuickBooks now and Xero
/// later, and both rows coexist. The provider-agnostic hybrid of EdiPlatform's
/// <c>ErpConnection</c> (the generic shape + delta cursors) and RC's
/// <c>BankConnection</c> (encrypted <c>*CipherText</c> columns via
/// <c>IDataProtector</c>).
///
/// <para>
/// Tokens are stored ONLY as encrypted cipher text (AC-4); plaintext never hits
/// the DB. Per-direction toggles let the landlord enable pull (the primary value:
/// money into Rental Command) and/or push independently.
/// </para>
/// </summary>
public class AccountingConnection : IPortfolioScoped, IAuditable
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public AccountingProvider Provider { get; set; }

    public AccountingConnectionStatus Status { get; set; } = AccountingConnectionStatus.Pending;

    /// <summary>Provider company/account scope — QuickBooks <c>realmId</c>, a Xero tenant id, etc.</summary>
    public string? ExternalAccountId { get; set; }

    /// <summary>Display only — the company name reported by the provider.</summary>
    public string? CompanyName { get; set; }

    /// <summary>Encrypted via <c>IDataProtector</c>. Never logged, never stored in plaintext.</summary>
    public string? AccessTokenCipherText { get; set; }

    /// <summary>Encrypted via <c>IDataProtector</c>.</summary>
    public string? RefreshTokenCipherText { get; set; }

    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>Bring the landlord's books into Rental Command (the primary direction). On by default.</summary>
    public bool PullEnabled { get; set; } = true;

    /// <summary>Send Rental Command into the landlord's books (secondary). Off by default.</summary>
    public bool PushEnabled { get; set; }

    /// <summary>jsonb per-resource delta cursors, e.g. <c>{"customers":"2026-05-01T...","payments":"..."}</c>.</summary>
    public string? LastPulledAtJson { get; set; }

    public string? LastError { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }

    /// <summary>DB-side due time used for fair scheduled-pull ordering.</summary>
    public DateTime NextPullAtUtc { get; set; } = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    public string? PullClaimOwner { get; set; }
    public Guid? PullClaimToken { get; set; }
    public DateTime? PullClaimExpiresAtUtc { get; set; }
    public int PullAttemptCount { get; set; }
    public DateTime? PullLastAttemptAtUtc { get; set; }

    public string? RefreshClaimOwner { get; set; }
    public Guid? RefreshClaimToken { get; set; }
    public DateTime? RefreshClaimExpiresAtUtc { get; set; }
    public int RefreshAttemptCount { get; set; }
    public DateTime? RefreshLastAttemptAtUtc { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
}
