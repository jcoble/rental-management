using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Leasing;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Policies;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Persistence-only writers for scan targets. Every query and write goes through the exact scoped
/// database context enlisted in the atomic transaction. Lease scans delegate to the canonical
/// relationship/account/agreement writer.
/// </summary>
public sealed class ProductionScanConfirmationTargetWriter : IScanConfirmationTargetWriter
{
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web);
    private const decimal PaidStatementComponentCorrectionTolerance = 1.00m;
    private readonly RentalCommandDbContext _db;

    public ProductionScanConfirmationTargetWriter(RentalCommandDbContext db) => _db = db;

    public bool Supports(ScanConfirmationTargetKind kind) => kind is
        ScanConfirmationTargetKind.Expense or
        ScanConfirmationTargetKind.Payment or
        ScanConfirmationTargetKind.WorkOrder or
        ScanConfirmationTargetKind.LeaseAgreement or
        ScanConfirmationTargetKind.Application or
        ScanConfirmationTargetKind.Loan or
        ScanConfirmationTargetKind.PropertyAcquisition or
        ScanConfirmationTargetKind.LeaseEndingNotice;

    public Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct) => command.Target.Kind switch
        {
            ScanConfirmationTargetKind.Expense => WriteExpenseAsync(
                command, Required(command.Target.Expense), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.Payment => WritePaymentAsync(
                command, Required(command.Target.Payment), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.WorkOrder => WriteWorkOrderAsync(
                command, Required(command.Target.WorkOrder), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.LeaseAgreement => CanonicalLeaseScanConfirmationWriter.WriteAsync(
                command, Required(command.Target.LeaseAgreement), _db, context, ct),
            ScanConfirmationTargetKind.Application => WriteApplicationAsync(
                command, Required(command.Target.Application), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.Loan => WriteLoanAsync(
                command, Required(command.Target.Loan), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.PropertyAcquisition => WritePropertyAcquisitionAsync(
                command, Required(command.Target.PropertyAcquisition), extractedFieldsJson, context, ct),
            ScanConfirmationTargetKind.LeaseEndingNotice => WriteLeaseEndingNoticeAsync(
                command, Required(command.Target.LeaseEndingNotice), extractedFieldsJson, context, ct),
            _ => throw new InvalidOperationException(
                $"Scan confirmation target {command.Target.Kind} is not supported by this writer."),
        };

    public async Task AuthorizeReplayAsync(
        ConfirmScanDraftCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        command.Target.Validate();
        var capabilities = RequiredCapabilities(command.Target.Kind);
        if (command.Target.Kind == ScanConfirmationTargetKind.Expense &&
            command.Target.Expense?.WorkOrderId is int workOrderId)
            await WorkOrderProgressionLock.AcquireAsync(context, ct, workOrderId);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await IsDraftAuthorizedAsync(command, capabilities, _db, securityNowUtc, ct))
            throw Unauthorized();

        if (command.Target.Kind == ScanConfirmationTargetKind.LeaseAgreement)
        {
            await CanonicalLeaseScanConfirmationWriter.AuthorizeAsync(command, _db, ct);
            return;
        }

        var propertyId = await ResolveTargetPropertyIdAsync(command, _db, ct);
        if (!await HasPropertyAuthorityAsync(
                command, capabilities, propertyId, _db, securityNowUtc, ct))
            throw Unauthorized();
    }

    private static IReadOnlyCollection<string> RequiredCapabilities(ScanConfirmationTargetKind kind) => kind switch
    {
        ScanConfirmationTargetKind.Expense => [CapabilityKeys.MoneyExpensesManage],
        ScanConfirmationTargetKind.Payment => [CapabilityKeys.MoneyPaymentsManage],
        ScanConfirmationTargetKind.WorkOrder => [CapabilityKeys.WorkManage],
        ScanConfirmationTargetKind.LeaseAgreement =>
            [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingAgreementsPrepare],
        ScanConfirmationTargetKind.Application => [CapabilityKeys.LeasingApplicationsManage],
        ScanConfirmationTargetKind.Loan => [CapabilityKeys.MoneyExpensesManage],
        ScanConfirmationTargetKind.PropertyAcquisition => [CapabilityKeys.RentalsManage],
        ScanConfirmationTargetKind.LeaseEndingNotice => [CapabilityKeys.RentalsManage],
        _ => [],
    };

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var keys = capabilities.Distinct(StringComparer.Ordinal).ToArray();
        return db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == command.PortfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc)
            && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
            && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
            && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
            && assignment.WorkspaceMembership.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.RevokedAtUtc == null
            && assignment.WorkspaceMembership.EffectiveFromUtc <= securityNowUtc
            && (assignment.WorkspaceMembership.EffectiveToUtc == null
                || assignment.WorkspaceMembership.EffectiveToUtc > securityNowUtc)
            && assignment.WorkspaceMembership.AccessContext!.UserId == command.ConfirmedByUserId
            && assignment.WorkspaceMembership.AccessContext.AccessRevision == command.ExpectedAccessRevision
            && assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active
            && assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null
            && assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null
            && db.Set<AuthSession>().Any(session =>
                session.Id == command.AuthSessionId && session.UserId == command.ConfirmedByUserId
                && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                keys.Contains(profileCapability.CapabilityDefinition!.Key)
                && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                    == CapabilityAuthorizationTargetKind.Property));
    }

    private static IQueryable<Property> AuthorizedProperties(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var assignments = EffectiveAssignments(command, capabilities, db, securityNowUtc);
        return db.Set<Property>().Where(property =>
            property.PortfolioId == command.PortfolioId
            && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == command.PortfolioId
                        && selected.PropertyId == property.Id))));
    }

    private static Task<bool> IsDraftAuthorizedAsync(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(command, capabilities, db, securityNowUtc);
        var assignments = EffectiveAssignments(command, capabilities, db, securityNowUtc);
        var allProperties = assignments.Where(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);

        return db.Set<ScanDraft>().AnyAsync(draft =>
            draft.Id == command.DraftId && draft.PortfolioId == command.PortfolioId
            && (((draft.CapturePropertyId != null || draft.CaptureUnitId != null
                    || draft.CaptureLeaseManagementId != null || draft.CaptureLeaseAgreementId != null
                    || draft.CaptureTenantAccountId != null || draft.CaptureTenantLedgerEntryId != null
                    || draft.CaptureWorkOrderId != null || draft.CaptureApplicationId != null
                    || draft.CaptureRentalListingId != null)
                && (draft.CapturePropertyId == null || authorizedProperties.Any(property =>
                    property.Id == draft.CapturePropertyId))
                && (draft.CaptureUnitId == null || db.Set<Unit>().Any(unit =>
                    unit.Id == draft.CaptureUnitId && unit.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == unit.PropertyId)))
                && (draft.CaptureLeaseManagementId == null || db.Set<LeaseManagement>().Any(management =>
                    management.Id == draft.CaptureLeaseManagementId && management.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == management.PropertyId)))
                && (draft.CaptureLeaseAgreementId == null || db.Set<LeaseAgreement>().Any(agreement =>
                    agreement.Id == draft.CaptureLeaseAgreementId && agreement.PortfolioId == draft.PortfolioId
                    && agreement.LeaseManagement != null
                    && authorizedProperties.Any(property => property.Id == agreement.LeaseManagement.PropertyId)))
                && (draft.CaptureTenantAccountId == null || db.Set<TenantAccount>().Any(account =>
                    account.Id == draft.CaptureTenantAccountId && account.PortfolioId == draft.PortfolioId
                    && account.LeaseManagement != null
                    && authorizedProperties.Any(property => property.Id == account.LeaseManagement.PropertyId)))
                && (draft.CaptureTenantLedgerEntryId == null || db.Set<TenantLedgerEntry>().Any(entry =>
                    entry.Id == draft.CaptureTenantLedgerEntryId && entry.PortfolioId == draft.PortfolioId
                    && entry.TenantAccount != null && entry.TenantAccount.LeaseManagement != null
                    && authorizedProperties.Any(property =>
                        property.Id == entry.TenantAccount.LeaseManagement.PropertyId)))
                && (draft.CaptureWorkOrderId == null || db.Set<WorkOrder>().Any(order =>
                    order.Id == draft.CaptureWorkOrderId && order.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == order.PropertyId)))
                && (draft.CaptureApplicationId == null || db.Set<RentalApplication>().Any(application =>
                    application.Id == draft.CaptureApplicationId && application.PortfolioId == draft.PortfolioId
                    && application.PropertyId != null
                    && authorizedProperties.Any(property => property.Id == application.PropertyId)))
                && (draft.CaptureRentalListingId == null || db.Set<RentalListing>().Any(listing =>
                    listing.Id == draft.CaptureRentalListingId && listing.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == listing.PropertyId))))
                || ((draft.CapturePropertyId == null && draft.CaptureUnitId == null
                    && draft.CaptureLeaseManagementId == null && draft.CaptureLeaseAgreementId == null
                    && draft.CaptureTenantAccountId == null && draft.CaptureTenantLedgerEntryId == null
                    && draft.CaptureWorkOrderId == null && draft.CaptureApplicationId == null
                    && draft.CaptureRentalListingId == null)
                    && ((draft.CaptureAccessContextId == command.AccessContextId && assignments.Any())
                        || allProperties.Any()))), ct);
    }

    private static async Task<int?> ResolveTargetPropertyIdAsync(
        ConfirmScanDraftCommand command,
        RentalCommandDbContext db,
        CancellationToken ct) => command.Target.Kind switch
        {
            ScanConfirmationTargetKind.Expense => await ResolveExpensePropertyIdAsync(
                command, Required(command.Target.Expense), db, ct),
            ScanConfirmationTargetKind.Payment => await db.Set<TenantAccount>()
                .Where(account => account.Id == Required(command.Target.Payment).TenantAccountId
                    && account.PortfolioId == command.PortfolioId && account.LeaseManagement != null)
                .Select(account => (int?)account.LeaseManagement!.PropertyId)
                .SingleOrDefaultAsync(ct),
            ScanConfirmationTargetKind.WorkOrder => Required(command.Target.WorkOrder).PropertyId,
            ScanConfirmationTargetKind.Application => await ResolveOptionalPropertyIdAsync(
                command, Required(command.Target.Application).PropertyId,
                Required(command.Target.Application).UnitId, db, ct),
            ScanConfirmationTargetKind.Loan => Required(command.Target.Loan).PropertyId,
            ScanConfirmationTargetKind.PropertyAcquisition => Required(command.Target.PropertyAcquisition).PropertyId,
            ScanConfirmationTargetKind.LeaseEndingNotice => await db.Set<LeaseManagement>()
                .Where(relationship => relationship.Id == Required(command.Target.LeaseEndingNotice).LeaseManagementId
                    && relationship.PortfolioId == command.PortfolioId)
                .Select(relationship => (int?)relationship.PropertyId)
                .SingleOrDefaultAsync(ct),
            _ => null,
        };

    private static async Task<int?> ResolveExpensePropertyIdAsync(
        ConfirmScanDraftCommand command,
        ScanExpenseTargetData target,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (target.PropertyId is int propertyId)
            return propertyId;
        if (target.UnitId is int unitId)
            return await db.Set<Unit>()
                .Where(unit => unit.Id == unitId && unit.PortfolioId == command.PortfolioId)
                .Select(unit => (int?)unit.PropertyId).SingleOrDefaultAsync(ct);
        if (target.WorkOrderId is int workOrderId)
            return await db.Set<WorkOrder>()
                .Where(order => order.Id == workOrderId && order.PortfolioId == command.PortfolioId)
                .Select(order => (int?)order.PropertyId).SingleOrDefaultAsync(ct);
        return null;
    }

    private static async Task<int?> ResolveOptionalPropertyIdAsync(
        ConfirmScanDraftCommand command,
        int? propertyId,
        int? unitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (propertyId is not null)
            return propertyId;
        return unitId is int selectedUnitId
            ? await db.Set<Unit>()
                .Where(unit => unit.Id == selectedUnitId && unit.PortfolioId == command.PortfolioId)
                .Select(unit => (int?)unit.PropertyId).SingleOrDefaultAsync(ct)
            : null;
    }

    private static Task<bool> HasPropertyAuthorityAsync(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        int? propertyId,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var assignments = EffectiveAssignments(command, capabilities, db, securityNowUtc);
        return propertyId is int selectedPropertyId
            ? AuthorizedProperties(command, capabilities, db, securityNowUtc)
                .AnyAsync(property => property.Id == selectedPropertyId, ct)
            : assignments.AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
    }

    private static UnauthorizedAccessException Unauthorized() =>
        new("The current session is not authorized to confirm this scan draft.");

    private async Task<ScanConfirmationTargetWriteResult> WriteExpenseAsync(
        ConfirmScanDraftCommand command,
        ScanExpenseTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var receipt = Required(target.Receipt);
        var reviewed = RequireReviewedExpenseFacts(receipt, target);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, target.WorkOrderId);

        var location = await ResolveExpenseLocationAsync(command.PortfolioId, target, _db, ct);
        var vendorId = await ResolveOrCreateVendorAsync(
            command.PortfolioId, receipt, ToUtc(command.ConfirmedAtUtc), context, ct);
        var now = ToUtc(command.ConfirmedAtUtc);
        var expense = new Expense
        {
            PortfolioId = command.PortfolioId,
            OperationalScope = location.OperationalScope,
            PropertyId = location.PropertyId,
            UnitId = location.UnitId,
            WorkOrderId = location.WorkOrderId,
            VendorId = vendorId,
            Category = reviewed.Category,
            Description = reviewed.VendorName,
            Amount = reviewed.Amount,
            Subtotal = receipt.Subtotal,
            TaxAmount = receipt.Tax,
            IncurredAt = reviewed.TransactionDate,
            DueDate = ToUtc(receipt.DueDate),
            PaidAt = target.IsPaid ? reviewed.TransactionDate : null,
            Status = target.IsPaid ? ExpenseStatus.Paid : ExpenseStatus.Pending,
            BillableToOwner = false,
            Notes = receipt.Notes,
            ReceiptData = SerializeReceipt(receipt),
            PaymentMethod = receipt.PaymentMethod,
            CardLast4 = receipt.CardLast4,
            DocumentKind = receipt.DocumentKind,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var lineNumber = 1;
        foreach (var line in receipt.LineItems ?? [])
        {
            expense.LineItems.Add(new ExpenseLineItem
            {
                Description = line.Description ?? string.Empty,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = line.Amount,
                LineNumber = lineNumber++,
            });
        }
        AddDefaultExpenseAllocation(command.PortfolioId, expense, location, reviewed.Amount, now);

        _db.Add(expense);
        var flush = await context.FlushBusinessAsync(ct);
        EnrichCreatedTargetAudit(command, extractedFieldsJson, context, flush, expense);
        return new ScanConfirmationTargetWriteResult(expense.Id, expense.UnitId, TargetAuditRecorded: true);
    }

    private static ReviewedExpenseFacts RequireReviewedExpenseFacts(
        ScanReceiptData receipt,
        ScanExpenseTargetData target)
    {
        var missing = new List<string>();
        var vendorName = receipt.VendorName?.Trim();
        if (string.IsNullOrWhiteSpace(vendorName))
            missing.Add("vendor");
        var transactionDate = ToUtc(receipt.TransactionDate);
        if (transactionDate is null)
            missing.Add("transaction date");
        var amount = receipt.Total is > 0m
            ? receipt.Total.Value
            : receipt.Subtotal is > 0m
                ? receipt.Subtotal.Value
                : (decimal?)null;
        if (amount is null)
            missing.Add("positive total or subtotal");
        if (receipt.Category is null)
            missing.Add("category");
        if (target.PropertyId is not > 0 && target.UnitId is not > 0 && target.WorkOrderId is not > 0)
            missing.Add("property, unit, or work order");

        if (missing.Count > 0)
        {
            throw new ScanConfirmationValidationException(
                "Review " + string.Join(", ", missing) + " before creating this expense.");
        }

        return new ReviewedExpenseFacts(
            vendorName!,
            transactionDate!.Value,
            amount!.Value,
            receipt.Category!.Value);
    }

    private sealed record ReviewedExpenseFacts(
        string VendorName,
        DateTime TransactionDate,
        decimal Amount,
        ScheduleECategory Category);

    private sealed record ReviewedLoanStatementFacts(
        decimal OpeningBalance,
        decimal PrincipalAmount,
        decimal InterestAmount,
        decimal EscrowAmount,
        decimal TotalAmount,
        decimal BalanceAfter,
        DateTime EffectiveDate);

    private static void AddDefaultExpenseAllocation(
        int portfolioId,
        Expense expense,
        ExpenseLocationContext location,
        decimal amount,
        DateTime now)
    {
        if (location.UnitId is int unitId)
        {
            expense.Allocations.Add(new ExpenseAllocation
            {
                PortfolioId = portfolioId,
                TargetKind = ExpenseAllocationTargetKind.Unit,
                UnitId = unitId,
                Amount = amount,
                CreatedAt = now,
            });
            return;
        }

        if (location.PropertyId is int propertyId)
        {
            expense.Allocations.Add(new ExpenseAllocation
            {
                PortfolioId = portfolioId,
                TargetKind = ExpenseAllocationTargetKind.Property,
                PropertyId = propertyId,
                Amount = amount,
                CreatedAt = now,
            });
        }
    }

    private async Task<ScanConfirmationTargetWriteResult> WritePaymentAsync(
        ConfirmScanDraftCommand command,
        ScanPaymentTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var receipt = Required(target.Receipt);
        var amount = receipt.Total ?? receipt.Subtotal ?? 0m;
        if (amount <= 0m)
        {
            throw new ScanConfirmationValidationException("Confirmed payment amount must be greater than zero.");
        }

        var accountContext = await _db.Set<TenantAccount>()
            .Where(account => account.Id == target.TenantAccountId
                && account.PortfolioId == command.PortfolioId)
            .Select(account => new
            {
                UnitId = (int?)account.LeaseManagement!.UnitId,
                ContextEntryIsValid = target.TenantLedgerEntryId == null
                    || _db.Set<TenantLedgerEntry>().Any(entry =>
                        entry.Id == target.TenantLedgerEntryId.Value
                        && entry.PortfolioId == command.PortfolioId
                        && entry.TenantAccountId == account.Id),
            })
            .SingleOrDefaultAsync(ct);
        if (accountContext?.UnitId is null)
        {
            throw new ScanConfirmationValidationException("Selected tenant account is not in this portfolio.");
        }
        if (!accountContext.ContextEntryIsValid)
        {
            throw new ScanConfirmationValidationException(
                "The selected ledger entry does not belong to this rental account.");
        }

        var paymentDate = ToUtc(receipt.TransactionDate) ?? ToUtc(command.ConfirmedAtUtc);
        var noteParts = new[] { receipt.PayerName, receipt.Notes }
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var notes = string.Join(" — ", noteParts);
        var receiptCommand = new RecordTenantReceiptCommand(
            command.PortfolioId,
            target.TenantAccountId,
            amount,
            DateOnly.FromDateTime(paymentDate),
            string.IsNullOrWhiteSpace(notes) ? "Scanned tenant payment" : notes,
            receipt.PaymentMethod ?? "Scanned check",
            string.IsNullOrWhiteSpace(receipt.CheckNumber)
                ? $"scan:{command.DraftId}"
                : receipt.CheckNumber.Trim(),
            receipt.PayerName,
            receipt.CheckNumber,
            receipt.BankName,
            command.SourceStoredFileId,
            target.TenantLedgerEntryId,
            command.ConfirmedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            CapabilityKeys.MoneyPaymentsManage,
            $"scan-receipt:{command.DraftId}",
            command.DeliveryIdempotencyKey,
            ToUtc(command.ConfirmedAtUtc))
        {
            AllocateOldestCharges = target.TenantLedgerEntryId is null,
        };
        var result = await new RecordTenantReceiptHandler(_db).HandleAsync(receiptCommand, context, ct);
        return new ScanConfirmationTargetWriteResult(
            target.TenantAccountId,
            accountContext.UnitId,
            nameof(TenantAccount),
            result.LedgerEntryId);
    }

    private async Task<ScanConfirmationTargetWriteResult> WriteWorkOrderAsync(
        ConfirmScanDraftCommand command,
        ScanWorkOrderTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var title = RequireText(target.Title, "Work order title");
        var description = RequireText(target.Description, "Work order description");
        await ValidateWorkOrderReferencesAsync(command.PortfolioId, target, _db, ct);

        var workOrder = new WorkOrder
        {
            PortfolioId = command.PortfolioId,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId,
            TenantId = target.TenantId,
            LeaseManagementId = target.LeaseManagementId,
            VendorId = target.VendorId,
            Title = title,
            Description = description,
            TechnicianAccessInstructions = Clean(target.TechnicianAccessInstructions),
            RequesterName = Clean(target.RequesterName),
            RequesterPhone = Clean(target.RequesterPhone),
            RequesterEmail = Clean(target.RequesterEmail),
            ResidentMustBePresent = target.ResidentMustBePresent,
            CallBeforeEntry = target.CallBeforeEntry,
            CallIfNotHome = target.CallIfNotHome,
            PermissionToEnter = target.PermissionToEnter,
            EntryNotes = Clean(target.EntryNotes),
            PetWarnings = Clean(target.PetWarnings),
            AccessWarnings = Clean(target.AccessWarnings),
            Category = string.IsNullOrWhiteSpace(target.Category) ? "General" : target.Category.Trim(),
            Priority = target.Priority,
            Status = WorkOrderStatus.New,
            RequestedAt = ToUtc(command.ConfirmedAtUtc),
            EstimatedCost = target.EstimatedCost,
            CreatedBy = command.ConfirmedByUserId.ToString(),
            ExtractedData = NormalizeJson(extractedFieldsJson),
            UpdatedAt = ToUtc(command.ConfirmedAtUtc),
        };
        workOrder.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            FromStatus = null,
            ToStatus = WorkOrderStatus.New,
            ChangedByUserId = command.ConfirmedByUserId,
            ChangedByLabel = "Staff",
            CreatedAtUtc = ToUtc(command.ConfirmedAtUtc),
        });

        _db.Add(workOrder);
        var flush = await context.FlushBusinessAsync(ct);
        EnrichCreatedTargetAudit(command, extractedFieldsJson, context, flush, workOrder);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            command.PortfolioId,
            nameof(WorkOrder),
            workOrder.Id,
            $"scan-work-order-create:{command.DeliveryIdempotencyKey}",
            ToUtc(command.ConfirmedAtUtc)));
        return new ScanConfirmationTargetWriteResult(workOrder.Id, workOrder.UnitId, TargetAuditRecorded: true);
    }

    private async Task<ScanConfirmationTargetWriteResult> WriteApplicationAsync(
        ConfirmScanDraftCommand command,
        ScanApplicationTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var firstName = RequireText(target.FirstName, "Applicant first name");
        var lastName = RequireText(target.LastName, "Applicant last name");
        await ValidateOptionalPropertyUnitAsync(
            command.PortfolioId, target.PropertyId, target.UnitId, _db, ct);

        var normalizedEmail = NormalizeEmail(target.Email);
        if (normalizedEmail is not null)
        {
            var existingOpenApplicationId = await _db.Set<RentalApplication>()
                .OpenForEmail(command.PortfolioId, normalizedEmail)
                .OrderBy(application => application.Id)
                .Select(application => (int?)application.Id)
                .FirstOrDefaultAsync(ct);
            if (existingOpenApplicationId is not null)
            {
                throw new ScanConfirmationValidationException(
                    $"An application for {normalizedEmail} already exists as application #{existingOpenApplicationId}.");
            }
        }

        var application = new RentalApplication
        {
            PortfolioId = command.PortfolioId,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId,
            FirstName = firstName,
            LastName = lastName,
            Email = string.IsNullOrWhiteSpace(target.Email) ? null : target.Email.Trim(),
            Phone = target.Phone,
            DateOfBirth = ToUtc(target.DateOfBirth),
            CurrentAddress = string.IsNullOrWhiteSpace(target.CurrentAddress)
                ? null
                : target.CurrentAddress.Trim(),
            Employer = target.Employer,
            MonthlyIncome = target.MonthlyIncome,
            DesiredMoveInDate = ToUtc(target.DesiredMoveInDate),
            Notes = BuildApplicationNotes(target),
            IdExtractedFields = NormalizeJson(extractedFieldsJson),
            ConsentGiven = false,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = ToUtc(command.ConfirmedAtUtc),
            CreatedAt = ToUtc(command.ConfirmedAtUtc),
            UpdatedAt = ToUtc(command.ConfirmedAtUtc),
        };

        _db.Add(application);
        var flush = await context.FlushBusinessAsync(ct);
        EnrichCreatedTargetAudit(command, extractedFieldsJson, context, flush, application);
        return new ScanConfirmationTargetWriteResult(application.Id, application.UnitId, TargetAuditRecorded: true);
    }

    private async Task<ScanConfirmationTargetWriteResult> WriteLoanAsync(
        ConfirmScanDraftCommand command,
        ScanLoanTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (target.ExistingLoanId.HasValue != target.ExistingLoanPaymentId.HasValue)
        {
            throw new ScanConfirmationValidationException(
                "Select both an existing loan and an existing scheduled payment to match this scan.");
        }
        if (target.ExistingLoanId.HasValue)
        {
            return await MatchExistingLoanPaymentAsync(command, target, context, ct);
        }

        if (!await IsPropertyInPortfolioAsync(
                command.PortfolioId, target.PropertyId, _db, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }
        await RejectDuplicateActiveLoanSourceAsync(command, target, _db, ct);

        var lender = RequireText(target.Lender, "Lender");
        var originalAmount = NormalizeDecimal(target.OriginalAmount, "Original loan amount", 0m, 999_999_999m, 2) ?? 0m;
        var currentBalance = NormalizeDecimal(target.CurrentBalance, "Current loan balance", 0m, 999_999_999m, 2);
        var interestRate = NormalizeDecimal(target.AnnualInterestRatePct, "Annual interest rate", 0m, 30m, 4) ?? 0m;
        var monthlyPrincipalInterest = NormalizeDecimal(
            target.MonthlyPrincipalInterest, "Monthly principal and interest", 0m, 9_999_999m, 2) ?? 0m;
        var monthlyEscrow = NormalizeDecimal(
            target.MonthlyEscrow, "Monthly escrow", 0m, 9_999_999m, 2) ?? 0m;
        var loan = new Loan
        {
            PortfolioId = command.PortfolioId,
            PropertyId = target.PropertyId,
            Lender = lender,
            OriginalAmount = originalAmount,
            CurrentBalance = currentBalance ?? originalAmount,
            AnnualInterestRatePct = interestRate,
            TermMonths = target.TermMonths is >= 1 and <= 1200 ? target.TermMonths.Value : 360,
            StartDate = ToUtc(target.StartDate) ?? ToUtc(command.ConfirmedAtUtc),
            DebtServiceAutomationStartDate = ToUtc(command.ConfirmedAtUtc),
            DayOfMonthDue = target.DayOfMonthDue is >= 1 and <= 31 ? target.DayOfMonthDue.Value : 1,
            MonthlyPrincipalInterest = monthlyPrincipalInterest,
            MonthlyEscrow = monthlyEscrow,
            EscrowCoversTaxes = target.EscrowCoversTaxes ?? false,
            EscrowCoversInsurance = target.EscrowCoversInsurance ?? false,
            Status = LoanStatus.Active,
            Notes = BuildLoanNotes(target.Notes),
            CreatedAt = ToUtc(command.ConfirmedAtUtc),
            UpdatedAt = ToUtc(command.ConfirmedAtUtc),
        };

        _db.Add(loan);
        var flush = await context.FlushBusinessAsync(ct);
        EnrichCreatedTargetAudit(command, extractedFieldsJson, context, flush, loan);
        return new ScanConfirmationTargetWriteResult(loan.Id, TargetAuditRecorded: true);
    }

    private static async Task RejectDuplicateActiveLoanSourceAsync(
        ConfirmScanDraftCommand command,
        ScanLoanTargetData target,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var sourceHash = command.SourceContentSha256?.Trim();
        if (string.IsNullOrWhiteSpace(sourceHash) && command.SourceStoredFileId is int sourceStoredFileId)
        {
            sourceHash = await db.Set<StoredFile>()
                .AsNoTracking()
                .Where(file => file.Id == sourceStoredFileId
                    && file.PortfolioId == command.PortfolioId
                    && file.DeletedAt == null)
                .Select(file => file.ContentSha256)
                .SingleOrDefaultAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(sourceHash))
            return;

        var duplicateLoanId = await (
            from draft in db.Set<ScanDraft>().AsNoTracking()
            join loan in db.Set<Loan>().AsNoTracking()
                on new { PortfolioId = draft.PortfolioId, LoanId = draft.ConfirmedEntityId }
                equals new { loan.PortfolioId, LoanId = (int?)loan.Id }
            where draft.PortfolioId == command.PortfolioId
                && draft.Id != command.DraftId
                && draft.Status == "Confirmed"
                && draft.TargetEntityType == ScanConfirmationTargetKind.Loan.ToString()
                && draft.ConfirmedEntityId > 0
                && (draft.SourceContentSha256 == sourceHash
                    || (draft.SourceContentSha256 == null
                        && draft.SourceStoredFile != null
                        && draft.SourceStoredFile.ContentSha256 == sourceHash))
                && loan.PropertyId == target.PropertyId
                && loan.Status == LoanStatus.Active
                && loan.DeletedAt == null
            orderby draft.ConfirmedAt ?? draft.CreatedAt, draft.Id
            select (int?)loan.Id)
            .FirstOrDefaultAsync(ct);

        if (duplicateLoanId is int existingLoanId)
        {
            throw new ScanConfirmationValidationException(
                $"This mortgage source is already confirmed as active loan #{existingLoanId} for this property. Open the existing loan instead of creating another active loan.");
        }
    }

    private async Task<ScanConfirmationTargetWriteResult> WritePropertyAcquisitionAsync(
        ConfirmScanDraftCommand command,
        ScanPropertyAcquisitionTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.SourceStoredFileId is not > 0)
        {
            throw new ScanConfirmationValidationException(
                "A reviewed deed source file is required before confirming property acquisition.");
        }
        if (command.CaptureContext?.PropertyId is int capturedPropertyId
            && capturedPropertyId != target.PropertyId)
        {
            throw new ScanConfirmationValidationException(
                "The reviewed deed must be confirmed against the property it was scanned from.");
        }

        var reviewed = await RequireReviewedPropertyAcquisitionFactsAsync(
            command, target, _db, ct);
        var property = await _db.Set<Property>()
            .Where(row => row.Id == target.PropertyId
                && row.PortfolioId == command.PortfolioId
                && row.DeletedAt == null)
            .SingleOrDefaultAsync(ct);
        if (property is null)
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }

        var now = ToUtc(command.ConfirmedAtUtc);
        property.PurchasePrice = reviewed.PurchasePrice;
        property.LandValue = reviewed.LandValue;
        property.InServiceDate = reviewed.InServiceDate;
        property.Notes = BuildPropertyAcquisitionNotes(property.Notes, target.Notes);
        property.UpdatedAt = now;
        context.BindSemanticAudit(property, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(Property),
            property.Id,
            AuditLogOperation.Updated,
            UserId: command.ConfirmedByUserId,
            OldValues: extractedFieldsJson,
            ChangeReason: $"Confirmed acquisition/basis from deed scan draft #{command.DraftId}."));

        var currentOwnerships = await _db.Set<PropertyOwnership>()
            .Where(ownership => ownership.PortfolioId == command.PortfolioId
                && ownership.PropertyId == property.Id
                && ownership.EffectiveFromUtc <= now
                && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > now))
            .ToListAsync(ct);
        var requestedByOwner = reviewed.Ownerships.ToDictionary(ownership => ownership.OwnerEntityId);
        var retained = new List<PropertyOwnership>();
        var ended = new List<PropertyOwnership>();
        var created = new List<PropertyOwnership>();

        foreach (var current in currentOwnerships)
        {
            if (requestedByOwner.Remove(current.OwnerEntityId, out var requested))
            {
                current.EffectiveFromUtc = reviewed.AcquisitionDate;
                current.OwnershipSharePercent = requested.OwnershipSharePercent;
                current.StatementRecipientName = requested.StatementRecipientName;
                current.StatementRecipientEmail = requested.StatementRecipientEmail;
                current.PayeeName = requested.PayeeName;
                retained.Add(current);
            }
            else
            {
                current.EffectiveToUtc = current.EffectiveFromUtc < now ? now : now.AddTicks(1);
                ended.Add(current);
            }
        }

        foreach (var requested in requestedByOwner.Values)
        {
            _db.Add(requested);
            created.Add(requested);
        }

        await context.FlushBusinessAsync(ct);
        foreach (var ownership in retained)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(PropertyOwnership),
                ownership.Id,
                AuditLogOperation.Updated,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Confirmed deed ownership for Property {property.Id} from scan draft #{command.DraftId}."));
        }
        foreach (var ownership in ended)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(PropertyOwnership),
                ownership.Id,
                AuditLogOperation.Updated,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Ended prior ownership for Property {property.Id} during deed scan confirmation #{command.DraftId}."));
        }
        foreach (var ownership in created)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(PropertyOwnership),
                ownership.Id,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Confirmed deed ownership for Property {property.Id} from scan draft #{command.DraftId}."));
        }

        return new ScanConfirmationTargetWriteResult(
            property.Id,
            CanonicalEntityType: nameof(Property),
            TargetAuditRecorded: true);
    }

    private async Task<ScanConfirmationTargetWriteResult> WriteLeaseEndingNoticeAsync(
        ConfirmScanDraftCommand command,
        ScanLeaseEndingNoticeTargetData target,
        string? extractedFieldsJson,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.SourceStoredFileId is not > 0)
        {
            throw new ScanConfirmationValidationException(
                "A scanned notice source file is required before confirming a lease-ending notice.");
        }

        var noticeGivenAtUtc = ToUtc(target.NoticeGivenAtUtc);
        var plannedMoveOutAtUtc = ToUtc(target.PlannedMoveOutAtUtc);
        if (target.LeaseManagementId <= 0 || target.UnitId <= 0)
        {
            throw new ScanConfirmationValidationException(
                "Choose the existing rental relationship this notice belongs to.");
        }

        var noticeType = string.IsNullOrWhiteSpace(target.NoticeType)
            ? "lease-ending notice"
            : target.NoticeType.Trim();
        var reason = string.IsNullOrWhiteSpace(target.Reason)
            ? $"Scanned {noticeType} from scan draft #{command.DraftId}."
            : $"Scanned {noticeType} from scan draft #{command.DraftId}: {target.Reason.Trim()}";
        if (reason.Length > 1000)
            reason = reason[..1000];

        var result = await new RecordLeaseEndingDispositionHandler(_db).HandleAsync(
            new RecordLeaseEndingDispositionCommand(
                command.PortfolioId,
                target.LeaseManagementId,
                target.UnitId,
                LeaseManagementEndingDisposition.NonRenewalMoveOut,
                noticeGivenAtUtc,
                plannedMoveOutAtUtc,
                reason,
                command.ConfirmedByUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision,
                command.DeliveryIdempotencyKey),
            context,
            ct);
        if (result.Outcome != RecordLeaseEndingDispositionOutcome.Recorded)
        {
            throw new ScanConfirmationValidationException(
                result.Error ?? "The selected rental relationship cannot receive this lease-ending notice.");
        }

        return new ScanConfirmationTargetWriteResult(
            result.LeaseManagementId,
            target.UnitId,
            CanonicalEntityType: nameof(LeaseManagement),
            LeaseManagementId: result.LeaseManagementId,
            TargetAuditRecorded: true);
    }

    private sealed record ReviewedPropertyAcquisitionFacts(
        DateTime AcquisitionDate,
        decimal PurchasePrice,
        decimal LandValue,
        DateTime InServiceDate,
        IReadOnlyList<PropertyOwnership> Ownerships);

    private static async Task<ReviewedPropertyAcquisitionFacts> RequireReviewedPropertyAcquisitionFactsAsync(
        ConfirmScanDraftCommand command,
        ScanPropertyAcquisitionTargetData target,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var missing = new List<string>();
        if (target.PropertyId <= 0)
            missing.Add("existing property");
        var acquisitionDate = ToUtc(target.AcquisitionDate);
        if (acquisitionDate is null)
            missing.Add("acquisition date");
        var purchasePrice = NormalizeDecimal(
            target.PurchasePrice, "Purchase price", 0.01m, 999_999_999m, 2);
        if (purchasePrice is null)
            missing.Add("purchase price");
        var landValue = NormalizeDecimal(
            target.LandValue ?? 0m, "Land value", 0m, 999_999_999m, 2) ?? 0m;
        if (purchasePrice is not null && landValue > purchasePrice.Value)
            throw new ScanConfirmationValidationException("Land value cannot exceed purchase price.");
        if (target.Ownerships.Count == 0)
            missing.Add("ownership");
        if (missing.Count > 0)
        {
            throw new ScanConfirmationValidationException(
                "Review " + string.Join(", ", missing) + " before confirming this property acquisition.");
        }

        var ownerIds = target.Ownerships.Select(ownership => ownership.OwnerEntityId).ToArray();
        if (ownerIds.Any(id => id <= 0) || ownerIds.Distinct().Count() != ownerIds.Length)
        {
            throw new ScanConfirmationValidationException(
                "Each deed ownership row must reference one distinct OwnerEntity.");
        }
        if (target.Ownerships.Sum(ownership => ownership.OwnershipSharePercent) != 100m)
        {
            throw new ScanConfirmationValidationException(
                "Reviewed deed ownership shares must total exactly 100 percent.");
        }

        var owners = await db.Set<OwnerEntity>().AsNoTracking()
            .Where(owner => owner.PortfolioId == command.PortfolioId
                && ownerIds.Contains(owner.Id)
                && owner.DeletedAt == null)
            .Select(owner => new { owner.Id, owner.Name, owner.Email })
            .ToListAsync(ct);
        if (owners.Count != ownerIds.Length)
        {
            throw new ScanConfirmationValidationException(
                "One or more deed owners are missing or outside this portfolio.");
        }

        var ownerById = owners.ToDictionary(owner => owner.Id);
        var ownerships = target.Ownerships.Select(request =>
        {
            var owner = ownerById[request.OwnerEntityId];
            var share = NormalizeDecimal(
                request.OwnershipSharePercent, "Ownership share", 0.0001m, 100m, 4)
                ?? throw new ScanConfirmationValidationException("Ownership share is required.");
            return new PropertyOwnership
            {
                PortfolioId = command.PortfolioId,
                PropertyId = target.PropertyId,
                OwnerEntityId = owner.Id,
                OwnershipSharePercent = share,
                EffectiveFromUtc = acquisitionDate!.Value,
                StatementRecipientName = string.IsNullOrWhiteSpace(request.StatementRecipientName)
                    ? owner.Name
                    : request.StatementRecipientName.Trim(),
                StatementRecipientEmail = request.StatementRecipientEmail?.Trim() ?? owner.Email,
                PayeeName = string.IsNullOrWhiteSpace(request.PayeeName)
                    ? owner.Name
                    : request.PayeeName.Trim(),
            };
        }).ToArray();

        return new ReviewedPropertyAcquisitionFacts(
            acquisitionDate!.Value,
            purchasePrice!.Value,
            landValue,
            ToUtc(target.InServiceDate) ?? acquisitionDate.Value,
            ownerships);
    }

    private async Task<ScanConfirmationTargetWriteResult> MatchExistingLoanPaymentAsync(
        ConfirmScanDraftCommand command,
        ScanLoanTargetData target,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (target.ExistingLoanId is not int loanId || target.ExistingLoanPaymentId is not int paymentId)
        {
            throw new ScanConfirmationValidationException(
                "Select both an existing loan and an existing scheduled payment to match this scan.");
        }

        var lockedPaymentId = await _db.Database.SqlQuery<int>($"""
            SELECT payment."Id" AS "Value"
            FROM "LoanPayments" AS payment
            JOIN "Loans" AS loan
              ON loan."Id" = payment."LoanId"
             AND loan."PortfolioId" = payment."PortfolioId"
            JOIN "Properties" AS property
              ON property."Id" = loan."PropertyId"
             AND property."PortfolioId" = loan."PortfolioId"
            WHERE payment."Id" = {paymentId}
              AND payment."LoanId" = {loanId}
              AND payment."PortfolioId" = {command.PortfolioId}
              AND loan."PropertyId" = {target.PropertyId}
              AND loan."DeletedAt" IS NULL
              AND property."DeletedAt" IS NULL
            FOR UPDATE OF payment
            """).SingleOrDefaultAsync(ct);
        if (lockedPaymentId == 0)
        {
            throw new ScanConfirmationValidationException(
                "Selected loan payment is not in this portfolio or property.");
        }

        var payment = await _db.Set<LoanPayment>()
            .Include(row => row.Loan)
            .SingleOrDefaultAsync(row =>
                row.Id == lockedPaymentId
                && row.LoanId == loanId
                && row.PortfolioId == command.PortfolioId, ct);
        if (payment?.Loan is null)
        {
            throw new ScanConfirmationValidationException(
                "Selected loan payment is not in this portfolio or property.");
        }

        var reviewed = RequireReviewedLoanStatementFacts(command, target);
        var effective = await LoanPaymentEffectiveQuery.From(_db)
            .SingleAsync(row =>
                row.Id == payment.Id
                && row.PortfolioId == command.PortfolioId, ct);

        if (effective.Status != LoanPaymentStatus.Paid)
        {
            var hasEarlierUnpaid = await LoanPaymentEffectiveQuery.From(_db)
                .AnyAsync(row =>
                    row.LoanId == payment.LoanId
                    && row.PortfolioId == command.PortfolioId
                    && row.Id != payment.Id
                    && (row.DueDate < reviewed.EffectiveDate
                        || (row.DueDate == reviewed.EffectiveDate && row.Id < payment.Id))
                    && row.Status != LoanPaymentStatus.Paid, ct);
            if (hasEarlierUnpaid)
            {
                throw new ScanConfirmationValidationException(
                    "Earlier scheduled loan payments must be posted first.");
            }
            if (payment.Loan.CurrentBalance != reviewed.OpeningBalance)
            {
                throw new ScanConfirmationValidationException(
                    "Statement opening unpaid principal must match the loan's current live balance before matching.");
            }

            var paidAt = reviewed.EffectiveDate;
            var correction = new LoanPaymentCorrection
            {
                PortfolioId = command.PortfolioId,
                LoanPaymentId = payment.Id,
                AttemptId = context.AttemptId,
                SourceScanDraftId = command.DraftId,
                DueDate = reviewed.EffectiveDate,
                PaidDate = paidAt,
                PrincipalAmount = reviewed.PrincipalAmount,
                InterestAmount = reviewed.InterestAmount,
                EscrowAmount = reviewed.EscrowAmount,
                TotalAmount = reviewed.TotalAmount,
                BalanceAfter = reviewed.BalanceAfter,
                Status = LoanPaymentStatus.Paid,
                PaymentDoesNotCoverInterest = false,
                CreatedAtUtc = ToUtc(command.ConfirmedAtUtc),
            };
            _db.Add(correction);
            payment.Loan.CurrentBalance = reviewed.BalanceAfter;
            payment.Loan.UpdatedAt = paidAt;
            if (reviewed.BalanceAfter == 0m)
                payment.Loan.Status = LoanStatus.PaidOff;

            context.BindSemanticAudit(correction, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LoanPaymentCorrection),
                0,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Scan draft #{command.DraftId} appended the effective snapshot for loan payment {payment.Id}."));
            context.BindSemanticAudit(payment.Loan, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Loan),
                payment.Loan.Id,
                AuditLogOperation.Updated,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Scan draft #{command.DraftId} matched loan payment {payment.Id} and reduced the live balance."));
            await context.FlushBusinessAsync(ct);
            StageLoanPaymentDataUpdate(context, command, nameof(LoanPayment), payment.Id);
            StageLoanPaymentDataUpdate(context, command, nameof(Loan), payment.Loan.Id);
        }
        else
        {
            var impliedOpeningBalance = effective.BalanceAfter + effective.PrincipalAmount;
            if (effective.EscrowAmount != reviewed.EscrowAmount
                || effective.TotalAmount != reviewed.TotalAmount
                || impliedOpeningBalance != reviewed.OpeningBalance)
            {
                throw new ScanConfirmationValidationException(
                    "Reviewed statement values must match the already-paid loan payment.");
            }

            var principalCorrection = reviewed.PrincipalAmount - effective.PrincipalAmount;
            var interestCorrection = reviewed.InterestAmount - effective.InterestAmount;
            if (principalCorrection + interestCorrection != 0m
                || Math.Abs(principalCorrection) >= PaidStatementComponentCorrectionTolerance
                || Math.Abs(interestCorrection) >= PaidStatementComponentCorrectionTolerance)
            {
                throw new ScanConfirmationValidationException(
                    "Reviewed statement values must match the already-paid loan payment.");
            }

            var hasLaterPaid = await LoanPaymentEffectiveQuery.From(_db)
                .AnyAsync(row =>
                    row.LoanId == payment.LoanId
                    && row.PortfolioId == command.PortfolioId
                    && row.Id != payment.Id
                    && (row.DueDate > effective.DueDate
                        || (row.DueDate == effective.DueDate && row.Id > payment.Id))
                    && row.Status == LoanPaymentStatus.Paid, ct);
            if (hasLaterPaid || payment.Loan.CurrentBalance != effective.BalanceAfter)
            {
                throw new ScanConfirmationValidationException(
                    "Already-paid loan payment corrections require the selected payment to be the latest posted payment.");
            }

            var postingChanged = effective.PrincipalAmount != reviewed.PrincipalAmount
                || effective.InterestAmount != reviewed.InterestAmount
                || effective.EscrowAmount != reviewed.EscrowAmount
                || effective.TotalAmount != reviewed.TotalAmount;
            var correction = new LoanPaymentCorrection
            {
                PortfolioId = command.PortfolioId,
                LoanPaymentId = payment.Id,
                AttemptId = context.AttemptId,
                SourceScanDraftId = command.DraftId,
                DueDate = reviewed.EffectiveDate,
                PaidDate = effective.PaidDate,
                PrincipalAmount = reviewed.PrincipalAmount,
                InterestAmount = reviewed.InterestAmount,
                EscrowAmount = reviewed.EscrowAmount,
                TotalAmount = reviewed.TotalAmount,
                BalanceAfter = reviewed.BalanceAfter,
                Status = LoanPaymentStatus.Paid,
                PaymentDoesNotCoverInterest = effective.PaymentDoesNotCoverInterest,
                CreatedAtUtc = ToUtc(command.ConfirmedAtUtc),
            };
            _db.Add(correction);
            payment.Loan.CurrentBalance = reviewed.BalanceAfter;
            payment.Loan.UpdatedAt = reviewed.EffectiveDate;
            if (reviewed.BalanceAfter == 0m)
                payment.Loan.Status = LoanStatus.PaidOff;

            context.BindSemanticAudit(correction, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(LoanPaymentCorrection),
                0,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Scan draft #{command.DraftId} appended the effective snapshot for paid loan payment {payment.Id}."));
            context.BindSemanticAudit(payment.Loan, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Loan),
                payment.Loan.Id,
                AuditLogOperation.Updated,
                UserId: command.ConfirmedByUserId,
                ChangeReason: $"Scan draft #{command.DraftId} reconciled paid loan payment {payment.Id} to the reviewed statement balance."));
            await context.FlushBusinessAsync(ct);
            if (postingChanged)
            {
                await MoneyAccountingPosting.PostLoanPaymentCorrectionAsync(
                    _db,
                    context,
                    payment,
                    correction,
                    command.ConfirmedByUserId,
                    ct);
            }
            StageLoanPaymentDataUpdate(context, command, nameof(LoanPayment), payment.Id);
            StageLoanPaymentDataUpdate(context, command, nameof(Loan), payment.Loan.Id);
        }

        return new ScanConfirmationTargetWriteResult(
            payment.Loan.Id,
            CanonicalEntityType: nameof(Loan),
            LoanPaymentId: payment.Id,
            TargetAuditRecorded: true);
    }

    private static void StageLoanPaymentDataUpdate(
        IAtomicCommandContext context,
        ConfirmScanDraftCommand command,
        string entityType,
        int entityId)
    {
        var now = ToUtc(command.ConfirmedAtUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey =
                $"{command.DeliveryIdempotencyKey}:{entityType}:{entityId}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static ReviewedLoanStatementFacts RequireReviewedLoanStatementFacts(
        ConfirmScanDraftCommand command,
        ScanLoanTargetData target)
    {
        var openingBalance = NormalizeDecimal(
            target.CurrentBalance, "Statement opening unpaid principal balance", 0m, 999_999_999m, 2);
        var principal = NormalizeDecimal(
            target.StatementPrincipalAmount, "Statement principal amount", 0m, 999_999_999m, 2);
        var interest = NormalizeDecimal(
            target.StatementInterestAmount, "Statement interest amount", 0m, 999_999_999m, 2);
        var escrow = NormalizeDecimal(
            target.StatementEscrowAmount, "Statement escrow amount", 0m, 999_999_999m, 2);
        var total = NormalizeDecimal(
            target.StatementTotalAmount, "Statement total amount", 0m, 999_999_999m, 2);
        var missing = new List<string>();
        if (openingBalance is null) missing.Add("opening unpaid principal balance");
        if (principal is null) missing.Add("statement principal");
        if (interest is null) missing.Add("statement interest");
        if (escrow is null) missing.Add("statement escrow");
        if (total is null) missing.Add("statement total");
        if (missing.Count > 0)
        {
            throw new ScanConfirmationValidationException(
                "Review " + string.Join(", ", missing) + " before matching this loan statement.");
        }

        var principalAndInterest = principal!.Value + interest!.Value;
        var expectedTotal = principalAndInterest + escrow!.Value;
        if (total!.Value != expectedTotal)
        {
            throw new ScanConfirmationValidationException(
                "Statement total must equal principal plus interest plus escrow.");
        }

        var reviewedPi = NormalizeDecimal(
            target.MonthlyPrincipalInterest, "Statement principal and interest", 0m, 999_999_999m, 2);
        if (reviewedPi is not null && reviewedPi.Value != principalAndInterest)
        {
            throw new ScanConfirmationValidationException(
                "Statement principal and interest must equal principal plus interest.");
        }

        var balanceAfter = openingBalance!.Value - principal.Value;
        if (balanceAfter < 0m)
        {
            throw new ScanConfirmationValidationException(
                "Statement principal cannot exceed the opening unpaid principal balance.");
        }

        return new ReviewedLoanStatementFacts(
            openingBalance.Value,
            principal.Value,
            interest.Value,
            escrow.Value,
            total.Value,
            balanceAfter,
            ToUtc(target.StatementEffectiveDate) ?? ToUtc(command.ConfirmedAtUtc));
    }

    private static void EnrichCreatedTargetAudit(
        ConfirmScanDraftCommand command,
        string? claimExtractedFieldsJson,
        IAtomicCommandContext context,
        AtomicBusinessFlush flush,
        object target)
    {
        var mutation = flush.Mutations.Single(row =>
            ReferenceEquals(row.EntityReference, target)
            && row.Operation == AuditLogOperation.Created);
        context.EnrichMutation(
            mutation,
            new AtomicSemanticAudit(
                command.PortfolioId,
                mutation.EntityType,
                mutation.EntityId,
                AuditLogOperation.Created,
                UserId: command.ConfirmedByUserId,
                OldValues: claimExtractedFieldsJson,
                ChangeReason: $"Created from scan draft #{command.DraftId}."));
    }

    private static async Task<ExpenseLocationContext> ResolveExpenseLocationAsync(
        int portfolioId,
        ScanExpenseTargetData target,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (target.WorkOrderId is int workOrderId)
        {
            var workOrderLocation = await db.Set<WorkOrder>()
                .Where(order => order.Id == workOrderId
                    && order.PortfolioId == portfolioId
                    && order.Status != WorkOrderStatus.Cancelled
                    && order.Status != WorkOrderStatus.Archived
                    && (target.PropertyId == null || order.PropertyId == target.PropertyId)
                    && (target.UnitId == null || order.UnitId == target.UnitId))
                .Select(order => new ExpenseLocationContext(
                    ExpenseOperationalScope.WorkOrder,
                    order.PropertyId,
                    order.UnitId,
                    order.Id))
                .SingleOrDefaultAsync(ct);
            return workOrderLocation
                ?? throw new ScanConfirmationValidationException("Selected work order is not in this portfolio or location.");
        }

        if (target.UnitId is int unitId)
        {
            var unitLocation = await db.Set<Unit>()
                .Where(unit => unit.Id == unitId
                    && unit.PortfolioId == portfolioId
                    && (target.PropertyId == null || unit.PropertyId == target.PropertyId))
                .Select(unit => new ExpenseLocationContext(
                    ExpenseOperationalScope.Unit,
                    unit.PropertyId,
                    unit.Id,
                    null))
                .SingleOrDefaultAsync(ct);
            return unitLocation
                ?? throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }

        if (target.PropertyId is int propertyId)
        {
            var propertyLocation = await db.Set<Property>()
                .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId)
                .Select(property => new ExpenseLocationContext(
                    ExpenseOperationalScope.Property,
                    property.Id,
                    null,
                    null))
                .SingleOrDefaultAsync(ct);
            return propertyLocation
                ?? throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }

        return new ExpenseLocationContext(ExpenseOperationalScope.Portfolio, null, null, null);
    }

    private sealed record ExpenseLocationContext(
        ExpenseOperationalScope OperationalScope,
        int? PropertyId,
        int? UnitId,
        int? WorkOrderId);

    private async Task ValidateOptionalPropertyUnitAsync(
        int portfolioId,
        int? propertyId,
        int? unitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var resolution = await db.ResolvePropertyUnit(portfolioId, propertyId, unitId)
            .SingleOrDefaultAsync(ct);
        if (propertyId is not null
            && (resolution is null || resolution.PropertyId is null))
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        if (unitId is not null
            && (resolution is null
                || resolution.UnitId is null
                || resolution.UnitDoesNotBelongToProperty))
        {
            throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }
    }

    private async Task ValidateWorkOrderReferencesAsync(
        int portfolioId,
        ScanWorkOrderTargetData target,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (!await IsPropertyInPortfolioAsync(portfolioId, target.PropertyId, _db, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }
        if (target.UnitId is int unitId && !await db.Set<Unit>()
                .AnyAsync(unit => unit.Id == unitId
                    && unit.PropertyId == target.PropertyId
                    && unit.Property != null
                    && unit.Property.PortfolioId == portfolioId, ct))
        {
            throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }
        if (target.VendorId is int vendorId && !await db.Set<Vendor>()
                .AnyAsync(vendor => vendor.Id == vendorId && vendor.PortfolioId == portfolioId, ct))
        {
            throw new ScanConfirmationValidationException("Selected vendor is not in this portfolio.");
        }
        if (target.LeaseManagementId is int leaseManagementId && !await db.Set<LeaseManagement>()
                .AnyAsync(relationship => relationship.Id == leaseManagementId
                    && relationship.PortfolioId == portfolioId
                    && relationship.PropertyId == target.PropertyId
                    && (target.UnitId == null || relationship.UnitId == target.UnitId), ct))
        {
            throw new ScanConfirmationValidationException("Selected lease relationship is not in this portfolio or location.");
        }
        if (target.TenantId is int tenantId && !await TenantMatchesLocationAsync(
                portfolioId, tenantId, target.PropertyId, target.UnitId, _db, ct))
        {
            throw new ScanConfirmationValidationException("Selected tenant does not belong to this location.");
        }
    }

    private static Task<bool> TenantMatchesLocationAsync(
        int portfolioId,
        int tenantId,
        int propertyId,
        int? unitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var currentParties =
            from party in db.Set<LeaseManagementParty>()
            join lifecycle in db.Set<LeaseManagementLifecycleProjection>()
                on new { party.PortfolioId, party.LeaseManagementId }
                equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
            where party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && party.EffectiveFrom <= lifecycle.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
                && lifecycle.Lifecycle != "Canceled"
                && lifecycle.Lifecycle != "Closed"
                && lifecycle.PropertyId == propertyId
                && (unitId == null || lifecycle.UnitId == unitId)
            select party.Id;
        return currentParties.AnyAsync(ct);
    }

    private async Task<int?> ResolveOrCreateVendorAsync(
        int portfolioId,
        ScanReceiptData receipt,
        DateTime now,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var name = receipt.VendorName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalizedName = name.ToLowerInvariant();
        var matchSummary = await _db.Set<Vendor>()
            .Where(vendor => vendor.PortfolioId == portfolioId
                && vendor.Name.Trim().ToLower() == normalizedName)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                Id = group.Min(vendor => vendor.Id),
            })
            .SingleOrDefaultAsync(ct);
        if (matchSummary?.Count == 1)
        {
            return matchSummary.Id;
        }
        if (matchSummary?.Count > 1)
        {
            return null;
        }

        var vendor = new Vendor
        {
            PortfolioId = portfolioId,
            Name = Truncate(name, 200)!,
            ServiceType = "General",
            Phone = Truncate(receipt.VendorPhone?.Trim(), 50),
            Website = Truncate(receipt.VendorWebsite?.Trim(), 500),
            TaxId = Truncate(receipt.VendorTaxId?.Trim(), 50),
            Notes = "Created from scanned receipt.",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Add(vendor);
        await context.FlushBusinessAsync(ct);
        return vendor.Id;
    }

    private static Task<bool> IsPropertyInPortfolioAsync(
        int portfolioId,
        int propertyId,
        RentalCommandDbContext db,
        CancellationToken ct) => db.Set<Property>()
            .AnyAsync(property => property.Id == propertyId && property.PortfolioId == portfolioId, ct);

    private static string SerializeReceipt(ScanReceiptData receipt) => JsonSerializer.Serialize(new
    {
        documentKind = receipt.DocumentKind,
        dueDate = receipt.DueDate,
        vendor = new
        {
            address = receipt.VendorAddress,
            phone = receipt.VendorPhone,
            website = receipt.VendorWebsite,
            taxId = receipt.VendorTaxId,
        },
        receiptNumber = receipt.ReceiptNumber,
        paymentMethod = receipt.PaymentMethod,
        cardLast4 = receipt.CardLast4,
        taxRate = receipt.TaxRate,
        tip = receipt.Tip,
        discount = receipt.Discount,
        shipping = receipt.Shipping,
        lineItems = receipt.LineItems,
        extra = ExtraFields(receipt.ExtraFields),
    }, ReceiptJsonOptions);

    private static string? NormalizeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ExtraFields(IReadOnlyList<ScanExtraFieldData>? fields)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields ?? [])
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                result[field.Name] = field.Value;
            }
        }
        return result;
    }

    private static string? BuildApplicationNotes(ScanApplicationTargetData target)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.Notes)) parts.Add(target.Notes.Trim());
        if (!string.IsNullOrWhiteSpace(target.ApplyingFor)) parts.Add($"Applying for: {target.ApplyingFor.Trim()}.");
        if (!string.IsNullOrWhiteSpace(target.IdLast4)) parts.Add($"ID last-4: {target.IdLast4.Trim()}.");
        if (!string.IsNullOrWhiteSpace(target.CoSignerName)) parts.Add($"Co-signer: {target.CoSignerName.Trim()}.");
        return parts.Count == 0 ? null : Truncate(string.Join(" ", parts), 2000);
    }

    private static string BuildLoanNotes(string? notes)
    {
        const string provenance = "Imported from scanned mortgage document.";
        var result = string.IsNullOrWhiteSpace(notes)
            ? provenance
            : $"{notes.Trim()} ({provenance})";
        return Truncate(result, 2000)!;
    }

    private static string? BuildPropertyAcquisitionNotes(string? existingNotes, string? reviewedNotes)
    {
        const string provenance = "Acquisition and basis confirmed from scanned deed.";
        var parts = new[]
        {
            existingNotes?.Trim(),
            string.IsNullOrWhiteSpace(reviewedNotes)
                ? provenance
                : $"{reviewedNotes.Trim()} ({provenance})",
        }.Where(part => !string.IsNullOrWhiteSpace(part));
        return Truncate(string.Join(Environment.NewLine, parts), 2000);
    }

    private static decimal? NormalizeDecimal(
        decimal? raw,
        string label,
        decimal min,
        decimal max,
        int scale)
    {
        if (raw is null)
        {
            return null;
        }
        if (raw < min || raw > max)
        {
            throw new ScanConfirmationValidationException($"{label} must be between {min} and {max}.");
        }
        return decimal.Round(raw.Value, scale, MidpointRounding.AwayFromZero);
    }

    private static string RequireText(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ScanConfirmationValidationException($"{label} is required.");
        }
        return value.Trim();
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? ToUtc(DateTime? value) => value is null ? null : ToUtc(value.Value);

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException("The selected scan target payload is missing.");

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
