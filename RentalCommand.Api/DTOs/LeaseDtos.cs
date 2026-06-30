using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for a <see cref="Lease"/>.</summary>
public class LeaseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int PropertyId { get; set; }
    public int UnitId { get; set; }
    public int TenantId { get; set; }
    public IReadOnlyList<int> TenantIds { get; set; } = [];
    public string LeaseNumber { get; set; } = string.Empty;
    public LeaseStatus Status { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }
    public decimal MonthlyRent { get; set; }
    public decimal SecurityDeposit { get; set; }
    public decimal LateFeeAmount { get; set; }
    public int RentDueDay { get; set; }
    public DateTime? RentTrackingStartDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Tenant's full name, projected from the <see cref="Lease.Tenant"/> navigation. Null when not loaded.</summary>
    public string? TenantName { get; set; }

    /// <summary>All tenants tied to this lease. <see cref="TenantId"/> remains the primary/back-compat tenant.</summary>
    public IReadOnlyList<LeaseTenantResponse> Tenants { get; set; } = [];

    /// <summary>Unit number, projected from the <see cref="Lease.Unit"/> navigation. Null when not loaded.</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Property name, projected from the <see cref="Lease.Property"/> navigation. Null when not loaded.</summary>
    public string? PropertyName { get; set; }

    /// <summary>True when a scanned source document is attached to this lease (drives the detail-page viewer).</summary>
    public bool HasScan { get; set; }

    /// <summary>True when the attached scan is an image (vs a PDF) — lets the UI show a thumbnail.</summary>
    public bool ScanIsImage { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>lease-1</c>.</summary>
    public string TestId => $"lease-{Id}";

    public static LeaseResponse FromEntity(Lease e, bool includeNavigations = false)
    {
        var response = new LeaseResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            PropertyId = e.PropertyId,
            UnitId = e.UnitId,
            TenantId = e.TenantId,
            TenantIds = e.TenantId > 0 ? [e.TenantId] : [],
            LeaseNumber = e.LeaseNumber,
            Status = e.Status,
            StartDate = e.StartDate,
            EndDate = e.EndDate,
            MoveInDate = e.MoveInDate,
            MoveOutDate = e.MoveOutDate,
            MonthlyRent = e.MonthlyRent,
            SecurityDeposit = e.SecurityDeposit,
            LateFeeAmount = e.LateFeeAmount,
            RentDueDay = e.RentDueDay,
            RentTrackingStartDate = e.RentTrackingStartDate,
            Notes = e.Notes,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };

        if (includeNavigations)
        {
            // Pickers (scan confirm, payment/deposit create) need human-readable labels,
            // not just the bare lease number. Projected from existing navigations.
            var tenants = e.LeaseTenants.Count == 0
                ? new List<LeaseTenantResponse>()
                : e.LeaseTenants
                    .OrderByDescending(lt => lt.IsPrimary)
                    .ThenBy(lt => lt.Id)
                    .Where(lt => lt.Tenant != null)
                    .Select(LeaseTenantResponse.FromEntity)
                    .ToList();
            if (tenants.Count == 0 && e.Tenant != null)
            {
                tenants =
                [
                    new LeaseTenantResponse
                    {
                        Id = e.Tenant.Id,
                        Name = $"{e.Tenant.FirstName} {e.Tenant.LastName}".Trim(),
                        Email = e.Tenant.Email,
                        Phone = e.Tenant.Phone,
                        IsPrimary = true,
                    },
                ];
            }

            response.Tenants = tenants;
            response.TenantIds = tenants.Count == 0 ? response.TenantIds : tenants.Select(t => t.Id).ToList();
            response.TenantName = tenants.Count == 0 ? null : string.Join(", ", tenants.Select(t => t.Name));
            response.UnitNumber = e.Unit?.UnitNumber;
            response.PropertyName = e.Property?.Name;
        }

        return response;
    }
}

public class LeaseTenantResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsPrimary { get; set; }

    public static LeaseTenantResponse FromEntity(LeaseTenant leaseTenant)
    {
        var tenant = leaseTenant.Tenant!;
        return new LeaseTenantResponse
        {
            Id = tenant.Id,
            Name = $"{tenant.FirstName} {tenant.LastName}".Trim(),
            Email = tenant.Email,
            Phone = tenant.Phone,
            IsPrimary = leaseTenant.IsPrimary,
        };
    }
}

public class LeaseListResponse
{
    public IReadOnlyList<LeaseResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class LeaseListQuery : ListQuery
{
    public int? TenantId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public LeaseStatus? Status { get; set; }
}

/// <summary>
/// Tenant-facing ledger for a single lease: every charge and payment, newest first, each carrying a
/// plain-English <see cref="LedgerTransactionResponse.Explanation"/> ("why") so a tenant can see
/// exactly what each line is. <see cref="Balance"/> is what the tenant still owes (charges minus
/// payments); negative means a credit/overpayment.
/// </summary>
public class LeaseLedgerResponse
{
    public int LeaseId { get; set; }
    public string LeaseNumber { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string? PropertyName { get; set; }

    /// <summary>Sum of charges (rent, fees, deposits) owed on this lease.</summary>
    public decimal TotalCharged { get; set; }

    /// <summary>Sum of payments received on this lease.</summary>
    public decimal TotalPaid { get; set; }

    /// <summary>Outstanding balance: <see cref="TotalCharged"/> minus <see cref="TotalPaid"/>. Negative = credit.</summary>
    public decimal Balance { get; set; }

    /// <summary>Ledger entries (charges and payments), newest first. Each has a plain-English explanation.</summary>
    public IReadOnlyList<LedgerTransactionResponse> Entries { get; set; } = [];

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => $"lease-ledger-{LeaseId}";
}

/// <summary>
/// Reference to a generated lease-agreement document, returned by the generate-document endpoint.
/// </summary>
public sealed record LeaseDocumentResponse(
    int StoredFileId,
    int LeaseId,
    string FileName,
    long FileSize,
    string DownloadUrl,
    DateTime GeneratedAt);

/// <summary>
/// Lightweight status for the generated lease-agreement document. Used by the UI to decide whether
/// to show Download without probing the blob endpoint and producing an expected 404.
/// </summary>
public sealed record LeaseDocumentStatusResponse(
    int LeaseId,
    bool HasDocument,
    int? StoredFileId,
    string? FileName,
    long? FileSize,
    string? DownloadUrl,
    DateTime? GeneratedAt);

/// <summary>
/// Body for <c>POST /leases/{id}/send-for-signature</c>. Signer override fields are retained for
/// request compatibility, but the lease workflow sends to the tenant(s) tied to the lease.
/// </summary>
public sealed class SendForSignatureRequest
{
    [MaxLength(200)]
    public string? SignerName { get; set; }

    [EmailAddress]
    [MaxLength(256)]
    public string? SignerEmail { get; set; }
}

/// <summary>Outcome of a send-for-signature attempt, mapped to HTTP by the controller.</summary>
public sealed class SendForSignatureResult
{
    public SendForSignatureOutcome Outcome { get; init; }
    public LeaseSignatureStatusResponse? Status { get; init; }

    /// <summary>Human-readable explanation (used for the not-configured/error responses).</summary>
    public string? Error { get; init; }

    public static SendForSignatureResult NotFound()
        => new() { Outcome = SendForSignatureOutcome.NotFound, Error = "Lease not found" };

    public static SendForSignatureResult NotConfigured()
        => new() { Outcome = SendForSignatureOutcome.NotConfigured, Error = "E-sign provider is not configured." };

    public static SendForSignatureResult MissingSigner(string? error = null)
        => new()
        {
            Outcome = SendForSignatureOutcome.MissingSigner,
            Error = string.IsNullOrWhiteSpace(error)
                ? "A signer name and email are required (the lease's tenant has none)."
                : error,
        };

    public static SendForSignatureResult AlreadyFinalized()
        => new() { Outcome = SendForSignatureOutcome.AlreadyFinalized, Error = "This lease is already signed/active; sending it again would discard the executed agreement." };

    public static SendForSignatureResult ProviderError(string error)
        => new() { Outcome = SendForSignatureOutcome.ProviderError, Error = error };

    public static SendForSignatureResult Sent(LeaseSignatureStatusResponse status)
        => new() { Outcome = SendForSignatureOutcome.Sent, Status = status };
}

public enum SendForSignatureOutcome
{
    Sent,
    NotFound,
    NotConfigured,
    MissingSigner,
    ProviderError,

    /// <summary>The lease is already signed/active (or has an executed document); re-sending is refused.</summary>
    AlreadyFinalized
}

/// <summary>Signature-workflow snapshot for a lease, returned by the status endpoint and after sending.</summary>
public sealed class LeaseSignatureStatusResponse
{
    public int LeaseId { get; init; }

    /// <summary>Where the lease sits in the e-sign workflow: None | Sent | Signed | Declined (string on the wire).</summary>
    public EsignStatus EsignStatus { get; init; }

    /// <summary>The lease's overall status (e.g. PendingSignature, Active) — string on the wire.</summary>
    public LeaseStatus LeaseStatus { get; init; }

    /// <summary>Provider envelope/signature-request id once sent; null otherwise.</summary>
    public string? EnvelopeId { get; init; }

    /// <summary>True when a fully-signed PDF is stored and downloadable.</summary>
    public bool HasSignedDocument { get; init; }

    /// <summary>Stable selector for frontend tests.</summary>
    public string TestId => $"lease-signature-{LeaseId}";
}

/// <summary>Recent outbox activity for lease e-sign emails.</summary>
public sealed class LeaseSignatureQueueResponse
{
    public int LeaseId { get; init; }
    public IReadOnlyList<LeaseSignatureQueueItemResponse> Items { get; init; } = [];
}

/// <summary>One lease e-sign email queue item, scoped to a single lease.</summary>
public sealed class LeaseSignatureQueueItemResponse
{
    public long Id { get; init; }
    public string RecipientEmail { get; init; } = string.Empty;
    public int? TenantId { get; init; }
    public string? Subject { get; init; }
    public string Status { get; init; } = "Queued";
    public DateTime QueuedAt { get; init; }
    public DateTime StatusAt { get; init; }
    public DateTime? SentAt { get; init; }
    public DateTime? FailedAt { get; init; }
    public int RetryCount { get; init; }
    public string? Error { get; init; }
    public string? SignatureRequestId { get; init; }

    /// <summary>
    /// Authenticated landlord fallback for local/demo delivery-disabled sends. Null when email was delivered,
    /// the request is no longer active, or the signer token cannot be resolved.
    /// </summary>
    public string? SigningUrl { get; init; }
}

public class CreateLeaseRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PropertyId { get; set; }

    [Required]
    [Range(1, int.MaxValue)]
    public int UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    public IReadOnlyList<int>? TenantIds { get; set; }

    [MaxLength(100)]
    public string? LeaseNumber { get; set; }

    [EnumDataType(typeof(LeaseStatus))]
    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;

    [Required]
    public DateTime StartDate { get; set; }

    [Required]
    public DateTime EndDate { get; set; }

    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }

    [Range(0.01, 99999999)]
    public decimal MonthlyRent { get; set; }

    [Range(0, 99999999)]
    public decimal SecurityDeposit { get; set; }

    [Range(0, 99999999)]
    public decimal LateFeeAmount { get; set; }

    [Range(1, 31)]
    public int RentDueDay { get; set; } = 1;

    [EnumDataType(typeof(RentTrackingStartMode))]
    public RentTrackingStartMode RentTrackingStartMode { get; set; } = RentTrackingStartMode.BackfillFromLeaseStart;

    public DateTime? RentTrackingStartDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Full scan-extraction superset JSON (jsonb); populated when importing from a scanned PDF.</summary>
    public string? ExtractedData { get; set; }
}

public class UpdateLeaseRequest
{
    [Range(1, int.MaxValue)]
    public int? TenantId { get; set; }

    public IReadOnlyList<int>? TenantIds { get; set; }

    [MaxLength(100)]
    public string? LeaseNumber { get; set; }

    [EnumDataType(typeof(LeaseStatus))]
    public LeaseStatus? Status { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? MoveInDate { get; set; }
    public DateTime? MoveOutDate { get; set; }

    [Range(0.01, 99999999)]
    public decimal? MonthlyRent { get; set; }

    [Range(0, 99999999)]
    public decimal? SecurityDeposit { get; set; }

    [Range(0, 99999999)]
    public decimal? LateFeeAmount { get; set; }

    [Range(1, 31)]
    public int? RentDueDay { get; set; }

    [EnumDataType(typeof(RentTrackingStartMode))]
    public RentTrackingStartMode? RentTrackingStartMode { get; set; }

    public DateTime? RentTrackingStartDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
