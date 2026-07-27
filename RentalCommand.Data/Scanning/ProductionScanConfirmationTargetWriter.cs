using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Data.Payments;

namespace RentalCommand.Data.Scanning;

/// <summary>
/// Persistence-only writers for scan targets. This type deliberately has no injected services:
/// every query and write goes through the transaction-owned persistence session supplied by the
/// atomic kernel. Lease scans delegate to the canonical relationship/account/agreement writer.
/// </summary>
public sealed class ProductionScanConfirmationTargetWriter : IScanConfirmationTargetWriter
{
    private static readonly JsonSerializerOptions ReceiptJsonOptions = new(JsonSerializerDefaults.Web);

    public ProductionScanConfirmationTargetWriter() { }

    public bool Supports(ScanConfirmationTargetKind kind) => kind is
        ScanConfirmationTargetKind.Expense or
        ScanConfirmationTargetKind.Payment or
        ScanConfirmationTargetKind.WorkOrder or
        ScanConfirmationTargetKind.LeaseAgreement or
        ScanConfirmationTargetKind.Application or
        ScanConfirmationTargetKind.Loan;

    public Task<ScanConfirmationTargetWriteResult> WriteAsync(
        ConfirmScanDraftCommand command,
        string? extractedFieldsJson,
        IAtomicWriteAttempt attempt,
        CancellationToken ct) => command.Target.Kind switch
        {
            ScanConfirmationTargetKind.Expense => WriteExpenseAsync(
                command, Required(command.Target.Expense), attempt, ct),
            ScanConfirmationTargetKind.Payment => WritePaymentAsync(
                command, Required(command.Target.Payment), extractedFieldsJson, attempt, ct),
            ScanConfirmationTargetKind.WorkOrder => WriteWorkOrderAsync(
                command, Required(command.Target.WorkOrder), extractedFieldsJson, attempt, ct),
            ScanConfirmationTargetKind.LeaseAgreement => CanonicalLeaseScanConfirmationWriter.WriteAsync(
                command, Required(command.Target.LeaseAgreement), attempt, ct),
            ScanConfirmationTargetKind.Application => WriteApplicationAsync(
                command, Required(command.Target.Application), extractedFieldsJson, attempt, ct),
            ScanConfirmationTargetKind.Loan => WriteLoanAsync(
                command, Required(command.Target.Loan), attempt, ct),
            _ => throw new InvalidOperationException(
                $"Scan confirmation target {command.Target.Kind} is not supported by this writer."),
        };

    public async Task AuthorizeReplayAsync(
        ConfirmScanDraftCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        command.Target.Validate();
        var capabilities = RequiredCapabilities(command.Target.Kind);
        var securityNowUtc = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await IsDraftAuthorizedAsync(command, capabilities, persistence, securityNowUtc, ct))
            throw Unauthorized();

        if (command.Target.Kind == ScanConfirmationTargetKind.LeaseAgreement)
        {
            await CanonicalLeaseScanConfirmationWriter.AuthorizeAsync(command, persistence, ct);
            return;
        }

        var propertyId = await ResolveTargetPropertyIdAsync(command, persistence, ct);
        if (!await HasPropertyAuthorityAsync(
                command, capabilities, propertyId, persistence, securityNowUtc, ct))
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
        _ => [],
    };

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc)
    {
        var keys = capabilities.Distinct(StringComparer.Ordinal).ToArray();
        return persistence.Query<MembershipRoleAssignment>().Where(assignment =>
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
            && persistence.Query<AuthSession>().Any(session =>
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
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc)
    {
        var assignments = EffectiveAssignments(command, capabilities, persistence, securityNowUtc);
        return persistence.Query<Property>().Where(property =>
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
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var authorizedProperties = AuthorizedProperties(command, capabilities, persistence, securityNowUtc);
        var assignments = EffectiveAssignments(command, capabilities, persistence, securityNowUtc);
        var allProperties = assignments.Where(assignment =>
            assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);

        return persistence.Query<ScanDraft>().AnyAsync(draft =>
            draft.Id == command.DraftId && draft.PortfolioId == command.PortfolioId
            && (((draft.CapturePropertyId != null || draft.CaptureUnitId != null
                    || draft.CaptureLeaseManagementId != null || draft.CaptureLeaseAgreementId != null
                    || draft.CaptureTenantAccountId != null || draft.CaptureTenantLedgerEntryId != null
                    || draft.CaptureWorkOrderId != null || draft.CaptureApplicationId != null
                    || draft.CaptureRentalListingId != null)
                && (draft.CapturePropertyId == null || authorizedProperties.Any(property =>
                    property.Id == draft.CapturePropertyId))
                && (draft.CaptureUnitId == null || persistence.Query<Unit>().Any(unit =>
                    unit.Id == draft.CaptureUnitId && unit.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == unit.PropertyId)))
                && (draft.CaptureLeaseManagementId == null || persistence.Query<LeaseManagement>().Any(management =>
                    management.Id == draft.CaptureLeaseManagementId && management.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == management.PropertyId)))
                && (draft.CaptureLeaseAgreementId == null || persistence.Query<LeaseAgreement>().Any(agreement =>
                    agreement.Id == draft.CaptureLeaseAgreementId && agreement.PortfolioId == draft.PortfolioId
                    && agreement.LeaseManagement != null
                    && authorizedProperties.Any(property => property.Id == agreement.LeaseManagement.PropertyId)))
                && (draft.CaptureTenantAccountId == null || persistence.Query<TenantAccount>().Any(account =>
                    account.Id == draft.CaptureTenantAccountId && account.PortfolioId == draft.PortfolioId
                    && account.LeaseManagement != null
                    && authorizedProperties.Any(property => property.Id == account.LeaseManagement.PropertyId)))
                && (draft.CaptureTenantLedgerEntryId == null || persistence.Query<TenantLedgerEntry>().Any(entry =>
                    entry.Id == draft.CaptureTenantLedgerEntryId && entry.PortfolioId == draft.PortfolioId
                    && entry.TenantAccount != null && entry.TenantAccount.LeaseManagement != null
                    && authorizedProperties.Any(property =>
                        property.Id == entry.TenantAccount.LeaseManagement.PropertyId)))
                && (draft.CaptureWorkOrderId == null || persistence.Query<WorkOrder>().Any(order =>
                    order.Id == draft.CaptureWorkOrderId && order.PortfolioId == draft.PortfolioId
                    && authorizedProperties.Any(property => property.Id == order.PropertyId)))
                && (draft.CaptureApplicationId == null || persistence.Query<RentalApplication>().Any(application =>
                    application.Id == draft.CaptureApplicationId && application.PortfolioId == draft.PortfolioId
                    && application.PropertyId != null
                    && authorizedProperties.Any(property => property.Id == application.PropertyId)))
                && (draft.CaptureRentalListingId == null || persistence.Query<RentalListing>().Any(listing =>
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
        IAtomicPersistenceSession persistence,
        CancellationToken ct) => command.Target.Kind switch
    {
        ScanConfirmationTargetKind.Expense => await ResolveExpensePropertyIdAsync(
            command, Required(command.Target.Expense), persistence, ct),
        ScanConfirmationTargetKind.Payment => await persistence.Query<TenantAccount>()
            .Where(account => account.Id == Required(command.Target.Payment).TenantAccountId
                && account.PortfolioId == command.PortfolioId && account.LeaseManagement != null)
            .Select(account => (int?)account.LeaseManagement!.PropertyId)
            .SingleOrDefaultAsync(ct),
        ScanConfirmationTargetKind.WorkOrder => Required(command.Target.WorkOrder).PropertyId,
        ScanConfirmationTargetKind.Application => await ResolveOptionalPropertyIdAsync(
            command, Required(command.Target.Application).PropertyId,
            Required(command.Target.Application).UnitId, persistence, ct),
        ScanConfirmationTargetKind.Loan => Required(command.Target.Loan).PropertyId,
        _ => null,
    };

    private static async Task<int?> ResolveExpensePropertyIdAsync(
        ConfirmScanDraftCommand command,
        ScanExpenseTargetData target,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (target.PropertyId is int propertyId)
            return propertyId;
        if (target.UnitId is int unitId)
            return await persistence.Query<Unit>()
                .Where(unit => unit.Id == unitId && unit.PortfolioId == command.PortfolioId)
                .Select(unit => (int?)unit.PropertyId).SingleOrDefaultAsync(ct);
        if (target.WorkOrderId is int workOrderId)
            return await persistence.Query<WorkOrder>()
                .Where(order => order.Id == workOrderId && order.PortfolioId == command.PortfolioId)
                .Select(order => (int?)order.PropertyId).SingleOrDefaultAsync(ct);
        return null;
    }

    private static async Task<int?> ResolveOptionalPropertyIdAsync(
        ConfirmScanDraftCommand command,
        int? propertyId,
        int? unitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (propertyId is not null)
            return propertyId;
        return unitId is int selectedUnitId
            ? await persistence.Query<Unit>()
                .Where(unit => unit.Id == selectedUnitId && unit.PortfolioId == command.PortfolioId)
                .Select(unit => (int?)unit.PropertyId).SingleOrDefaultAsync(ct)
            : null;
    }

    private static Task<bool> HasPropertyAuthorityAsync(
        ConfirmScanDraftCommand command,
        IReadOnlyCollection<string> capabilities,
        int? propertyId,
        IAtomicPersistenceSession persistence,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var assignments = EffectiveAssignments(command, capabilities, persistence, securityNowUtc);
        return propertyId is int selectedPropertyId
            ? AuthorizedProperties(command, capabilities, persistence, securityNowUtc)
                .AnyAsync(property => property.Id == selectedPropertyId, ct)
            : assignments.AnyAsync(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
    }

    private static UnauthorizedAccessException Unauthorized() =>
        new("The current session is not authorized to confirm this scan draft.");

    private static async Task<ScanConfirmationTargetWriteResult> WriteExpenseAsync(
        ConfirmScanDraftCommand command,
        ScanExpenseTargetData target,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var receipt = Required(target.Receipt);
        var amount = receipt.Total ?? receipt.Subtotal ?? 0m;
        if (amount <= 0m)
        {
            throw new ScanConfirmationValidationException("Confirmed expense amount must be greater than zero.");
        }

        await ValidateExpenseLocationAsync(command.PortfolioId, target, attempt.Persistence, ct);
        var vendorId = await ResolveOrCreateVendorAsync(
            command.PortfolioId, receipt, ToUtc(command.ConfirmedAtUtc), attempt, ct);
        var now = ToUtc(command.ConfirmedAtUtc);
        var expense = new Expense
        {
            PortfolioId = command.PortfolioId,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId,
            WorkOrderId = target.WorkOrderId,
            VendorId = vendorId,
            Category = receipt.Category ?? ScheduleECategory.Other,
            Description = string.IsNullOrWhiteSpace(receipt.VendorName)
                ? "Scanned receipt"
                : receipt.VendorName.Trim(),
            Amount = amount,
            Subtotal = receipt.Subtotal,
            TaxAmount = receipt.Tax,
            IncurredAt = ToUtc(receipt.TransactionDate) ?? now,
            DueDate = target.IsPaid ? null : ToUtc(receipt.DueDate),
            PaidAt = target.IsPaid ? ToUtc(receipt.TransactionDate) ?? now : null,
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

        attempt.Persistence.Add(expense);
        await attempt.FlushBusinessAsync(ct);
        return new ScanConfirmationTargetWriteResult(expense.Id, expense.UnitId);
    }

    private static async Task<ScanConfirmationTargetWriteResult> WritePaymentAsync(
        ConfirmScanDraftCommand command,
        ScanPaymentTargetData target,
        string? extractedFieldsJson,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var receipt = Required(target.Receipt);
        var amount = receipt.Total ?? receipt.Subtotal ?? 0m;
        if (amount <= 0m)
        {
            throw new ScanConfirmationValidationException("Confirmed payment amount must be greater than zero.");
        }

        var accountContext = await attempt.Persistence.Query<TenantAccount>()
            .Where(account => account.Id == target.TenantAccountId
                && account.PortfolioId == command.PortfolioId)
            .Select(account => new
            {
                UnitId = (int?)account.LeaseManagement!.UnitId,
                ContextEntryIsValid = target.TenantLedgerEntryId == null
                    || attempt.Persistence.Query<TenantLedgerEntry>().Any(entry =>
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
            receipt.CheckNumber,
            receipt.PayerName,
            receipt.CheckNumber,
            receipt.BankName,
            command.SourceStoredFileId,
            true,
            command.ConfirmedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            CapabilityKeys.MoneyPaymentsManage,
            $"scan-receipt:{command.DraftId}",
            command.DeliveryIdempotencyKey,
            ToUtc(command.ConfirmedAtUtc));
        var result = await new RecordTenantReceiptHandler().HandleAsync(receiptCommand, attempt, ct);
        return new ScanConfirmationTargetWriteResult(
            target.TenantAccountId,
            accountContext.UnitId,
            nameof(TenantAccount),
            result.LedgerEntryId);
    }

    private static async Task<ScanConfirmationTargetWriteResult> WriteWorkOrderAsync(
        ConfirmScanDraftCommand command,
        ScanWorkOrderTargetData target,
        string? extractedFieldsJson,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var title = RequireText(target.Title, "Work order title");
        var description = RequireText(target.Description, "Work order description");
        await ValidateWorkOrderReferencesAsync(command.PortfolioId, target, attempt.Persistence, ct);

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

        attempt.Persistence.Add(workOrder);
        await attempt.FlushBusinessAsync(ct);
        return new ScanConfirmationTargetWriteResult(workOrder.Id, workOrder.UnitId);
    }

    private static async Task<ScanConfirmationTargetWriteResult> WriteApplicationAsync(
        ConfirmScanDraftCommand command,
        ScanApplicationTargetData target,
        string? extractedFieldsJson,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var firstName = RequireText(target.FirstName, "Applicant first name");
        var lastName = RequireText(target.LastName, "Applicant last name");
        await ValidateOptionalPropertyUnitAsync(
            command.PortfolioId, target.PropertyId, target.UnitId, attempt.Persistence, ct);

        var normalizedEmail = NormalizeEmail(target.Email);
        if (normalizedEmail is not null)
        {
            var existingOpenApplicationId = await attempt.Persistence.Query<RentalApplication>()
                .Where(application => application.PortfolioId == command.PortfolioId
                    && application.Email != null
                    && (application.Status == ApplicationStatus.Submitted
                        || application.Status == ApplicationStatus.UnderReview
                        || application.Status == ApplicationStatus.Approved)
                    && application.Email.Trim().ToLower() == normalizedEmail)
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

        attempt.Persistence.Add(application);
        await attempt.FlushBusinessAsync(ct);
        return new ScanConfirmationTargetWriteResult(application.Id, application.UnitId);
    }

    private static async Task<ScanConfirmationTargetWriteResult> WriteLoanAsync(
        ConfirmScanDraftCommand command,
        ScanLoanTargetData target,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        if (!await IsPropertyInPortfolioAsync(
                command.PortfolioId, target.PropertyId, attempt.Persistence, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }

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

        attempt.Persistence.Add(loan);
        await attempt.FlushBusinessAsync(ct);
        return new ScanConfirmationTargetWriteResult(loan.Id);
    }

    private static async Task ValidateExpenseLocationAsync(
        int portfolioId,
        ScanExpenseTargetData target,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (target.PropertyId is int propertyId &&
            !await IsPropertyInPortfolioAsync(portfolioId, propertyId, persistence, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }
        if (target.UnitId is int unitId && !await persistence.Query<Unit>()
                .AnyAsync(unit => unit.Id == unitId
                    && unit.Property != null
                    && unit.Property.PortfolioId == portfolioId
                    && (target.PropertyId == null || unit.PropertyId == target.PropertyId), ct))
        {
            throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }
        if (target.WorkOrderId is int workOrderId && !await persistence.Query<WorkOrder>()
                .AnyAsync(order => order.Id == workOrderId && order.PortfolioId == portfolioId, ct))
        {
            throw new ScanConfirmationValidationException("Selected work order is not in this portfolio.");
        }
    }

    private static async Task ValidateOptionalPropertyUnitAsync(
        int portfolioId,
        int? propertyId,
        int? unitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (propertyId is int selectedPropertyId &&
            !await IsPropertyInPortfolioAsync(portfolioId, selectedPropertyId, persistence, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }
        if (unitId is int selectedUnitId && !await persistence.Query<Unit>()
                .AnyAsync(unit => unit.Id == selectedUnitId
                    && unit.Property != null
                    && unit.Property.PortfolioId == portfolioId
                    && (propertyId == null || unit.PropertyId == propertyId), ct))
        {
            throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }
    }

    private static async Task ValidateWorkOrderReferencesAsync(
        int portfolioId,
        ScanWorkOrderTargetData target,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        if (!await IsPropertyInPortfolioAsync(portfolioId, target.PropertyId, persistence, ct))
        {
            throw new ScanConfirmationValidationException("Selected property is not in this portfolio.");
        }
        if (target.UnitId is int unitId && !await persistence.Query<Unit>()
                .AnyAsync(unit => unit.Id == unitId
                    && unit.PropertyId == target.PropertyId
                    && unit.Property != null
                    && unit.Property.PortfolioId == portfolioId, ct))
        {
            throw new ScanConfirmationValidationException("Selected unit is not in this portfolio or property.");
        }
        if (target.VendorId is int vendorId && !await persistence.Query<Vendor>()
                .AnyAsync(vendor => vendor.Id == vendorId && vendor.PortfolioId == portfolioId, ct))
        {
            throw new ScanConfirmationValidationException("Selected vendor is not in this portfolio.");
        }
        if (target.LeaseManagementId is int leaseManagementId && !await persistence.Query<LeaseManagement>()
                .AnyAsync(relationship => relationship.Id == leaseManagementId
                    && relationship.PortfolioId == portfolioId
                    && relationship.PropertyId == target.PropertyId
                    && (target.UnitId == null || relationship.UnitId == target.UnitId), ct))
        {
            throw new ScanConfirmationValidationException("Selected lease relationship is not in this portfolio or location.");
        }
        if (target.TenantId is int tenantId && !await TenantMatchesLocationAsync(
                portfolioId, tenantId, target.PropertyId, target.UnitId, persistence, ct))
        {
            throw new ScanConfirmationValidationException("Selected tenant does not belong to this location.");
        }
    }

    private static Task<bool> TenantMatchesLocationAsync(
        int portfolioId,
        int tenantId,
        int propertyId,
        int? unitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var currentParties =
            from party in persistence.Query<LeaseManagementParty>()
            join lifecycle in persistence.Query<LeaseManagementLifecycleProjection>()
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

    private static async Task<int?> ResolveOrCreateVendorAsync(
        int portfolioId,
        ScanReceiptData receipt,
        DateTime now,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        var name = receipt.VendorName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalizedName = name.ToLowerInvariant();
        var matchSummary = await attempt.Persistence.Query<Vendor>()
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
        attempt.Persistence.Add(vendor);
        await attempt.FlushBusinessAsync(ct);
        return vendor.Id;
    }

    private static Task<bool> IsPropertyInPortfolioAsync(
        int portfolioId,
        int propertyId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) => persistence.Query<Property>()
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
}
