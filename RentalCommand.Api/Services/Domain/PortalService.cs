using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IPortalService"/>
public class PortalService : IPortalService
{
    private readonly RentalCommandDbContext _db;
    private readonly ILeaseQaService _leaseQa;
    private readonly TimeProvider _timeProvider;

    public PortalService(RentalCommandDbContext db, ILeaseQaService leaseQa, TimeProvider timeProvider)
    {
        _db = db;
        _leaseQa = leaseQa;
        _timeProvider = timeProvider;
    }

    public Task<int?> ResolveTenantIdAsync(
        int portfolioId,
        int accessContextId,
        CancellationToken ct = default) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId &&
                access.AccessContextId == accessContextId)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => (int?)access.TenantId)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PortalLeaseRelationshipResponse>> GetLeasesAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        CancellationToken ct = default) =>
        await BuildLeaseRelationshipQuery(portfolioId, accessContextId, tenantId)
            .ToListAsync(ct);

    internal IQueryable<PortalLeaseRelationshipResponse> BuildLeaseRelationshipQuery(
        int portfolioId,
        int accessContextId,
        int tenantId) =>
        from access in EffectiveTenantRelationshipQuery(portfolioId, accessContextId, tenantId)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementPartyId }
            equals new { party.PortfolioId, party.Id }
        join management in _db.LeaseManagements.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { access.PortfolioId, access.LeaseManagementId }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        let selectedAgreementId = lifecycle.CurrentAgreementId ?? lifecycle.UpcomingAgreementId
        from agreement in _db.LeaseAgreements.AsNoTracking()
            .Where(item => item.PortfolioId == access.PortfolioId
                && item.Id == selectedAgreementId)
            .DefaultIfEmpty()
        from agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            .Where(status => status.PortfolioId == access.PortfolioId
                && status.AgreementId == selectedAgreementId)
            .Select(status => new
            {
                AgreementStatus = (string?)status.AgreementStatus,
                IsGoverning = (bool?)status.IsGoverning,
            })
            .DefaultIfEmpty()
        orderby (DateOnly?)agreement!.TermStartOn descending,
            management.Id descending
        select new PortalLeaseRelationshipResponse
        {
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            PortfolioId = management.PortfolioId,
            PropertyId = management.PropertyId,
            UnitId = management.UnitId,
            TenantId = party.TenantId,
            TenantAccountId = lifecycle.TenantAccountId,
            RelationshipNumber = management.RelationshipNumber,
            Lifecycle = lifecycle.Lifecycle,
            PropertyName = management.Property!.Name,
            UnitNumber = management.Unit!.UnitNumber,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
            Agreement = agreement == null
                ? null
                : new PortalLeaseAgreementResponse
                {
                    LeaseAgreementId = agreement.Id,
                    VersionNumber = agreement.VersionNumber,
                    AgreementNumber = agreement.AgreementNumber,
                    AgreementStatus = agreementStatus.AgreementStatus ?? string.Empty,
                    IsGoverning = agreementStatus.IsGoverning ?? false,
                    ChangeType = agreement.ChangeType,
                    TermType = agreement.TermType,
                    TermStartOn = agreement.TermStartOn,
                    TermEndOn = agreement.TermEndOn,
                    BaseRentAmount = agreement.BaseRentAmount,
                    SecurityDepositObligation = agreement.SecurityDepositObligation,
                    LateFeeAmount = agreement.LateFeeAmount,
                    RentDueDay = agreement.RentDueDay,
                    Currency = agreement.Currency,
                    FullyExecutedAtUtc = agreement.FullyExecutedAtUtc,
                    ExecutedStoredFileId = agreement.ExecutedArtifact == null
                        || agreement.ExecutedArtifact.ArtifactKind != LegalDocumentArtifactKind.ExecutedAgreement
                        || agreement.ExecutedArtifact.StoredFile == null
                        || agreement.ExecutedArtifact.StoredFile.DeletedAt != null
                            ? null
                            : agreement.ExecutedArtifact.StoredFileId,
                },
        };

    public async Task<PortalBalanceResponse> GetBalanceAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var financialRelationships = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && (party.Role == LeaseManagementPartyRole.PrimaryTenant
                    || party.Role == LeaseManagementPartyRole.CoTenant
                    || party.Role == LeaseManagementPartyRole.Guarantor));

        // One SQL statement authorizes every account through a party row belonging to this tenant.
        // Receivable and past-due facts come from the database projections; collected cash is the net
        // value of immutable receipt credits after any immutable reversals.
        var rollup = await _db.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.PortfolioId == portfolioId && tenant.Id == tenantId)
            .Select(_ => new
            {
                Collected = _db.TenantLedgerEntries
                    .Where(entry => entry.PortfolioId == portfolioId
                        && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == entry.TenantAccount!.LeaseManagementId))
                    .Sum(entry => (decimal?)(entry.Amount -
                        (_db.TenantLedgerEntries
                            .Where(reversal => reversal.PortfolioId == portfolioId
                                && reversal.TenantAccountId == entry.TenantAccountId
                                && reversal.EntryType == TenantLedgerEntryType.Reversal
                                && reversal.ReversesEntryId == entry.Id)
                            .Sum(reversal => (decimal?)reversal.Amount) ?? 0m))) ?? 0m,
                Outstanding = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (decimal?)balance.ReceivableBalance) ?? 0m,
                Overdue = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (decimal?)balance.PastDueAmount) ?? 0m,
                OverdueCount = _db.TenantAccountBalanceProjections
                    .Where(balance => balance.PortfolioId == portfolioId
                        && financialRelationships.Any(party =>
                            party.LeaseManagementId == balance.LeaseManagementId))
                    .Sum(balance => (int?)balance.PastDueCount) ?? 0,
            })
            .SingleOrDefaultAsync(ct);

        return new PortalBalanceResponse
        {
            TenantId = tenantId,
            Collected = rollup?.Collected ?? 0m,
            Outstanding = rollup?.Outstanding ?? 0m,
            Overdue = rollup?.Overdue ?? 0m,
            OverdueCount = rollup?.OverdueCount ?? 0,
        };
    }

    public async Task<IReadOnlyList<PortalPaymentResponse>> GetPaymentsAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var financialRelationships = _db.LeaseManagementParties
            .AsNoTracking()
            .Where(party => party.PortfolioId == portfolioId
                && party.TenantId == tenantId
                && (party.Role == LeaseManagementPartyRole.PrimaryTenant
                    || party.Role == LeaseManagementPartyRole.CoTenant
                    || party.Role == LeaseManagementPartyRole.Guarantor));

        var payments = await (
            from charge in _db.TenantChargeBalanceProjections.AsNoTracking()
            join account in _db.TenantAccounts.AsNoTracking()
                on new { charge.PortfolioId, charge.TenantAccountId }
                equals new { account.PortfolioId, TenantAccountId = account.Id }
            where charge.PortfolioId == portfolioId
                && financialRelationships.Any(party =>
                    party.LeaseManagementId == account.LeaseManagementId)
            orderby (charge.DueOn ?? charge.EffectiveOn) descending, charge.TenantLedgerEntryId descending
            select new PortalPaymentResponse
            {
                Id = charge.TenantLedgerEntryId,
                TenantAccountId = account.Id,
                LeaseManagementId = account.LeaseManagementId,
                PaymentType = charge.EntryType == "RentCharge" ? "Rent"
                    : charge.EntryType == "DepositCharge" ? "SecurityDeposit"
                    : charge.EntryType == "LateFeeCharge" ? "LateFee"
                    : "Other",
                Status = charge.OriginalAmount - charge.ReversedAmount <= 0m ? "Waived"
                    : charge.OpenAmount <= 0m ? "Paid"
                    : charge.NetAllocations > 0m ? "Partial"
                    : charge.IsPastDue ? "Late"
                    : "Scheduled",
                Amount = charge.OriginalAmount - charge.ReversedAmount,
                DueDate = (charge.DueOn ?? charge.EffectiveOn)
                    .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                PaidDate = charge.OpenAmount <= 0m
                    ? _db.TenantLedgerAllocations
                        .Where(allocation => allocation.PortfolioId == portfolioId
                            && allocation.TenantAccountId == account.Id
                            && allocation.DebitEntryId == charge.TenantLedgerEntryId
                            && allocation.CreditEntry!.EntryType == TenantLedgerEntryType.PaymentReceipt)
                        .Max(allocation => (DateTime?)allocation.CreditEntry!.PostedAtUtc)
                    : null,
                Method = _db.TenantLedgerAllocations
                    .Where(allocation => allocation.PortfolioId == portfolioId
                        && allocation.TenantAccountId == account.Id
                        && allocation.DebitEntryId == charge.TenantLedgerEntryId
                        && allocation.CreditEntry!.EntryType == TenantLedgerEntryType.PaymentReceipt)
                    .OrderByDescending(allocation => allocation.AllocatedAtUtc)
                    .ThenByDescending(allocation => allocation.Id)
                    .Select(allocation => allocation.CreditEntry!.ProviderPaymentAttempt == null
                        ? null
                        : allocation.CreditEntry.ProviderPaymentAttempt.PaymentMethodSummary)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return payments;
    }

    public async Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow();

        return await BuildAppointmentsQuery(portfolioId, accessContextId, tenantId, now)
            .ToListAsync(ct);
    }

    internal IQueryable<AppointmentResponse> BuildAppointmentsQuery(
        int portfolioId,
        int accessContextId,
        int tenantId,
        DateTime now)
    {
        var effectiveRelationships = EffectiveTenantRelationshipQuery(
            portfolioId, accessContextId, tenantId);

        return _db.Appointments
            .AsNoTracking()
            .Where(appointment => appointment.PortfolioId == portfolioId
                && appointment.TenantId == tenantId
                && appointment.ScheduledStart >= now
                && (appointment.Status == AppointmentStatus.Scheduled
                    || appointment.Status == AppointmentStatus.Confirmed)
                && effectiveRelationships.Any(access =>
                    appointment.LeaseManagementId == null
                    || access.LeaseManagementId == appointment.LeaseManagementId))
            .OrderBy(appointment => appointment.ScheduledStart)
            .ThenBy(appointment => appointment.Id)
            .Take(50)
            .Select(appointment => new AppointmentResponse
            {
                Id = appointment.Id,
                PortfolioId = appointment.PortfolioId,
                PropertyId = appointment.PropertyId,
                UnitId = appointment.UnitId,
                LeaseManagementId = appointment.LeaseManagementId,
                TenantId = appointment.TenantId,
                Title = appointment.Title,
                ProspectName = appointment.ProspectName,
                ProspectEmail = appointment.ProspectEmail,
                Type = appointment.Type,
                Status = appointment.Status,
                ScheduledStart = appointment.ScheduledStart,
                ScheduledEnd = appointment.ScheduledEnd,
                AssignedTo = appointment.AssignedTo,
                Notes = appointment.Notes,
                CreatedAt = appointment.CreatedAt,
                UpdatedAt = appointment.UpdatedAt,
                PropertyName = appointment.Property == null
                    ? (from access in effectiveRelationships
                       join management in _db.LeaseManagements.AsNoTracking()
                           on new { access.PortfolioId, Id = access.LeaseManagementId }
                           equals new { management.PortfolioId, management.Id }
                       join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                           on new { access.PortfolioId, access.LeaseManagementId }
                           equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                       where appointment.LeaseManagementId == null
                           || appointment.LeaseManagementId == management.Id
                       orderby lifecycle.CurrentAgreementId != null descending,
                           management.PossessionGivenAtUtc descending,
                           management.Id descending
                       select management.Property!.Name)
                        .FirstOrDefault()
                    : appointment.Property.Name,
                UnitNumber = appointment.Unit == null
                    ? (from access in effectiveRelationships
                       join management in _db.LeaseManagements.AsNoTracking()
                           on new { access.PortfolioId, Id = access.LeaseManagementId }
                           equals new { management.PortfolioId, management.Id }
                       join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
                           on new { access.PortfolioId, access.LeaseManagementId }
                           equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
                       where appointment.LeaseManagementId == null
                           || appointment.LeaseManagementId == management.Id
                       orderby lifecycle.CurrentAgreementId != null descending,
                           management.PossessionGivenAtUtc descending,
                           management.Id descending
                       select management.Unit!.UnitNumber)
                        .FirstOrDefault()
                    : appointment.Unit.UnitNumber,
                TenantName = appointment.Tenant == null
                    ? null
                    : (appointment.Tenant.FirstName + " " + appointment.Tenant.LastName).Trim(),
            });
    }

    public async Task<IReadOnlyList<WorkOrderResponse>> GetWorkOrdersAsync(int portfolioId, int tenantId, CancellationToken ct = default)
    {
        var workOrders = await _db.WorkOrders
            .AsNoTracking()
            .Where(w => w.PortfolioId == portfolioId && w.TenantId == tenantId)
            .OrderByDescending(w => w.RequestedAt)
            .ToListAsync(ct);

        return workOrders.Select(WorkOrderResponse.FromEntity).ToList();
    }

    public async Task<WorkOrderDetailResponse?> GetWorkOrderDetailAsync(
        int portfolioId, int tenantId, int workOrderId, CancellationToken ct = default)
    {
        // Ownership is part of the lookup: a work order on another tenant's lease/unit simply isn't
        // found, so we never leak its existence or its timeline. Mirrors the lease-ledger restriction.
        var workOrder = await _db.WorkOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                w => w.Id == workOrderId && w.PortfolioId == portfolioId && w.TenantId == tenantId, ct);
        if (workOrder is null)
        {
            return null;
        }

        var events = await _db.WorkOrderStatusEvents
            .AsNoTracking()
            .Where(e => e.WorkOrderId == workOrderId && e.PortfolioId == portfolioId)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        return WorkOrderDetailResponse.FromEntity(workOrder, events);
    }

    public async Task<WorkOrderResponse?> CreateTenantWorkOrderAsync(
        int portfolioId,
        int tenantId,
        CreateTenantWorkOrderRequest request,
        CancellationToken ct = default)
    {
        var relationship = await _db.LeaseManagementParties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolioId && p.TenantId == tenantId
                && p.LeaseManagement != null
                && p.LeaseManagement.CanceledAtUtc == null
                && p.LeaseManagement.PossessionReturnedAtUtc == null)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => new { p.LeaseManagementId, p.LeaseManagement!.PropertyId, p.LeaseManagement.UnitId })
            .FirstOrDefaultAsync(ct);

        if (relationship is null)
        {
            return null;
        }

        var now = _timeProvider.UtcNow();
        var workOrder = new WorkOrder
        {
            PortfolioId = portfolioId,
            PropertyId = relationship.PropertyId,
            UnitId = relationship.UnitId,
            TenantId = tenantId,
            LeaseManagementId = relationship.LeaseManagementId,
            Title = request.Title.Trim(),
            Description = request.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? "Resident Request" : request.Category.Trim(),
            Priority = request.Priority,
            Status = WorkOrderStatus.New,
            RequestedAt = now,
            CreatedBy = "Tenant",
            UpdatedAt = now,
        };

        // Seed the timeline with the initial null → New event so the tenant's own request shows a
        // status stream from the moment they submit it.
        workOrder.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = portfolioId,
            FromStatus = null,
            ToStatus = workOrder.Status,
            Note = null,
            ChangedByUserId = null,
            ChangedByLabel = "Tenant",
            CreatedAtUtc = now,
        });

        _db.WorkOrders.Add(workOrder);
        await _db.SaveChangesAsync(ct);

        return WorkOrderResponse.FromEntity(workOrder);
    }

    public async Task<LeaseQuestionResponse?> AskLeaseAsync(
        int portfolioId,
        int accessContextId,
        int tenantId,
        int? leaseManagementId,
        string question,
        CancellationToken ct = default)
    {
        var resolvedLeaseManagementId = await BuildOwnedLeaseManagementQuery(
                portfolioId, accessContextId, tenantId, leaseManagementId)
            .FirstOrDefaultAsync(ct);
        if (resolvedLeaseManagementId == null)
        {
            return null;
        }

        return await _leaseQa.AskAsync(
            portfolioId, resolvedLeaseManagementId.Value, question, ct);
    }

    public async Task<AutopayStatusResponse?> GetAutopayStatusAsync(
        int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default)
    {
        var businessDate = DateOnly.FromDateTime(_timeProvider.UtcNow());
        return await OwnedAutopayQuery(portfolioId, tenantId, tenantAccountId, businessDate)
            .AsNoTracking()
            .Select(row => new AutopayStatusResponse
            {
                TenantAccountId = row.Account.Id,
                Active = row.Enrollment != null,
                EnrolledAt = row.Enrollment == null ? null : row.Enrollment.EnrolledAtUtc,
                OnlinePaymentsAvailable = true,
            })
            .FirstOrDefaultAsync(ct);
    }

    public async Task<AutopayStatusResponse?> CancelAutopayAsync(
        int portfolioId, int tenantId, int tenantAccountId, CancellationToken ct = default)
    {
        var businessDate = DateOnly.FromDateTime(_timeProvider.UtcNow());
        var target = await OwnedAutopayQuery(portfolioId, tenantId, tenantAccountId, businessDate)
            .FirstOrDefaultAsync(ct);
        if (target == null) return null;
        if (target.Enrollment != null)
        {
            target.Enrollment.CanceledAtUtc = _timeProvider.UtcNow();
            target.Enrollment.CancelReason = "Canceled by tenant";
            await _db.SaveChangesAsync(ct);
        }

        return new AutopayStatusResponse
        {
            TenantAccountId = tenantAccountId,
            Active = false,
            OnlinePaymentsAvailable = true,
        };
    }

    private IQueryable<OwnedAutopayRow> OwnedAutopayQuery(
        int portfolioId, int tenantId, int tenantAccountId, DateOnly businessDate) =>
        from account in _db.TenantAccounts
        join party in _db.LeaseManagementParties
            on new { account.LeaseManagementId, account.PortfolioId }
            equals new { party.LeaseManagementId, party.PortfolioId }
        join access in _db.TenantUserAccesses
            on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
            equals new { access.LeaseManagementPartyId, access.PortfolioId }
        from enrollment in _db.TenantAutopayEnrollments
            .Where(item => item.TenantAccountId == account.Id
                && item.PortfolioId == account.PortfolioId
                && item.CanceledAtUtc == null)
            .DefaultIfEmpty()
        where account.Id == tenantAccountId
            && account.PortfolioId == portfolioId
            && account.ClosedAtUtc == null
            && party.TenantId == tenantId
            && party.EffectiveFrom <= businessDate
            && (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate)
            && party.Role != LeaseManagementPartyRole.Occupant
            && access.RevokedAtUtc == null
        orderby party.Id
        select new OwnedAutopayRow(account, enrollment);

    private sealed record OwnedAutopayRow(TenantAccount Account, TenantAutopayEnrollment? Enrollment);

    internal IQueryable<int?> BuildOwnedLeaseManagementQuery(
        int portfolioId,
        int accessContextId,
        int tenantId,
        int? leaseManagementId) =>
        from access in EffectiveTenantRelationshipQuery(portfolioId, accessContextId, tenantId)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementPartyId }
            equals new { party.PortfolioId, party.Id }
        join management in _db.LeaseManagements.AsNoTracking()
            on new { access.PortfolioId, Id = access.LeaseManagementId }
            equals new { management.PortfolioId, management.Id }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { access.PortfolioId, access.LeaseManagementId }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        where (leaseManagementId == null || management.Id == leaseManagementId)
            && lifecycle.CurrentAgreementId != null
            && party.TenantId == tenantId
        orderby (lifecycle.Lifecycle == "Occupied" || lifecycle.Lifecycle == "Ending") descending,
            management.PossessionGivenAtUtc descending,
            management.Id descending
        select (int?)management.Id;

    private IQueryable<RentalCommand.Data.Authorization.EffectiveTenantAccessProjection> EffectiveTenantRelationshipQuery(
        int portfolioId,
        int accessContextId,
        int tenantId) =>
        _db.EffectiveTenantAccess
            .AsNoTracking()
            .Where(access => access.PortfolioId == portfolioId
                && access.AccessContextId == accessContextId
                && access.TenantId == tenantId);
}
