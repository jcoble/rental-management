using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public sealed class LeaseManagementQueryService : ILeaseManagementQueryService
{
    private const int DefaultLedgerPageSize = 50;
    private const int MaxLedgerPageSize = 200;
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;

    public LeaseManagementQueryService(RentalCommandDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<LeaseManagementListResponse> ListPageAsync(
        LeaseManagementReadContext access,
        LeaseManagementListQuery query,
        CancellationToken ct = default)
    {
        var rows = BuildSummaryQuery(access, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplySort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeaseManagementListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public Task<DateOnly> GetPortfolioBusinessDateAsync(
        LeaseManagementReadContext access,
        CancellationToken ct = default) =>
        _db.Database
            .SqlQuery<DateOnly>($"SELECT rc_business_date({access.PortfolioId}) AS \"Value\"")
            .SingleAsync(ct);

    public async Task<LeaseManagementDetailResponse?> GetAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default)
    {
        var header = await BuildDetailHeaderQuery(access, leaseManagementId).SingleOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        var parties = await BuildPartyQuery(access, leaseManagementId)
            .OrderBy(party => party.EffectiveThrough != null)
            .ThenBy(party => party.Role)
            .ThenBy(party => party.TenantName)
            .ThenBy(party => party.LeaseManagementPartyId)
            .ToListAsync(ct);
        var legalHistoryCounts = await BuildLegalHistoryCountsQuery(access, leaseManagementId)
            .SingleAsync(ct);

        return new LeaseManagementDetailResponse
        {
            Summary = header,
            EndingDisposition = header.EndingDisposition,
            EndingDispositionDecidedAtUtc = header.EndingDispositionDecidedAtUtc,
            EndingDispositionDecidedByUserId = header.EndingDispositionDecidedByUserId,
            NoticeGivenAtUtc = header.NoticeGivenAtUtc,
            CancellationReasonCode = header.CancellationReasonCode,
            CancellationNote = header.CancellationNote,
            Parties = parties,
            AgreementCount = legalHistoryCounts.AgreementCount,
            AddendumCount = legalHistoryCounts.AddendumCount,
            LegalArtifactCount = legalHistoryCounts.LegalArtifactCount,
        };
    }

    public Task<bool> CanReadAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default) =>
        BuildLeaseWorkspaceReadManagementQuery(access)
            .AnyAsync(management => management.Id == leaseManagementId, ct);

    public Task<LeaseQaAgreementFacts?> GetLeaseQaAgreementAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default) =>
        BuildLeaseQaAgreementQuery(access, leaseManagementId).SingleOrDefaultAsync(ct);

    internal IQueryable<LeaseQaAgreementFacts> BuildLeaseQaAgreementQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
        join status in _db.LeaseAgreementStatusProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { status.PortfolioId, status.LeaseManagementId }
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { status.PortfolioId, AgreementId = status.AgreementId }
            equals new { agreement.PortfolioId, AgreementId = agreement.Id }
        where management.Id == leaseManagementId
            && status.IsGoverning
            && agreement.FullyExecutedAtUtc != null
            && agreement.VoidedAtUtc == null
            && agreement.ExecutedArtifact != null
            && agreement.ExecutedArtifact.ArtifactKind == LegalDocumentArtifactKind.ExecutedAgreement
            && agreement.ExecutedArtifact.StoredFile != null
            && agreement.ExecutedArtifact.StoredFile.PortfolioId == access.PortfolioId
            && agreement.ExecutedArtifact.StoredFile.DeletedAt == null
        select new LeaseQaAgreementFacts(
            agreement.Id,
            agreement.LeaseManagementId,
            agreement.AgreementNumber,
            agreement.TermStartOn,
            agreement.TermEndOn,
            agreement.BaseRentAmount,
            agreement.SecurityDepositObligation,
            agreement.LateFeeAmount,
            agreement.RentDueDay,
            agreement.TermsPayload,
            agreement.ExecutedArtifact!.StoredFileId,
            agreement.ExecutedArtifact.StoredFile!.FileName);

    public async Task<IReadOnlyList<int>> ListAuthorizedAgreementIssueSignerIdsAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct = default) =>
        await BuildAuthorizedAgreementIssueSignerQuery(access, leaseManagementId, leaseAgreementId)
            .ToArrayAsync(ct);

    internal IQueryable<int> BuildAuthorizedAgreementIssueSignerQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId) =>
        BuildAgreementPreparationManagementQuery(access)
            .Where(management => management.Id == leaseManagementId)
            .SelectMany(management => management.Agreements)
            .Where(agreement => agreement.Id == leaseAgreementId)
            .SelectMany(agreement => agreement.Signers)
            .OrderBy(signer => signer.SigningOrder)
            .ThenBy(signer => signer.Id)
            .Select(signer => signer.Id);

    public async Task<IReadOnlyList<int>> ListAuthorizedAddendumIssueSignerIdsAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId,
        CancellationToken ct = default) =>
        await BuildAuthorizedAddendumIssueSignerQuery(access, leaseManagementId, leaseAddendumId)
            .ToArrayAsync(ct);

    internal IQueryable<int> BuildAuthorizedAddendumIssueSignerQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId) =>
        BuildAgreementPreparationManagementQuery(access)
            .Where(management => management.Id == leaseManagementId)
            .SelectMany(management => management.Addenda)
            .Where(addendum => addendum.Id == leaseAddendumId)
            .SelectMany(addendum => addendum.Signers)
            .OrderBy(signer => signer.SigningOrder)
            .ThenBy(signer => signer.Id)
            .Select(signer => signer.Id);

    private IQueryable<LeaseManagement> BuildAgreementPreparationManagementQuery(
        LeaseManagementReadContext access) =>
        BuildAuthorizedManagementQuery(
            access,
            [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingAgreementsPrepare]);

    private IQueryable<LeaseManagement> BuildLeaseWorkspaceReadManagementQuery(
        LeaseManagementReadContext access) =>
        BuildAuthorizedManagementQuery(
            access,
            [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingAgreementsPrepare]);

    public async Task<ReturnPossessionContextResponse> GetReturnPossessionContextAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default)
    {
        var parties = await BuildCurrentPartiesQuery(access, leaseManagementId).ToListAsync(ct);
        var activeTenantUserAccesses = await BuildCurrentPartyAccessQuery(access, leaseManagementId)
            .ToListAsync(ct);

        return new ReturnPossessionContextResponse
        {
            Parties = parties,
            ActiveTenantUserAccesses = activeTenantUserAccesses,
        };
    }

    public async Task<LeaseAgreementHistoryPageResponse?> ListAgreementHistoryPageAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        LeaseLegalHistoryQuery query,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync(access, leaseManagementId, ct))
        {
            return null;
        }

        var rows = BuildAgreementHistoryQuery(access, leaseManagementId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyAgreementSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeaseAgreementHistoryPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeaseAgreementDraftDetailResponse?> GetAgreementDraftAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct = default)
    {
        var row = await BuildAgreementDraftDetailQuery(access, leaseManagementId, leaseAgreementId)
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        using var terms = JsonDocument.Parse(row.TermsPayloadJson);
        return new LeaseAgreementDraftDetailResponse
        {
            LeaseManagementId = row.LeaseManagementId,
            LeaseAgreementId = row.LeaseAgreementId,
            PublicId = row.PublicId,
            VersionNumber = row.VersionNumber,
            DraftRevision = row.DraftRevision,
            AgreementNumber = row.AgreementNumber,
            ChangeType = row.ChangeType,
            CorrectionReason = row.CorrectionReason,
            TermType = row.TermType,
            TermStartOn = row.TermStartOn,
            TermEndOn = row.TermEndOn,
            GoverningFromOn = row.GoverningFromOn,
            BaseRentAmount = row.BaseRentAmount,
            RentDueDay = row.RentDueDay,
            SecurityDepositObligation = row.SecurityDepositObligation,
            LateFeeAmount = row.LateFeeAmount,
            GracePeriodDays = row.GracePeriodDays,
            Currency = row.Currency,
            TermsSchemaVersion = row.TermsSchemaVersion,
            TermsPayload = terms.RootElement.Clone(),
            DocumentSourceVersionId = row.DocumentSourceVersionId,
            DocumentTemplateId = row.DocumentTemplateId,
            DocumentTemplateVersion = row.DocumentTemplateVersion,
            SourceAgreement = row.SourceAgreement,
            Signers = row.Signers,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
        };
    }

    public async Task<LeasePartyLegalBasisPageResponse?> ListEligiblePartyLegalBasisPageAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        ListQuery query,
        CancellationToken ct = default)
    {
        if (!await BuildAgreementPreparationManagementQuery(access)
                .AnyAsync(management => management.Id == leaseManagementId, ct))
        {
            return null;
        }

        var rows = BuildEligiblePartyLegalBasisQuery(access, leaseManagementId, query);

        var totalCount = await rows.CountAsync(ct);
        var ordered = query.SortDescending
            ? rows.OrderBy(item => item.FullyExecutedAtUtc).ThenBy(item => item.LeaseAgreementId)
            : rows.OrderByDescending(item => item.FullyExecutedAtUtc).ThenByDescending(item => item.LeaseAgreementId);
        var items = await ordered
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeasePartyLegalBasisPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    internal IQueryable<LeasePartyLegalBasisResponse> BuildEligiblePartyLegalBasisQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        ListQuery query) =>
            from management in BuildAgreementPreparationManagementQuery(access)
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { agreement.PortfolioId, agreement.LeaseManagementId }
            where management.Id == leaseManagementId
                && (agreement.ChangeType == LeaseAgreementChangeType.Correction
                    || agreement.ChangeType == LeaseAgreementChangeType.Restatement)
            where agreement.FullyExecutedAtUtc != null
                && agreement.ExecutedArtifactId != null
                && agreement.VoidedAtUtc == null
                && agreement.DraftCanceledAtUtc == null
                && (string.IsNullOrWhiteSpace(query.Search)
                    || EF.Functions.ILike(agreement.AgreementNumber, $"%{query.Search.Trim()}%")
                    || (agreement.CorrectionReason != null
                        && EF.Functions.ILike(agreement.CorrectionReason, $"%{query.Search.Trim()}%")))
            select new LeasePartyLegalBasisResponse
            {
                LeaseAgreementId = agreement.Id,
                AgreementNumber = agreement.AgreementNumber,
                VersionNumber = agreement.VersionNumber,
                ChangeType = agreement.ChangeType,
                CorrectionReason = agreement.CorrectionReason,
                FullyExecutedAtUtc = agreement.FullyExecutedAtUtc!.Value,
            };

    public Task<LeaseAgreementSignatureProgressResponse?> GetAgreementSignatureProgressAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct = default) =>
        BuildAgreementSignatureProgressQuery(access, leaseManagementId, leaseAgreementId)
            .SingleOrDefaultAsync(ct);

    public Task<LeaseAgreementEffectiveAddendumSeriesResponse?> GetEffectiveAddendumSeriesAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int sourceAgreementId,
        CancellationToken ct = default) =>
        BuildEffectiveAddendumSeriesQuery(access, leaseManagementId, sourceAgreementId)
            .SingleOrDefaultAsync(ct);

    public async Task<LeaseAddendumHistoryPageResponse?> ListAddendumHistoryPageAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        LeaseLegalHistoryQuery query,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync(access, leaseManagementId, ct))
        {
            return null;
        }

        var rows = BuildAddendumHistoryQuery(access, leaseManagementId, query);
        var totalCount = await rows.CountAsync(ct);
        var items = await ApplyAddendumSort(rows, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeaseAddendumHistoryPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeaseAddendumEligibleBaseAgreementPageResponse?> ListAddendumEligibleBaseAgreementsAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        ListQuery query,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync(access, leaseManagementId, ct))
        {
            return null;
        }

        var rows = BuildAddendumEligibleBaseAgreementQuery(access, leaseManagementId);
        var totalCount = await rows.CountAsync(ct);
        var items = await rows
            .OrderByDescending(item => item.GoverningFromOn)
            .ThenByDescending(item => item.VersionNumber)
            .ThenByDescending(item => item.LeaseAgreementId)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .ToListAsync(ct);
        return new LeaseAddendumEligibleBaseAgreementPageResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<LeaseAddendumSignerCandidatesResponse?> GetAddendumSignerCandidatesAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        CancellationToken ct = default)
    {
        if (!await CanReadAsync(access, leaseManagementId, ct))
        {
            return null;
        }

        return new LeaseAddendumSignerCandidatesResponse
        {
            Items = await BuildCurrentPartiesQuery(access, leaseManagementId).ToListAsync(ct),
        };
    }

    public async Task<LeaseAddendumDraftDetailResponse?> GetAddendumDraftAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId,
        CancellationToken ct = default)
    {
        var row = await BuildAddendumDraftDetailQuery(access, leaseManagementId, leaseAddendumId)
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        using var terms = JsonDocument.Parse(row.TermsPayloadJson);
        return new LeaseAddendumDraftDetailResponse
        {
            LeaseManagementId = row.LeaseManagementId,
            LeaseAddendumId = row.LeaseAddendumId,
            PublicId = row.PublicId,
            SeriesPublicId = row.SeriesPublicId,
            BaseAgreementId = row.BaseAgreementId,
            BaseAgreementPublicId = row.BaseAgreementPublicId,
            BaseAgreementNumber = row.BaseAgreementNumber,
            BaseAgreementCurrency = row.BaseAgreementCurrency,
            VersionNumber = row.VersionNumber,
            DraftRevision = row.DraftRevision,
            AddendumNumber = row.AddendumNumber,
            Purpose = row.Purpose,
            SourceAddendumId = row.SourceAddendumId,
            EffectiveFromOn = row.EffectiveFromOn,
            EffectiveThroughOn = row.EffectiveThroughOn,
            TermsSchemaVersion = row.TermsSchemaVersion,
            TermsPayload = terms.RootElement.Clone(),
            DocumentSourceVersionId = row.DocumentSourceVersionId,
            DocumentTemplateId = row.DocumentTemplateId,
            DocumentTemplateVersion = row.DocumentTemplateVersion,
            Signers = row.Signers,
            FinancialEffects = row.FinancialEffects,
            CreatedAtUtc = row.CreatedAtUtc,
            UpdatedAtUtc = row.UpdatedAtUtc,
        };
    }

    public Task<LegalArtifactFileReference?> GetAgreementArtifactAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        int artifactId,
        CancellationToken ct = default) =>
        BuildAgreementArtifactFileQuery(access, leaseManagementId, leaseAgreementId, artifactId)
            .SingleOrDefaultAsync(ct);

    public Task<LegalArtifactFileReference?> GetAgreementSourceScanAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        CancellationToken ct = default) =>
        BuildAgreementSourceScanFileQuery(access, leaseManagementId, leaseAgreementId)
            .OrderByDescending(file => file.UploadedAtUtc)
            .Select(file => new LegalArtifactFileReference(
                file.StoredFileId, file.StorageKey, file.FileName, file.ContentType))
            .FirstOrDefaultAsync(ct);

    public Task<LegalArtifactFileReference?> GetAddendumArtifactAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId,
        int artifactId,
        CancellationToken ct = default) =>
        BuildAddendumArtifactFileQuery(access, leaseManagementId, leaseAddendumId, artifactId)
            .SingleOrDefaultAsync(ct);

    public async Task<LeaseLedgerResponse?> GetLedgerAsync(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int skip = 0,
        int? take = null,
        CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        var pageSize = Math.Clamp(take ?? DefaultLedgerPageSize, 1, MaxLedgerPageSize);
        var header = await BuildCanonicalLedgerHeaderQuery(access, leaseManagementId)
            .SingleOrDefaultAsync(ct);
        if (header is null)
        {
            return null;
        }

        var entries = await BuildCanonicalLedgerEntriesQuery(access.PortfolioId, header.TenantAccountId)
            .OrderByDescending(entry => entry.EffectiveOn)
            .ThenByDescending(entry => entry.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);
        var opening = await BuildOpeningEntryQuery(access.PortfolioId, header.TenantAccountId)
            .FirstOrDefaultAsync(ct);

        return new LeaseLedgerResponse
        {
            LeaseManagementId = header.LeaseManagementId,
            TenantAccountId = header.TenantAccountId,
            AccountNumber = header.AccountNumber,
            TenantName = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
            PropertyName = header.PropertyName,
            TotalCharged = header.TotalDebits,
            TotalPaid = header.TotalCredits,
            Balance = header.ReceivableBalance,
            PastDueCount = header.PastDueCount,
            Opening = opening is null ? null : ToLedgerResponse(opening, header, "Opening"),
            Entries = entries.Select(entry => ToLedgerResponse(entry, header)).ToList(),
            TotalCount = header.TotalEntryCount,
            Skip = skip,
            Take = pageSize,
        };
    }

    internal IQueryable<LeaseManagementSummaryResponse> BuildSummaryQuery(
        LeaseManagementReadContext access,
        LeaseManagementListQuery query)
    {
        var rows = BuildSummaryBaseQuery(access, CapabilityKeys.RentalsRead);
        if (query.PropertyId is int propertyId)
            rows = rows.Where(row => row.PropertyId == propertyId);
        if (query.UnitId is int unitId)
            rows = rows.Where(row => row.UnitId == unitId);
        if (query.TenantId is int tenantId)
            rows = rows.Where(row => _db.LeaseManagementParties.Any(party =>
                party.PortfolioId == access.PortfolioId
                && party.LeaseManagementId == row.LeaseManagementId
                && party.TenantId == tenantId));
        if (!string.IsNullOrWhiteSpace(query.Lifecycle))
        {
            var lifecycle = query.Lifecycle.Trim();
            rows = rows.Where(row => row.Lifecycle == lifecycle);
        }
        if (query.HasReconciliationException is bool hasException)
            rows = rows.Where(row => row.HasReconciliationException == hasException);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row =>
                EF.Functions.ILike(row.RelationshipNumber, $"%{term}%")
                || EF.Functions.ILike(row.PropertyName, $"%{term}%")
                || EF.Functions.ILike(row.UnitNumber, $"%{term}%")
                || (row.PrimaryTenantName != null && EF.Functions.ILike(row.PrimaryTenantName, $"%{term}%"))
                || (row.AgreementNumber != null && EF.Functions.ILike(row.AgreementNumber, $"%{term}%")));
        }
        return rows;
    }

    private IQueryable<LeaseManagementSummaryResponse> BuildSummaryBaseQuery(
        LeaseManagementReadContext access,
        string capabilityKey) =>
        from management in BuildAuthorizedManagementQuery(access, capabilityKey)
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
            equals new { agreement.PortfolioId, AgreementId = (int?)agreement.Id } into agreements
        from agreement in agreements.DefaultIfEmpty()
        join agreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.CurrentAgreementId }
            equals new { agreementStatus.PortfolioId, AgreementId = (int?)agreementStatus.AgreementId } into statuses
        from agreementStatus in statuses.DefaultIfEmpty()
        join upcomingAgreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.UpcomingAgreementId }
            equals new { upcomingAgreement.PortfolioId, AgreementId = (int?)upcomingAgreement.Id }
            into upcomingAgreements
        from upcomingAgreement in upcomingAgreements.DefaultIfEmpty()
        join upcomingAgreementStatus in _db.LeaseAgreementStatusProjections.AsNoTracking()
            on new { management.PortfolioId, AgreementId = lifecycle.UpcomingAgreementId }
            equals new
            {
                upcomingAgreementStatus.PortfolioId,
                AgreementId = (int?)upcomingAgreementStatus.AgreementId,
            }
            into upcomingStatuses
        from upcomingAgreementStatus in upcomingStatuses.DefaultIfEmpty()
        select new LeaseManagementSummaryResponse
        {
            LeaseManagementId = management.Id,
            LeaseManagementPublicId = management.PublicId,
            RelationshipNumber = management.RelationshipNumber,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            UnitId = management.UnitId,
            UnitNumber = management.Unit!.UnitNumber,
            Lifecycle = lifecycle.Lifecycle,
            BusinessDate = lifecycle.BusinessDate,
            LeaseAgreementId = lifecycle.CurrentAgreementId,
            AgreementNumber = agreement == null ? null : agreement.AgreementNumber,
            AgreementStatus = agreementStatus == null ? null : agreementStatus.AgreementStatus,
            TermStartOn = agreement == null ? null : agreement.TermStartOn,
            TermEndOn = agreement == null ? null : agreement.TermEndOn,
            BaseRentAmount = agreement == null ? null : agreement.BaseRentAmount,
            UpcomingLeaseAgreementId = lifecycle.UpcomingAgreementId,
            UpcomingAgreementNumber = upcomingAgreement == null
                ? null
                : upcomingAgreement.AgreementNumber,
            UpcomingAgreementStatus = upcomingAgreementStatus == null
                ? null
                : upcomingAgreementStatus.AgreementStatus,
            UpcomingTermStartOn = upcomingAgreement == null
                ? null
                : upcomingAgreement.TermStartOn,
            UpcomingTermEndOn = upcomingAgreement == null
                ? null
                : upcomingAgreement.TermEndOn,
            TenantAccountId = lifecycle.TenantAccountId,
            PrimaryTenantId = lifecycle.CurrentPrimaryTenantId,
            PrimaryTenantName = lifecycle.CurrentPrimaryTenantName,
            CurrentPartyCount = lifecycle.CurrentPartyCount,
            CurrentResidentCount = lifecycle.CurrentResidentCount,
            CurrentFinanciallyResponsiblePartyCount = lifecycle.CurrentFinanciallyResponsiblePartyCount,
            HasReconciliationException = lifecycle.HasReconciliationException,
            PlannedPossessionAtUtc = management.PlannedPossessionAtUtc,
            PossessionGivenAtUtc = management.PossessionGivenAtUtc,
            PlannedMoveOutAtUtc = management.PlannedMoveOutAtUtc,
            PossessionReturnedAtUtc = management.PossessionReturnedAtUtc,
            AccountClosedAtUtc = management.AccountClosedAtUtc,
            CanceledAtUtc = management.CanceledAtUtc,
            EndingDisposition = management.EndingDisposition,
            EndingDispositionDecidedAtUtc = management.EndingDispositionDecidedAtUtc,
            EndingDispositionDecidedByUserId = management.EndingDispositionDecidedByUserId,
            NoticeGivenAtUtc = management.NoticeGivenAtUtc,
            CancellationReasonCode = management.CancellationReasonCode,
            CancellationNote = management.CancellationNote,
            UpdatedAtUtc = management.UpdatedAtUtc,
        };

    internal IQueryable<LeaseManagementSummaryResponse> BuildDetailHeaderQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        BuildSummaryBaseQuery(access, CapabilityKeys.RentalsRead)
            .Where(summary => summary.LeaseManagementId == leaseManagementId);

    internal IQueryable<LeaseManagementPartyResponse> BuildPartyQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { party.PortfolioId, party.LeaseManagementId }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        where management.Id == leaseManagementId
        select new LeaseManagementPartyResponse
        {
            LeaseManagementPartyId = party.Id,
            LeaseManagementId = party.LeaseManagementId,
            TenantId = party.TenantId,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant!.LastName).Trim(),
            Email = party.Tenant.Email,
            Phone = party.Tenant.Phone,
            Role = party.Role,
            EffectiveFrom = party.EffectiveFrom,
            EffectiveThrough = party.EffectiveThrough,
            IsCurrent = party.EffectiveFrom <= lifecycle.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate),
            CanGrantTenantPortalAccess = party.EffectiveThrough == null
                || party.EffectiveThrough >= lifecycle.BusinessDate,
            GuarantorLegalNoticeEligible = party.GuarantorLegalNoticeEligible,
        };

    internal IQueryable<LeaseManagementPartyResponse> BuildCurrentPartiesQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        BuildPartyQuery(access, leaseManagementId)
            .Where(party => party.IsCurrent)
            .OrderBy(party => party.Role)
            .ThenBy(party => party.TenantName)
            .ThenBy(party => party.LeaseManagementPartyId);

    internal IQueryable<ActiveTenantUserAccessResponse> BuildCurrentPartyAccessQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { party.PortfolioId, party.LeaseManagementId }
        join tenantAccess in _db.TenantUserAccesses.AsNoTracking()
            on new { party.PortfolioId, LeaseManagementPartyId = party.Id }
            equals new { tenantAccess.PortfolioId, tenantAccess.LeaseManagementPartyId }
        where management.Id == leaseManagementId
            && (party.EffectiveThrough == null || party.EffectiveThrough >= lifecycle.BusinessDate)
            && tenantAccess.RevokedAtUtc == null
        let requiresAccountActivation = tenantAccess.ApplicationUser!.PasswordHash == null
        let hasTenantPortalAuthority = _db.WorkspaceAccessContexts.AsNoTracking().Any(context =>
            context.Id == tenantAccess.AccessContextId &&
            context.UserId == tenantAccess.ApplicationUserId &&
            context.PortfolioId == tenantAccess.PortfolioId &&
            context.Status == WorkspaceAccessContextStatus.Active &&
            context.SuspendedAtUtc == null &&
            context.RevokedAtUtc == null &&
            context.Membership != null &&
            context.Membership.PortfolioId == tenantAccess.PortfolioId &&
            context.Membership.DefaultExperience == WorkspaceExperience.Tenant &&
            context.Membership.Status == WorkspaceMembershipStatus.Active &&
            context.Membership.SuspendedAtUtc == null &&
            context.Membership.RevokedAtUtc == null &&
            context.Membership.EffectiveFromUtc <= lifecycle.EffectiveNowUtc &&
            (context.Membership.EffectiveToUtc == null ||
                context.Membership.EffectiveToUtc > lifecycle.EffectiveNowUtc) &&
            context.Membership.RoleAssignments.Any(assignment =>
                assignment.PortfolioId == tenantAccess.PortfolioId &&
                assignment.RoleProfile!.Key == RoleProfileKeys.TenantPortal &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.EffectiveFromUtc <= lifecycle.EffectiveNowUtc &&
                (assignment.EffectiveToUtc == null ||
                    assignment.EffectiveToUtc > lifecycle.EffectiveNowUtc)))
        orderby party.Role,
            party.Tenant!.FirstName,
            party.Tenant!.LastName,
            party.Id,
            tenantAccess.GrantedAtUtc,
            tenantAccess.Id
        select new ActiveTenantUserAccessResponse
        {
            TenantUserAccessId = tenantAccess.Id,
            PublicId = tenantAccess.PublicId,
            LeaseManagementPartyId = tenantAccess.LeaseManagementPartyId,
            AccessContextId = tenantAccess.AccessContextId,
            ApplicationUserId = tenantAccess.ApplicationUserId,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant!.LastName).Trim(),
            UserDisplayName = tenantAccess.ApplicationUser!.DisplayName,
            UserEmail = tenantAccess.ApplicationUser.Email ?? string.Empty,
            GrantedAtUtc = tenantAccess.GrantedAtUtc,
            Reason = tenantAccess.Reason,
            RequiresAccountActivation = requiresAccountActivation,
            HasPendingActivationInvitation = requiresAccountActivation && hasTenantPortalAuthority &&
                _db.WorkspaceInvitations.AsNoTracking().Any(invitation =>
                    invitation.PortfolioId == tenantAccess.PortfolioId &&
                    invitation.WorkspaceMembership!.AccessContextId == tenantAccess.AccessContextId &&
                    invitation.InvitedUserId == tenantAccess.ApplicationUserId &&
                    invitation.AcceptedAtUtc == null &&
                    invitation.RevokedAtUtc == null &&
                    invitation.ExpiresAtUtc > lifecycle.EffectiveNowUtc),
            IsPortalLoginReady = !requiresAccountActivation && hasTenantPortalAuthority,
        };

    internal IQueryable<LeaseAgreementDraftDetailReadRow> BuildAgreementDraftDetailQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        join sourceVersion in _db.LegalDocumentSourceVersions.AsNoTracking()
            on new { agreement.PortfolioId, SourceVersionId = agreement.DocumentSourceVersionId }
            equals new { sourceVersion.PortfolioId, SourceVersionId = sourceVersion.Id }
        let sourceAgreementId = agreement.ReplacesAgreementId
            ?? agreement.RenewsAgreementId
            ?? agreement.ReissuesAgreementId
            ?? agreement.TransferredFromAgreementId
        from sourceAgreement in _db.LeaseAgreements.AsNoTracking()
            .Where(candidate => candidate.PortfolioId == agreement.PortfolioId
                && candidate.LeaseManagementId == agreement.LeaseManagementId
                && candidate.Id == sourceAgreementId)
            .DefaultIfEmpty()
        where management.Id == leaseManagementId
            && agreement.Id == leaseAgreementId
            && agreement.IssuedAtUtc == null
            && agreement.IssuedArtifactId == null
            && agreement.FullyExecutedAtUtc == null
            && agreement.ExecutedArtifactId == null
            && agreement.VoidedAtUtc == null
            && agreement.DraftCanceledAtUtc == null
        select new LeaseAgreementDraftDetailReadRow
        {
            LeaseManagementId = management.Id,
            LeaseAgreementId = agreement.Id,
            PublicId = agreement.PublicId,
            VersionNumber = agreement.VersionNumber,
            DraftRevision = agreement.DraftRevision,
            AgreementNumber = agreement.AgreementNumber,
            ChangeType = agreement.ChangeType,
            CorrectionReason = agreement.CorrectionReason,
            TermType = agreement.TermType,
            TermStartOn = agreement.TermStartOn,
            TermEndOn = agreement.TermEndOn,
            GoverningFromOn = agreement.GoverningFromOn,
            BaseRentAmount = agreement.BaseRentAmount,
            RentDueDay = agreement.RentDueDay,
            SecurityDepositObligation = agreement.SecurityDepositObligation,
            LateFeeAmount = agreement.LateFeeAmount,
            GracePeriodDays = agreement.GracePeriodDays,
            Currency = agreement.Currency,
            TermsSchemaVersion = agreement.TermsSchemaVersion,
            TermsPayloadJson = agreement.TermsPayload,
            DocumentSourceVersionId = agreement.DocumentSourceVersionId,
            DocumentTemplateId = sourceVersion.DocumentTemplateId,
            DocumentTemplateVersion = sourceVersion.DocumentTemplateVersion,
            SourceAgreement = sourceAgreement == null
                ? null
                : new LeaseAgreementSourceComparisonResponse
                {
                    LeaseAgreementId = sourceAgreement.Id,
                    AgreementNumber = sourceAgreement.AgreementNumber,
                    VersionNumber = sourceAgreement.VersionNumber,
                    ChangeType = sourceAgreement.ChangeType,
                    TermType = sourceAgreement.TermType,
                    TermStartOn = sourceAgreement.TermStartOn,
                    TermEndOn = sourceAgreement.TermEndOn,
                    GoverningFromOn = sourceAgreement.GoverningFromOn,
                    BaseRentAmount = sourceAgreement.BaseRentAmount,
                    RentDueDay = sourceAgreement.RentDueDay,
                    SecurityDepositObligation = sourceAgreement.SecurityDepositObligation,
                    LateFeeAmount = sourceAgreement.LateFeeAmount,
                    GracePeriodDays = sourceAgreement.GracePeriodDays,
                    Currency = sourceAgreement.Currency,
                },
            Signers = agreement.Signers
                .Where(signer => signer.PortfolioId == access.PortfolioId)
                .OrderBy(signer => signer.SigningOrder)
                .ThenBy(signer => signer.Id)
                .Select(signer => new LeaseAgreementDraftSignerResponse
                {
                    LeaseAgreementSignerId = signer.Id,
                    LeaseManagementPartyId = signer.LeaseManagementPartyId,
                    TenantId = signer.TenantId,
                    SignerRole = signer.SignerRole,
                    NameSnapshot = signer.NameSnapshot,
                    EmailSnapshot = signer.EmailSnapshot,
                    SigningOrder = signer.SigningOrder,
                    IsRequired = signer.IsRequired,
                })
                .ToList(),
            CreatedAtUtc = agreement.CreatedAtUtc,
            UpdatedAtUtc = agreement.UpdatedAtUtc,
        };

    internal IQueryable<LeaseAddendumEligibleBaseAgreementResponse> BuildAddendumEligibleBaseAgreementQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        where management.Id == leaseManagementId
            && agreement.FullyExecutedAtUtc != null
            && agreement.ExecutedArtifactId != null
            && agreement.VoidedAtUtc == null
            && agreement.DraftCanceledAtUtc == null
        select new LeaseAddendumEligibleBaseAgreementResponse
        {
            LeaseAgreementId = agreement.Id,
            PublicId = agreement.PublicId,
            AgreementNumber = agreement.AgreementNumber,
            VersionNumber = agreement.VersionNumber,
            TermStartOn = agreement.TermStartOn,
            TermEndOn = agreement.TermEndOn,
            GoverningFromOn = agreement.GoverningFromOn,
            Currency = agreement.Currency,
        };

    internal IQueryable<LeaseAddendumDraftDetailReadRow> BuildAddendumDraftDetailQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join addendum in _db.LeaseAddenda.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { addendum.PortfolioId, addendum.LeaseManagementId }
        join baseAgreement in _db.LeaseAgreements.AsNoTracking()
            on new
            {
                addendum.PortfolioId,
                addendum.LeaseManagementId,
                BaseAgreementId = addendum.BaseAgreementId,
            }
            equals new
            {
                baseAgreement.PortfolioId,
                baseAgreement.LeaseManagementId,
                BaseAgreementId = baseAgreement.Id,
            }
        join sourceVersion in _db.LegalDocumentSourceVersions.AsNoTracking()
            on new { addendum.PortfolioId, SourceVersionId = addendum.DocumentSourceVersionId }
            equals new { sourceVersion.PortfolioId, SourceVersionId = sourceVersion.Id }
        where management.Id == leaseManagementId
            && addendum.Id == leaseAddendumId
            && addendum.IssuedAtUtc == null
            && addendum.IssuedArtifactId == null
            && addendum.FullyExecutedAtUtc == null
            && addendum.ExecutedArtifactId == null
            && addendum.VoidedAtUtc == null
            && addendum.DraftCanceledAtUtc == null
        select new LeaseAddendumDraftDetailReadRow
        {
            LeaseManagementId = management.Id,
            LeaseAddendumId = addendum.Id,
            PublicId = addendum.PublicId,
            SeriesPublicId = addendum.SeriesPublicId,
            BaseAgreementId = baseAgreement.Id,
            BaseAgreementPublicId = baseAgreement.PublicId,
            BaseAgreementNumber = baseAgreement.AgreementNumber,
            BaseAgreementCurrency = baseAgreement.Currency,
            VersionNumber = addendum.VersionNumber,
            DraftRevision = addendum.DraftRevision,
            AddendumNumber = addendum.AddendumNumber,
            Purpose = addendum.Purpose,
            SourceAddendumId = addendum.ReplacesAddendumId,
            EffectiveFromOn = addendum.EffectiveFromOn,
            EffectiveThroughOn = addendum.EffectiveThroughOn,
            TermsSchemaVersion = addendum.TermsSchemaVersion,
            TermsPayloadJson = addendum.TermsPayload,
            DocumentSourceVersionId = addendum.DocumentSourceVersionId,
            DocumentTemplateId = sourceVersion.DocumentTemplateId,
            DocumentTemplateVersion = sourceVersion.DocumentTemplateVersion,
            Signers = addendum.Signers
                .Where(signer => signer.PortfolioId == access.PortfolioId)
                .OrderBy(signer => signer.SigningOrder)
                .ThenBy(signer => signer.Id)
                .Select(signer => new LeaseAddendumDraftSignerResponse
                {
                    LeaseAddendumSignerId = signer.Id,
                    LeaseManagementPartyId = signer.LeaseManagementPartyId,
                    TenantId = signer.TenantId,
                    SignerRole = signer.SignerRole,
                    NameSnapshot = signer.NameSnapshot,
                    EmailSnapshot = signer.EmailSnapshot,
                    SigningOrder = signer.SigningOrder,
                    IsRequired = signer.IsRequired,
                })
                .ToList(),
            FinancialEffects = addendum.FinancialEffects
                .Where(effect => effect.PortfolioId == access.PortfolioId)
                .OrderBy(effect => effect.Id)
                .Select(effect => new LeaseAddendumDraftFinancialEffectResponse
                {
                    LeaseAddendumFinancialEffectId = effect.Id,
                    EffectType = effect.EffectType,
                    Amount = effect.Amount,
                    Currency = effect.Currency,
                    ChargeCode = effect.ChargeCode,
                    EffectiveFromOn = effect.EffectiveFromOn,
                    EffectiveThroughOn = effect.EffectiveThroughOn,
                    DueOn = effect.DueOn,
                    Description = effect.Description,
                })
                .ToList(),
            CreatedAtUtc = addendum.CreatedAtUtc,
            UpdatedAtUtc = addendum.UpdatedAtUtc,
        };

    internal IQueryable<LeaseAgreementSignatureProgressResponse> BuildAgreementSignatureProgressQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        join request in _db.SignatureRequests.AsNoTracking()
            on new { agreement.PortfolioId, LeaseAgreementId = (int?)agreement.Id }
            equals new { request.PortfolioId, request.LeaseAgreementId }
        where management.Id == leaseManagementId
            && agreement.Id == leaseAgreementId
            && agreement.IssuedAtUtc != null
            && agreement.IssuedArtifactId != null
            && request.IssuedArtifactId == agreement.IssuedArtifactId
        select new LeaseAgreementSignatureProgressResponse
        {
            LeaseManagementId = management.Id,
            LeaseAgreementId = agreement.Id,
            SignatureRequestId = request.Id,
            SignatureRequestPublicId = request.PublicId,
            Provider = request.Provider,
            Subject = request.Subject,
            Status = request.Status,
            TotalSignerCount = request.Signers.Count(signer =>
                signer.PortfolioId == access.PortfolioId),
            RequiredSignerCount = request.Signers.Count(signer =>
                signer.PortfolioId == access.PortfolioId && signer.IsRequired),
            SignedSignerCount = request.Signers.Count(signer =>
                signer.PortfolioId == access.PortfolioId
                && signer.Status == SignatureSignerStatus.Signed),
            DeclinedSignerCount = request.Signers.Count(signer =>
                signer.PortfolioId == access.PortfolioId
                && signer.Status == SignatureSignerStatus.Declined),
            IssuedArtifactId = request.IssuedArtifactId,
            IssuedArtifactReady = request.IssuedArtifact != null
                && request.IssuedArtifact.StoredFile != null
                && request.IssuedArtifact.StoredFile.DeletedAt == null,
            ExecutedArtifactId = request.ExecutedArtifactId,
            ExecutedArtifactReady = request.ExecutedArtifact != null
                && request.ExecutedArtifact.StoredFile != null
                && request.ExecutedArtifact.StoredFile.DeletedAt == null,
            PreparedAtUtc = request.PreparedAtUtc,
            ProviderAcceptedAtUtc = request.ProviderAcceptedAtUtc,
            CompletedAtUtc = request.CompletedAtUtc,
            DeclinedAtUtc = request.DeclinedAtUtc,
            VoidedAtUtc = request.VoidedAtUtc,
            FailureCode = request.FailureCode,
            Signers = request.Signers
                .Where(signer => signer.PortfolioId == access.PortfolioId
                    && signer.AgreementSignerId != null
                    && signer.AgreementSigner != null
                    && signer.AgreementSigner.LeaseAgreementId == agreement.Id)
                .OrderBy(signer => signer.SigningOrder)
                .ThenBy(signer => signer.Id)
                .Select(signer => new LeaseAgreementSignatureProgressSignerResponse
                {
                    SignatureSignerId = signer.Id,
                    LeaseAgreementSignerId = signer.AgreementSignerId!.Value,
                    LeaseManagementPartyId = signer.AgreementSigner!.LeaseManagementPartyId,
                    TenantId = signer.AgreementSigner.TenantId,
                    SignerRole = signer.AgreementSigner.SignerRole,
                    NameSnapshot = signer.NameSnapshot,
                    EmailSnapshot = signer.EmailSnapshot,
                    SigningOrder = signer.SigningOrder,
                    IsRequired = signer.IsRequired,
                    Status = signer.Status,
                    DeliveryQueuedAtUtc = signer.CreatedAtUtc,
                    ViewedAtUtc = signer.ViewedAtUtc,
                    ConsentGivenAtUtc = signer.ConsentGivenAtUtc,
                    SignedAtUtc = signer.SignedAtUtc,
                    DeclinedAtUtc = signer.DeclinedAtUtc,
                })
                .ToList(),
        };

    internal IQueryable<LeaseAgreementEffectiveAddendumSeriesResponse> BuildEffectiveAddendumSeriesQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int sourceAgreementId)
    {
        var governingSourceStatus = _db.LeaseAgreementStatusProjections.AsNoTracking()
            .Where(status => status.PortfolioId == access.PortfolioId
                && status.LeaseManagementId == leaseManagementId
                && status.AgreementId == sourceAgreementId
                && status.IsGoverning);
        var businessDate = governingSourceStatus.Select(status => status.BusinessDate);

        var effectiveSeries = _db.LeaseAddenda.AsNoTracking()
            .Where(addendum => addendum.PortfolioId == access.PortfolioId
                && addendum.LeaseManagementId == leaseManagementId
                && addendum.FullyExecutedAtUtc != null
                && addendum.ExecutedArtifactId != null
                && addendum.VoidedAtUtc == null
                && addendum.DraftCanceledAtUtc == null
                && addendum.BaseAgreement != null
                && addendum.BaseAgreement.FullyExecutedAtUtc != null
                && addendum.BaseAgreement.ExecutedArtifactId != null
                && addendum.BaseAgreement.VoidedAtUtc == null
                && addendum.BaseAgreement.DraftCanceledAtUtc == null
                && addendum.EffectiveFromOn <= businessDate.First()
                && (addendum.EffectiveThroughOn == null
                    || addendum.EffectiveThroughOn >= businessDate.First())
                && (addendum.SupersededEffectiveOn == null
                    || addendum.SupersededEffectiveOn > businessDate.First())
                && !_db.LeaseAddenda.Any(newer =>
                    newer.PortfolioId == access.PortfolioId
                    && newer.LeaseManagementId == leaseManagementId
                    && newer.SeriesPublicId == addendum.SeriesPublicId
                    && newer.VersionNumber > addendum.VersionNumber
                    && newer.FullyExecutedAtUtc != null
                    && newer.ExecutedArtifactId != null
                    && newer.VoidedAtUtc == null
                    && newer.DraftCanceledAtUtc == null
                    && newer.BaseAgreement != null
                    && newer.BaseAgreement.FullyExecutedAtUtc != null
                    && newer.BaseAgreement.ExecutedArtifactId != null
                    && newer.BaseAgreement.VoidedAtUtc == null
                    && newer.BaseAgreement.DraftCanceledAtUtc == null
                    && newer.EffectiveFromOn <= businessDate.First()
                    && (newer.EffectiveThroughOn == null
                        || newer.EffectiveThroughOn >= businessDate.First())
                    && (newer.SupersededEffectiveOn == null
                        || newer.SupersededEffectiveOn > businessDate.First())));

        return
            from management in BuildLeaseWorkspaceReadManagementQuery(access)
            join sourceAgreement in _db.LeaseAgreements.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { sourceAgreement.PortfolioId, sourceAgreement.LeaseManagementId }
            where management.Id == leaseManagementId
                && sourceAgreement.Id == sourceAgreementId
                && sourceAgreement.FullyExecutedAtUtc != null
                && sourceAgreement.ExecutedArtifactId != null
                && sourceAgreement.VoidedAtUtc == null
                && sourceAgreement.DraftCanceledAtUtc == null
                && governingSourceStatus.Any()
            select new LeaseAgreementEffectiveAddendumSeriesResponse
            {
                LeaseManagementId = management.Id,
                SourceAgreementId = sourceAgreement.Id,
                SourceAgreementNumber = sourceAgreement.AgreementNumber,
                SourceTermStartOn = sourceAgreement.TermStartOn,
                SourceTermEndOn = sourceAgreement.TermEndOn,
                SourceGoverningFromOn = sourceAgreement.GoverningFromOn,
                BusinessDate = businessDate.First(),
                DecisionRequired = effectiveSeries.Any(),
                RequiredDecisionCount = effectiveSeries.Count(),
                Series = effectiveSeries
                    .OrderBy(addendum => addendum.Purpose)
                    .ThenBy(addendum => addendum.AddendumNumber)
                    .ThenBy(addendum => addendum.SeriesPublicId)
                    .Select(addendum => new LeaseAgreementEffectiveAddendumSeriesItemResponse
                    {
                        SeriesPublicId = addendum.SeriesPublicId,
                        CurrentLeaseAddendumId = addendum.Id,
                        CurrentLeaseAddendumPublicId = addendum.PublicId,
                        CurrentVersionNumber = addendum.VersionNumber,
                        BaseAgreementId = addendum.BaseAgreementId,
                        BaseAgreementNumber = addendum.BaseAgreement!.AgreementNumber,
                        BaseAgreementTermStartOn = addendum.BaseAgreement.TermStartOn,
                        BaseAgreementTermEndOn = addendum.BaseAgreement.TermEndOn,
                        Purpose = addendum.Purpose,
                        Title = addendum.AddendumNumber,
                        EffectiveFromOn = addendum.EffectiveFromOn,
                        EffectiveThroughOn = addendum.EffectiveThroughOn,
                        DecisionRequired = true,
                        FinancialEffectCount = addendum.FinancialEffects.Count(effect =>
                            effect.PortfolioId == access.PortfolioId),
                        FinancialEffects = addendum.FinancialEffects
                            .Where(effect => effect.PortfolioId == access.PortfolioId)
                            .OrderBy(effect => effect.EffectType)
                            .ThenBy(effect => effect.EffectiveFromOn)
                            .ThenBy(effect => effect.DueOn)
                            .ThenBy(effect => effect.Id)
                            .Select(effect => new LeaseAgreementRenewalFinancialEffectSummaryResponse
                            {
                                LeaseAddendumFinancialEffectId = effect.Id,
                                EffectType = effect.EffectType,
                                Amount = effect.Amount,
                                Currency = effect.Currency,
                                ChargeCode = effect.ChargeCode,
                                EffectiveFromOn = effect.EffectiveFromOn,
                                EffectiveThroughOn = effect.EffectiveThroughOn,
                                DueOn = effect.DueOn,
                                Description = effect.Description,
                            })
                            .ToList(),
                    })
                    .ToList(),
            };
    }

    internal IQueryable<LeaseLegalHistoryCountsReadRow> BuildLegalHistoryCountsQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        BuildLeaseWorkspaceReadManagementQuery(access)
            .Where(management => management.Id == leaseManagementId)
            .Select(management => new LeaseLegalHistoryCountsReadRow
            {
                AgreementCount = _db.LeaseAgreements.Count(agreement =>
                    agreement.PortfolioId == access.PortfolioId
                    && agreement.LeaseManagementId == management.Id),
                AddendumCount = _db.LeaseAddenda.Count(addendum =>
                    addendum.PortfolioId == access.PortfolioId
                    && addendum.LeaseManagementId == management.Id),
                LegalArtifactCount = _db.LegalDocumentArtifacts.Count(artifact =>
                    artifact.PortfolioId == access.PortfolioId
                    && (_db.LeaseAgreements.Any(agreement =>
                            agreement.PortfolioId == access.PortfolioId
                            && agreement.LeaseManagementId == management.Id
                            && (agreement.IssuedArtifactId == artifact.Id
                                || agreement.ExecutedArtifactId == artifact.Id))
                        || _db.LeaseAddenda.Any(addendum =>
                            addendum.PortfolioId == access.PortfolioId
                            && addendum.LeaseManagementId == management.Id
                            && (addendum.IssuedArtifactId == artifact.Id
                                || addendum.ExecutedArtifactId == artifact.Id)))),
            });

    internal IQueryable<LeaseAgreementHistoryResponse> BuildAgreementHistoryQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        LeaseLegalHistoryQuery query)
    {
        var rows =
            from management in BuildLeaseWorkspaceReadManagementQuery(access)
            join agreement in _db.LeaseAgreements.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { agreement.PortfolioId, agreement.LeaseManagementId }
            join status in _db.LeaseAgreementStatusProjections.AsNoTracking()
                .Select(projection => new
                {
                    projection.PortfolioId,
                    projection.AgreementId,
                    AgreementStatus = (string?)projection.AgreementStatus,
                    IsGoverning = (bool?)projection.IsGoverning,
                })
                on new { agreement.PortfolioId, AgreementId = agreement.Id }
                equals new { status.PortfolioId, status.AgreementId }
                into statusRows
            from status in statusRows.DefaultIfEmpty()
            where management.Id == leaseManagementId
            select new LeaseAgreementHistoryResponse
            {
                LeaseManagementId = management.Id,
                LeaseAgreementId = agreement.Id,
                PublicId = agreement.PublicId,
                VersionNumber = agreement.VersionNumber,
                AgreementNumber = agreement.AgreementNumber,
                ChangeType = agreement.ChangeType,
                CorrectionReason = agreement.CorrectionReason,
                ReplacesAgreementId = agreement.ReplacesAgreementId,
                RenewsAgreementId = agreement.RenewsAgreementId,
                ReissuesAgreementId = agreement.ReissuesAgreementId,
                ReissueReason = agreement.ReissueReason,
                HasLiveReissue = _db.LeaseAgreements.Any(candidate =>
                    candidate.PortfolioId == agreement.PortfolioId
                    && candidate.LeaseManagementId == agreement.LeaseManagementId
                    && candidate.ReissuesAgreementId == agreement.Id
                    && candidate.DraftCanceledAtUtc == null
                    && (candidate.VoidedAtUtc == null || candidate.FullyExecutedAtUtc != null)),
                TermType = agreement.TermType,
                TermStartOn = agreement.TermStartOn,
                TermEndOn = agreement.TermEndOn,
                GoverningFromOn = agreement.GoverningFromOn,
                SupersededEffectiveOn = agreement.SupersededEffectiveOn,
                BaseRentAmount = agreement.BaseRentAmount,
                AgreementStatus = status.AgreementStatus ?? "Unknown",
                IsGoverning = status.IsGoverning ?? false,
                SignerCount = _db.LeaseAgreementSigners.Count(signer =>
                    signer.PortfolioId == access.PortfolioId
                    && signer.LeaseAgreementId == agreement.Id),
                HasSourceScan = _db.StoredFiles.Any(file =>
                    file.PortfolioId == access.PortfolioId
                    && file.EntityType == nameof(LeaseAgreement)
                    && file.EntityId == agreement.Id
                    && file.DeletedAt == null),
                IssuedArtifact = agreement.IssuedArtifact == null ? null : new LegalArtifactSummaryResponse
                {
                    LegalDocumentArtifactId = agreement.IssuedArtifact.Id,
                    PublicId = agreement.IssuedArtifact.PublicId,
                    ArtifactKind = agreement.IssuedArtifact.ArtifactKind,
                    FileName = agreement.IssuedArtifact.FileName,
                    ContentType = agreement.IssuedArtifact.ContentType,
                    ByteLength = agreement.IssuedArtifact.ByteLength,
                    ContentSha256 = agreement.IssuedArtifact.ContentSha256,
                    CreatedAtUtc = agreement.IssuedArtifact.CreatedAtUtc,
                },
                ExecutedArtifact = agreement.ExecutedArtifact == null ? null : new LegalArtifactSummaryResponse
                {
                    LegalDocumentArtifactId = agreement.ExecutedArtifact.Id,
                    PublicId = agreement.ExecutedArtifact.PublicId,
                    ArtifactKind = agreement.ExecutedArtifact.ArtifactKind,
                    FileName = agreement.ExecutedArtifact.FileName,
                    ContentType = agreement.ExecutedArtifact.ContentType,
                    ByteLength = agreement.ExecutedArtifact.ByteLength,
                    ContentSha256 = agreement.ExecutedArtifact.ContentSha256,
                    CreatedAtUtc = agreement.ExecutedArtifact.CreatedAtUtc,
                },
                IssuedAtUtc = agreement.IssuedAtUtc,
                FullyExecutedAtUtc = agreement.FullyExecutedAtUtc,
                VoidedAtUtc = agreement.VoidedAtUtc,
                DraftCanceledAtUtc = agreement.DraftCanceledAtUtc,
                DraftCanceledByUserId = agreement.DraftCanceledByUserId,
                DraftCancellationReason = agreement.DraftCancellationReason,
                CreatedAtUtc = agreement.CreatedAtUtc,
                UpdatedAtUtc = agreement.UpdatedAtUtc,
            };

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            rows = rows.Where(row => row.AgreementStatus == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row => EF.Functions.ILike(row.AgreementNumber, $"%{term}%"));
        }
        return rows;
    }

    internal IQueryable<LeaseAddendumHistoryResponse> BuildAddendumHistoryQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        LeaseLegalHistoryQuery query)
    {
        var rows =
            from management in BuildLeaseWorkspaceReadManagementQuery(access)
            join addendum in _db.LeaseAddenda.AsNoTracking()
                on new { management.PortfolioId, LeaseManagementId = management.Id }
                equals new { addendum.PortfolioId, addendum.LeaseManagementId }
            join status in _db.LeaseAddendumStatusProjections.AsNoTracking()
                .Select(projection => new
                {
                    projection.PortfolioId,
                    projection.LeaseAddendumId,
                    AddendumStatus = (string?)projection.AddendumStatus,
                    FinancialEffectCount = (int?)projection.FinancialEffectCount,
                })
                on new { addendum.PortfolioId, LeaseAddendumId = addendum.Id }
                equals new { status.PortfolioId, status.LeaseAddendumId }
                into statusRows
            from status in statusRows.DefaultIfEmpty()
            where management.Id == leaseManagementId
            select new LeaseAddendumHistoryResponse
            {
                LeaseManagementId = management.Id,
                LeaseAddendumId = addendum.Id,
                PublicId = addendum.PublicId,
                SeriesPublicId = addendum.SeriesPublicId,
                BaseAgreementId = addendum.BaseAgreementId,
                VersionNumber = addendum.VersionNumber,
                AddendumNumber = addendum.AddendumNumber,
                Purpose = addendum.Purpose,
                ReplacesAddendumId = addendum.ReplacesAddendumId,
                EffectiveFromOn = addendum.EffectiveFromOn,
                EffectiveThroughOn = addendum.EffectiveThroughOn,
                SupersededEffectiveOn = addendum.SupersededEffectiveOn,
                AddendumStatus = status.AddendumStatus ?? "Unknown",
                FinancialEffectCount = status.FinancialEffectCount ?? 0,
                RecurringRentDelta = _db.LeaseAddendumFinancialEffects
                    .Where(effect => effect.PortfolioId == access.PortfolioId
                        && effect.LeaseAddendumId == addendum.Id
                        && effect.EffectType == LeaseAddendumFinancialEffectType.RecurringRentDelta)
                    .Sum(effect => (decimal?)effect.Amount) ?? 0m,
                SignerCount = _db.LeaseAddendumSigners.Count(signer =>
                    signer.PortfolioId == access.PortfolioId
                    && signer.LeaseAddendumId == addendum.Id),
                CanCorrect = addendum.FullyExecutedAtUtc != null
                    && addendum.ExecutedArtifactId != null
                    && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null
                    && addendum.SupersededByAddendumId == null
                    && !_db.LeaseAddenda.Any(candidate =>
                        candidate.PortfolioId == access.PortfolioId
                        && candidate.LeaseManagementId == leaseManagementId
                        && candidate.ReplacesAddendumId == addendum.Id
                        && candidate.DraftCanceledAtUtc == null
                        && (candidate.VoidedAtUtc == null
                            || candidate.FullyExecutedAtUtc != null)),
                IssuedArtifact = addendum.IssuedArtifact == null ? null : new LegalArtifactSummaryResponse
                {
                    LegalDocumentArtifactId = addendum.IssuedArtifact.Id,
                    PublicId = addendum.IssuedArtifact.PublicId,
                    ArtifactKind = addendum.IssuedArtifact.ArtifactKind,
                    FileName = addendum.IssuedArtifact.FileName,
                    ContentType = addendum.IssuedArtifact.ContentType,
                    ByteLength = addendum.IssuedArtifact.ByteLength,
                    ContentSha256 = addendum.IssuedArtifact.ContentSha256,
                    CreatedAtUtc = addendum.IssuedArtifact.CreatedAtUtc,
                },
                ExecutedArtifact = addendum.ExecutedArtifact == null ? null : new LegalArtifactSummaryResponse
                {
                    LegalDocumentArtifactId = addendum.ExecutedArtifact.Id,
                    PublicId = addendum.ExecutedArtifact.PublicId,
                    ArtifactKind = addendum.ExecutedArtifact.ArtifactKind,
                    FileName = addendum.ExecutedArtifact.FileName,
                    ContentType = addendum.ExecutedArtifact.ContentType,
                    ByteLength = addendum.ExecutedArtifact.ByteLength,
                    ContentSha256 = addendum.ExecutedArtifact.ContentSha256,
                    CreatedAtUtc = addendum.ExecutedArtifact.CreatedAtUtc,
                },
                IssuedAtUtc = addendum.IssuedAtUtc,
                FullyExecutedAtUtc = addendum.FullyExecutedAtUtc,
                VoidedAtUtc = addendum.VoidedAtUtc,
                CreatedAtUtc = addendum.CreatedAtUtc,
                UpdatedAtUtc = addendum.UpdatedAtUtc,
            };

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            rows = rows.Where(row => row.AddendumStatus == status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(row => EF.Functions.ILike(row.AddendumNumber, $"%{term}%"));
        }
        return rows;
    }

    internal IQueryable<LegalArtifactFileReference> BuildAgreementArtifactFileQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId,
        int artifactId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        join artifact in _db.LegalDocumentArtifacts.AsNoTracking()
            on agreement.PortfolioId equals artifact.PortfolioId
        join file in _db.StoredFiles.AsNoTracking()
            on new { artifact.PortfolioId, Id = artifact.StoredFileId }
            equals new { file.PortfolioId, file.Id }
        where management.Id == leaseManagementId
            && agreement.Id == leaseAgreementId
            && artifact.Id == artifactId
            && (agreement.IssuedArtifactId == artifact.Id || agreement.ExecutedArtifactId == artifact.Id)
            && file.DeletedAt == null
        select new LegalArtifactFileReference(artifact.Id, artifact.StorageKey, artifact.FileName, artifact.ContentType);

    internal IQueryable<LegalArtifactFileReference> BuildAddendumArtifactFileQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAddendumId,
        int artifactId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join addendum in _db.LeaseAddenda.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { addendum.PortfolioId, addendum.LeaseManagementId }
        join artifact in _db.LegalDocumentArtifacts.AsNoTracking()
            on addendum.PortfolioId equals artifact.PortfolioId
        join file in _db.StoredFiles.AsNoTracking()
            on new { artifact.PortfolioId, Id = artifact.StoredFileId }
            equals new { file.PortfolioId, file.Id }
        where management.Id == leaseManagementId
            && addendum.Id == leaseAddendumId
            && artifact.Id == artifactId
            && (addendum.IssuedArtifactId == artifact.Id || addendum.ExecutedArtifactId == artifact.Id)
            && file.DeletedAt == null
        select new LegalArtifactFileReference(artifact.Id, artifact.StorageKey, artifact.FileName, artifact.ContentType);

    internal IQueryable<AgreementSourceScanFileReadRow> BuildAgreementSourceScanFileQuery(
        LeaseManagementReadContext access,
        int leaseManagementId,
        int leaseAgreementId) =>
        from management in BuildLeaseWorkspaceReadManagementQuery(access)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        join file in _db.StoredFiles.AsNoTracking()
            on new
            {
                agreement.PortfolioId,
                EntityType = nameof(LeaseAgreement),
                EntityId = (long?)agreement.Id,
            }
            equals new { file.PortfolioId, file.EntityType, file.EntityId }
        where management.Id == leaseManagementId
            && agreement.Id == leaseAgreementId
            && file.DeletedAt == null
        select new AgreementSourceScanFileReadRow
        {
            StoredFileId = file.Id,
            StorageKey = file.FilePath,
            FileName = file.FileName,
            ContentType = file.ContentType,
            UploadedAtUtc = file.UploadedAt,
        };

    internal IQueryable<CanonicalLedgerHeaderReadRow> BuildCanonicalLedgerHeaderQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.MoneyBalancesRead)
        where management.Id == leaseManagementId
        join account in _db.TenantAccounts.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { account.PortfolioId, account.LeaseManagementId }
        join lifecycle in _db.LeaseManagementLifecycleProjections.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { lifecycle.PortfolioId, lifecycle.LeaseManagementId }
        join balance in _db.TenantAccountBalanceProjections.AsNoTracking()
            on new { account.PortfolioId, TenantAccountId = account.Id }
            equals new { balance.PortfolioId, balance.TenantAccountId }
        select new CanonicalLedgerHeaderReadRow
        {
            LeaseManagementId = management.Id,
            TenantAccountId = account.Id,
            AccountNumber = account.AccountNumber,
            TenantName = lifecycle.CurrentPrimaryTenantName,
            PropertyId = management.PropertyId,
            PropertyName = management.Property!.Name,
            TotalDebits = balance.TotalDebits,
            TotalCredits = balance.TotalCredits,
            ReceivableBalance = balance.ReceivableBalance,
            PastDueCount = balance.PastDueCount,
            TotalEntryCount = _db.TenantLedgerEntries.Count(entry =>
                entry.PortfolioId == access.PortfolioId
                && entry.TenantAccountId == account.Id
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance),
        };

    internal IQueryable<CanonicalLedgerEntryReadRow> BuildCanonicalLedgerEntriesQuery(
        int portfolioId,
        int tenantAccountId) =>
        _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType != TenantLedgerEntryType.OpeningBalance)
            .Select(ToLedgerReadRowExpression());

    private IQueryable<CanonicalLedgerEntryReadRow> BuildOpeningEntryQuery(
        int portfolioId,
        int tenantAccountId) =>
        _db.TenantLedgerEntries.AsNoTracking()
            .Where(entry => entry.PortfolioId == portfolioId
                && entry.TenantAccountId == tenantAccountId
                && entry.EntryType == TenantLedgerEntryType.OpeningBalance)
            .OrderBy(entry => entry.EffectiveOn)
            .ThenBy(entry => entry.Id)
            .Select(ToLedgerReadRowExpression());

    private static System.Linq.Expressions.Expression<Func<TenantLedgerEntry, CanonicalLedgerEntryReadRow>>
        ToLedgerReadRowExpression() => entry => new CanonicalLedgerEntryReadRow
        {
            Id = entry.Id,
            TenantAccountId = entry.TenantAccountId,
            EntryType = entry.EntryType,
            Direction = entry.Direction,
            Amount = entry.Amount,
            EffectiveOn = entry.EffectiveOn,
            DueOn = entry.DueOn,
            Description = entry.Description,
            PaymentMethodSummary = entry.ProviderPaymentAttempt == null
                ? null : entry.ProviderPaymentAttempt.PaymentMethodSummary,
            LeaseAgreementBaseRent = entry.LeaseAgreement == null
                ? null : entry.LeaseAgreement.BaseRentAmount,
        };

    internal IQueryable<LeaseManagement> BuildAuthorizedManagementQuery(
        LeaseManagementReadContext access,
        string capabilityKey) =>
        BuildAuthorizedManagementQuery(access, [capabilityKey]);

    internal IQueryable<LeaseManagement> BuildAuthorizedManagementQuery(
        LeaseManagementReadContext access,
        IReadOnlyCollection<string> capabilityKeys)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        return _db.LeaseManagements.AsNoTracking().WhereAuthorized(
            _db,
            new WorkspaceReadScope(
                access.PortfolioId,
                access.UserId,
                access.SessionId,
                access.AccessContextId,
                access.AccessRevision),
            capabilityKeys,
            utcNow);
    }

    private static IQueryable<LeaseManagementSummaryResponse> ApplySort(
        IQueryable<LeaseManagementSummaryResponse> rows,
        LeaseManagementListQuery query) => query.SortField switch
        {
            "relationshipnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.RelationshipNumber).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.RelationshipNumber).ThenBy(row => row.LeaseManagementId),
            "propertyname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PropertyName).ThenByDescending(row => row.UnitNumber)
                : rows.OrderBy(row => row.PropertyName).ThenBy(row => row.UnitNumber),
            "unitnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.UnitNumber).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.UnitNumber).ThenBy(row => row.LeaseManagementId),
            "tenantname" => query.SortDescending
                ? rows.OrderByDescending(row => row.PrimaryTenantName).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.PrimaryTenantName).ThenBy(row => row.LeaseManagementId),
            "lifecycle" => query.SortDescending
                ? rows.OrderByDescending(row => row.Lifecycle).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.Lifecycle).ThenBy(row => row.LeaseManagementId),
            "rent" => query.SortDescending
                ? rows.OrderByDescending(row => row.BaseRentAmount).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.BaseRentAmount).ThenBy(row => row.LeaseManagementId),
            _ => query.SortDescending
                ? rows.OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.LeaseManagementId)
                : rows.OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.LeaseManagementId),
        };

    private static IQueryable<LeaseAgreementHistoryResponse> ApplyAgreementSort(
        IQueryable<LeaseAgreementHistoryResponse> rows,
        LeaseLegalHistoryQuery query) => query.SortField switch
        {
            "agreementnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.AgreementNumber).ThenByDescending(row => row.LeaseAgreementId)
                : rows.OrderBy(row => row.AgreementNumber).ThenBy(row => row.LeaseAgreementId),
            "status" => query.SortDescending
                ? rows.OrderByDescending(row => row.AgreementStatus).ThenByDescending(row => row.VersionNumber)
                : rows.OrderBy(row => row.AgreementStatus).ThenBy(row => row.VersionNumber),
            "termstarton" => query.SortDescending
                ? rows.OrderByDescending(row => row.TermStartOn).ThenByDescending(row => row.VersionNumber)
                : rows.OrderBy(row => row.TermStartOn).ThenBy(row => row.VersionNumber),
            "termendon" => query.SortDescending
                ? rows.OrderByDescending(row => row.TermEndOn).ThenByDescending(row => row.VersionNumber)
                : rows.OrderBy(row => row.TermEndOn).ThenBy(row => row.VersionNumber),
            _ => query.SortDescending
                ? rows.OrderByDescending(row => row.VersionNumber).ThenByDescending(row => row.LeaseAgreementId)
                : rows.OrderBy(row => row.VersionNumber).ThenBy(row => row.LeaseAgreementId),
        };

    private static IQueryable<LeaseAddendumHistoryResponse> ApplyAddendumSort(
        IQueryable<LeaseAddendumHistoryResponse> rows,
        LeaseLegalHistoryQuery query) => query.SortField switch
        {
            "addendumnumber" => query.SortDescending
                ? rows.OrderByDescending(row => row.AddendumNumber).ThenByDescending(row => row.LeaseAddendumId)
                : rows.OrderBy(row => row.AddendumNumber).ThenBy(row => row.LeaseAddendumId),
            "status" => query.SortDescending
                ? rows.OrderByDescending(row => row.AddendumStatus).ThenByDescending(row => row.VersionNumber)
                : rows.OrderBy(row => row.AddendumStatus).ThenBy(row => row.VersionNumber),
            "effectivefromon" => query.SortDescending
                ? rows.OrderByDescending(row => row.EffectiveFromOn).ThenByDescending(row => row.VersionNumber)
                : rows.OrderBy(row => row.EffectiveFromOn).ThenBy(row => row.VersionNumber),
            _ => query.SortDescending
                ? rows.OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.LeaseAddendumId)
                : rows.OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.LeaseAddendumId),
        };

    internal static LedgerTransactionResponse ToLedgerResponse(
        CanonicalLedgerEntryReadRow entry,
        CanonicalLedgerHeaderReadRow header,
        string? type = null) => new()
        {
            Date = entry.EffectiveOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Type = type ?? (entry.EntryType == TenantLedgerEntryType.PaymentReceipt ? "Payment"
            : entry.Direction == TenantLedgerDirection.Debit ? "Charge" : "Credit"),
            Id = entry.Id,
            Description = entry.Description,
            Amount = entry.Direction == TenantLedgerDirection.Debit ? -entry.Amount : entry.Amount,
            TenantAccountId = entry.TenantAccountId,
            PropertyId = header.PropertyId,
            PropertyName = header.PropertyName,
            Counterparty = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
            Category = entry.EntryType.ToString(),
            Status = "Posted",
            SourceHref = $"/tenant-accounts/{entry.TenantAccountId}/entries/{entry.Id}",
            IsProrated = entry.EntryType == TenantLedgerEntryType.RentCharge
            && entry.LeaseAgreementBaseRent.HasValue
            && entry.Amount != entry.LeaseAgreementBaseRent.Value,
            Explanation = LedgerExplanation.ForTenantLedgerEntry(
            entry.EntryType, entry.Direction, entry.Amount, entry.EffectiveOn, entry.DueOn,
            entry.PaymentMethodSummary, entry.Description),
        };

    internal sealed class CanonicalLedgerHeaderReadRow
    {
        public int LeaseManagementId { get; init; }
        public int TenantAccountId { get; init; }
        public string AccountNumber { get; init; } = string.Empty;
        public string? TenantName { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public decimal TotalDebits { get; init; }
        public decimal TotalCredits { get; init; }
        public decimal ReceivableBalance { get; init; }
        public int PastDueCount { get; init; }
        public int TotalEntryCount { get; init; }
    }

    internal sealed class LeaseAgreementDraftDetailReadRow
    {
        public int LeaseManagementId { get; init; }
        public int LeaseAgreementId { get; init; }
        public Guid PublicId { get; init; }
        public int VersionNumber { get; init; }
        public int DraftRevision { get; init; }
        public string AgreementNumber { get; init; } = string.Empty;
        public LeaseAgreementChangeType ChangeType { get; init; }
        public string? CorrectionReason { get; init; }
        public LeaseAgreementTermType TermType { get; init; }
        public DateOnly TermStartOn { get; init; }
        public DateOnly? TermEndOn { get; init; }
        public DateOnly GoverningFromOn { get; init; }
        public decimal BaseRentAmount { get; init; }
        public short RentDueDay { get; init; }
        public decimal SecurityDepositObligation { get; init; }
        public decimal LateFeeAmount { get; init; }
        public short GracePeriodDays { get; init; }
        public string Currency { get; init; } = string.Empty;
        public int TermsSchemaVersion { get; init; }
        public string TermsPayloadJson { get; init; } = string.Empty;
        public int DocumentSourceVersionId { get; init; }
        public int? DocumentTemplateId { get; init; }
        public int? DocumentTemplateVersion { get; init; }
        public LeaseAgreementSourceComparisonResponse? SourceAgreement { get; init; }
        public IReadOnlyList<LeaseAgreementDraftSignerResponse> Signers { get; init; } = [];
        public DateTime CreatedAtUtc { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    internal sealed class LeaseAddendumDraftDetailReadRow
    {
        public int LeaseManagementId { get; init; }
        public int LeaseAddendumId { get; init; }
        public Guid PublicId { get; init; }
        public Guid SeriesPublicId { get; init; }
        public int BaseAgreementId { get; init; }
        public Guid BaseAgreementPublicId { get; init; }
        public string BaseAgreementNumber { get; init; } = string.Empty;
        public string BaseAgreementCurrency { get; init; } = string.Empty;
        public int VersionNumber { get; init; }
        public int DraftRevision { get; init; }
        public string AddendumNumber { get; init; } = string.Empty;
        public LeaseAddendumPurpose Purpose { get; init; }
        public int? SourceAddendumId { get; init; }
        public DateOnly EffectiveFromOn { get; init; }
        public DateOnly? EffectiveThroughOn { get; init; }
        public int TermsSchemaVersion { get; init; }
        public string TermsPayloadJson { get; init; } = string.Empty;
        public int DocumentSourceVersionId { get; init; }
        public int? DocumentTemplateId { get; init; }
        public int? DocumentTemplateVersion { get; init; }
        public IReadOnlyList<LeaseAddendumDraftSignerResponse> Signers { get; init; } = [];
        public IReadOnlyList<LeaseAddendumDraftFinancialEffectResponse> FinancialEffects { get; init; } = [];
        public DateTime CreatedAtUtc { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    internal sealed class LeaseLegalHistoryCountsReadRow
    {
        public int AgreementCount { get; init; }
        public int AddendumCount { get; init; }
        public int LegalArtifactCount { get; init; }
    }

    internal sealed class AgreementSourceScanFileReadRow
    {
        public int StoredFileId { get; init; }
        public string StorageKey { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public DateTime UploadedAtUtc { get; init; }
    }

    internal sealed class CanonicalLedgerEntryReadRow
    {
        public long Id { get; init; }
        public int TenantAccountId { get; init; }
        public TenantLedgerEntryType EntryType { get; init; }
        public TenantLedgerDirection Direction { get; init; }
        public decimal Amount { get; init; }
        public DateOnly EffectiveOn { get; init; }
        public DateOnly? DueOn { get; init; }
        public string Description { get; init; } = string.Empty;
        public string? PaymentMethodSummary { get; init; }
        public decimal? LeaseAgreementBaseRent { get; init; }
    }
}
