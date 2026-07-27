using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RentalCommand.Core.Scanning;

public enum ScanConfirmationTargetKind
{
    Expense,
    Payment,
    WorkOrder,
    LeaseAgreement,
    Application,
    Loan,
}

public sealed record ScanReceiptLineData(
    string? Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount) : IAtomicCommandData;

/// <summary>Complete reviewed receipt/check facts shared by expense and payment confirmation.</summary>
public sealed record ScanReceiptData(
    string? VendorName,
    string? VendorAddress,
    string? VendorPhone,
    string? VendorWebsite,
    string? VendorTaxId,
    string? ReceiptNumber,
    DateTime? TransactionDate,
    decimal? Subtotal,
    decimal? Tax,
    decimal? TaxRate,
    decimal? Tip,
    decimal? Discount,
    decimal? Shipping,
    decimal? Total,
    string? PaymentMethod,
    string? CardLast4,
    ScheduleECategory? Category,
    string? DocumentKind,
    string? Notes,
    DateTime? DueDate,
    IReadOnlyList<ScanReceiptLineData> LineItems,
    string? PayerName,
    string? CheckNumber,
    string? BankName,
    IReadOnlyList<ScanExtraFieldData> ExtraFields) : IAtomicCommandData;

public sealed record ScanExtraFieldData(string Name, string Value) : IAtomicCommandData;

public enum LeaseScanReviewDisposition
{
    AlreadyFullySigned = 1,
    NeedsSignatures = 2,
}

public sealed record ScanExpenseTargetData(
    ScanReceiptData Receipt,
    bool IsPaid,
    int? PropertyId,
    int? UnitId,
    int? WorkOrderId) : IAtomicCommandData;

public sealed record ScanPaymentTargetData(
    ScanReceiptData Receipt,
    int TenantAccountId,
    long? TenantLedgerEntryId = null) : IAtomicCommandData;

public sealed record ScanWorkOrderTargetData(
    int PropertyId,
    int? UnitId,
    int? TenantId,
    int? LeaseManagementId,
    int? VendorId,
    string? Title,
    string? Description,
    string? Category,
    WorkOrderPriority Priority,
    decimal? EstimatedCost) : IAtomicCommandData;

public sealed record ScanLeaseTargetData(
    int PropertyId,
    int? UnitId,
    int? TenantId,
    string? TenantName,
    string? TenantEmail,
    string? TenantPhone,
    string? TenantEmergencyContact,
    string? PropertyName,
    string? PropertyType,
    RentalStructure? RentalStructure,
    string? PropertyAddress,
    string? PropertyCity,
    string? PropertyState,
    string? PropertyPostalCode,
    string? UnitNumber,
    decimal? UnitBedrooms,
    decimal? UnitBathrooms,
    int? UnitSquareFeet,
    string? LeaseNumber,
    DateTime? StartDate,
    DateTime? EndDate,
    decimal? MonthlyRent,
    decimal? SecurityDeposit,
    decimal? LateFee,
    int? RentDueDay,
    LeaseScanReviewDisposition? ReviewDisposition = null,
    int? LeaseManagementId = null,
    int? TenantAccountId = null,
    int? LeaseAgreementId = null,
    int? DocumentTemplateId = null,
    int TermsSchemaVersion = 1,
    string? TermsPayload = null,
    short GracePeriodDays = 0,
    DateTime? PossessionGivenAtUtc = null,
    RentTrackingStartMode RentTrackingStartMode = RentTrackingStartMode.ForwardOnly,
    DateOnly? RentTrackingStartOn = null) : IAtomicCommandData;

public sealed record ScanApplicationTargetData(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Phone,
    DateTime? DateOfBirth,
    string? CurrentAddress,
    string? Employer,
    decimal? MonthlyIncome,
    DateTime? DesiredMoveInDate,
    string? ApplyingFor,
    string? IdLast4,
    string? CoSignerName,
    string? Notes,
    int? PropertyId,
    int? UnitId) : IAtomicCommandData;

public sealed record ScanLoanTargetData(
    int PropertyId,
    string? Lender,
    decimal? OriginalAmount,
    decimal? CurrentBalance,
    decimal? AnnualInterestRatePct,
    int? TermMonths,
    DateTime? StartDate,
    int? DayOfMonthDue,
    decimal? MonthlyPrincipalInterest,
    decimal? MonthlyEscrow,
    bool? EscrowCoversTaxes,
    bool? EscrowCoversInsurance,
    string? Notes) : IAtomicCommandData;

/// <summary>
/// Sealed discriminated envelope accepted by the atomic admission validator. Exactly the member named
/// by <see cref="Kind"/> must be present; no raw request JSON or API-layer type crosses the boundary.
/// </summary>
public sealed record ScanConfirmationTargetData(
    ScanConfirmationTargetKind Kind,
    ScanExpenseTargetData? Expense = null,
    ScanPaymentTargetData? Payment = null,
    ScanWorkOrderTargetData? WorkOrder = null,
    ScanLeaseTargetData? LeaseAgreement = null,
    ScanApplicationTargetData? Application = null,
    ScanLoanTargetData? Loan = null) : IAtomicCommandData
{
    public void Validate()
    {
        var populated = (Expense is null ? 0 : 1)
            + (Payment is null ? 0 : 1)
            + (WorkOrder is null ? 0 : 1)
            + (LeaseAgreement is null ? 0 : 1)
            + (Application is null ? 0 : 1)
            + (Loan is null ? 0 : 1);
        var selectedIsPresent = Kind switch
        {
            ScanConfirmationTargetKind.Expense => Expense is not null,
            ScanConfirmationTargetKind.Payment => Payment is not null,
            ScanConfirmationTargetKind.WorkOrder => WorkOrder is not null,
            ScanConfirmationTargetKind.LeaseAgreement => LeaseAgreement is not null,
            ScanConfirmationTargetKind.Application => Application is not null,
            ScanConfirmationTargetKind.Loan => Loan is not null,
            _ => false,
        };
        if (populated != 1 || !selectedIsPresent)
        {
            throw new ArgumentException("Exactly the target payload selected by Kind must be supplied.");
        }
    }

    public string EntityType => Kind.ToString();
}

public sealed record ConfirmScanDraftCommand(
    int PortfolioId,
    int DraftId,
    int ConfirmedByUserId,
    DateTime ConfirmedAtUtc,
    string ExpectedDraftFingerprint,
    ScanConfirmationTargetData Target,
    int? SourceStoredFileId = null,
    Guid AuthSessionId = default,
    int AccessContextId = 0,
    long ExpectedAccessRevision = 0,
    string DeliveryIdempotencyKey = "",
    string? SourceContentSha256 = null,
    string? SourceLabel = null,
    ScanCaptureContextData? CaptureContext = null) : IAtomicCommandData;

/// <summary>
/// Stable version token for the exact draft facts used to prepare a confirmation command. The
/// length-prefixed binary encoding distinguishes null from empty and prevents field-boundary
/// ambiguity; callers compare the SHA-256 digest after acquiring the draft's transaction lock.
/// </summary>
public static class ScanConfirmationDraftFingerprint
{
    private const int EncodingVersion = 4;

    public static string Create(
        string targetEntityType,
        int? sourceStoredFileId,
        string? extractedFields,
        string? sourceContentSha256 = null,
        int? captureAccessContextId = null,
        long? captureAccessRevision = null,
        int? capturePropertyId = null,
        int? captureUnitId = null,
        int? captureLeaseManagementId = null,
        int? captureLeaseAgreementId = null,
        int? captureTenantAccountId = null,
        long? captureTenantLedgerEntryId = null,
        int? captureWorkOrderId = null,
        int? captureApplicationId = null,
        int? captureRentalListingId = null,
        string? sourceLabel = null)
    {
        ArgumentNullException.ThrowIfNull(targetEntityType);

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(EncodingVersion);
            WriteNullableString(writer, targetEntityType);
            writer.Write(sourceStoredFileId.HasValue);
            if (sourceStoredFileId.HasValue)
                writer.Write(sourceStoredFileId.Value);
            WriteNullableString(writer, CanonicalizeJson(extractedFields));
            WriteNullableString(writer, sourceContentSha256);
            WriteNullableInt32(writer, captureAccessContextId);
            WriteNullableInt64(writer, captureAccessRevision);
            WriteNullableInt32(writer, capturePropertyId);
            WriteNullableInt32(writer, captureUnitId);
            WriteNullableInt32(writer, captureLeaseManagementId);
            WriteNullableInt32(writer, captureLeaseAgreementId);
            WriteNullableInt32(writer, captureTenantAccountId);
            WriteNullableInt64(writer, captureTenantLedgerEntryId);
            WriteNullableInt32(writer, captureWorkOrderId);
            WriteNullableInt32(writer, captureApplicationId);
            WriteNullableInt32(writer, captureRentalListingId);
            WriteNullableString(writer, sourceLabel);
        }

        return Convert.ToHexStringLower(SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }

    private static string? CanonicalizeJson(string? value)
    {
        if (value is null)
            return null;

        using var document = JsonDocument.Parse(value);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
            WriteCanonicalJson(writer, document.RootElement);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

/// <summary>One receipt identity per caller operation, scoped to its portfolio-owned draft.</summary>
public static class ScanConfirmationCommandIdentity
{
    public static AtomicCommandIdentity Create(int portfolioId, int draftId, string clientOperationId)
    {
        if (portfolioId <= 0 || draftId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(draftId), "A persisted portfolio and scan draft are required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOperationId);
        var normalized = clientOperationId.Trim();
        if (normalized.Length > 160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientOperationId),
                "Client operation id cannot exceed 160 characters.");
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return new AtomicCommandIdentity("scan.confirm", $"{portfolioId}:{draftId}:{digest}");
    }
}

public enum ConfirmScanDraftOutcome
{
    Confirmed,
    AlreadyConfirmed,
    DraftNotFound,
    DraftNotReady,
    DraftRejected,
    TargetMismatch,
    UnsupportedTarget,
    DuplicateSourceContent,
}

public sealed record ConfirmScanDraftResult(
    ConfirmScanDraftOutcome Outcome,
    int DraftId,
    string TargetEntityType,
    int? TargetEntityId,
    int? UnitId = null,
    string? Error = null,
    long? LedgerEntryId = null,
    int? LeaseManagementId = null) : IAtomicResultData;

/// <summary>Internal writer result; the handler turns it into the stable receipt result contract.</summary>
public sealed record ScanConfirmationTargetWriteResult(
    int EntityId,
    int? UnitId = null,
    string? CanonicalEntityType = null,
    long? LedgerEntryId = null,
    int? LeaseManagementId = null,
    bool TargetAuditRecorded = false);

/// <summary>
/// Transaction-only target seam. Implementations must be sealed, data/persistence-only atomic
/// dependencies; remote work and broadcasting are forbidden and represented by durable outbox intent.
/// </summary>
public interface IScanConfirmationTargetWriter : IAtomicTransactionSafeDependency
{
    bool Supports(ScanConfirmationTargetKind kind);

    Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        string? extractedFieldsJson,
        IAtomicWriteAttempt attempt,
        CancellationToken ct);

    Task AuthorizeReplayAsync(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) => Task.CompletedTask;
}
