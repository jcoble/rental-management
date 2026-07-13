using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

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
        BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
            .AnyAsync(management => management.Id == leaseManagementId, ct);

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
            LeaseAgreementId = lifecycle.CurrentAgreementId,
            AgreementNumber = agreement == null ? null : agreement.AgreementNumber,
            AgreementStatus = agreementStatus == null ? null : agreementStatus.AgreementStatus,
            TermStartOn = agreement == null ? null : agreement.TermStartOn,
            TermEndOn = agreement == null ? null : agreement.TermEndOn,
            BaseRentAmount = agreement == null ? null : agreement.BaseRentAmount,
            UpcomingLeaseAgreementId = lifecycle.UpcomingAgreementId,
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
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
        join party in _db.LeaseManagementParties.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { party.PortfolioId, party.LeaseManagementId }
        where management.Id == leaseManagementId
        select new LeaseManagementPartyResponse
        {
            LeaseManagementPartyId = party.Id,
            LeaseManagementId = party.LeaseManagementId,
            TenantId = party.TenantId,
            TenantName = (party.Tenant!.FirstName + " " + party.Tenant.LastName).Trim(),
            Email = party.Tenant.Email,
            Phone = party.Tenant.Phone,
            Role = party.Role,
            EffectiveFrom = party.EffectiveFrom,
            EffectiveThrough = party.EffectiveThrough,
            GuarantorLegalNoticeEligible = party.GuarantorLegalNoticeEligible,
        };

    internal IQueryable<LeaseLegalHistoryCountsReadRow> BuildLegalHistoryCountsQuery(
        LeaseManagementReadContext access,
        int leaseManagementId) =>
        BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
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
            from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
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
                ReplacesAgreementId = agreement.ReplacesAgreementId,
                RenewsAgreementId = agreement.RenewsAgreementId,
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
            from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
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
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
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
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
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
        from management in BuildAuthorizedManagementQuery(access, CapabilityKeys.RentalsRead)
        join agreement in _db.LeaseAgreements.AsNoTracking()
            on new { management.PortfolioId, LeaseManagementId = management.Id }
            equals new { agreement.PortfolioId, agreement.LeaseManagementId }
        join file in _db.StoredFiles.AsNoTracking()
            on new
            {
                agreement.PortfolioId,
                EntityType = nameof(LeaseAgreement),
                EntityId = (int?)agreement.Id,
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
        string capabilityKey)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        return _db.LeaseManagements.AsNoTracking().Where(management =>
            management.PortfolioId == access.PortfolioId
            && _db.AuthSessions.AsNoTracking().Any(session =>
                session.Id == access.SessionId
                && session.UserId == access.UserId
                && session.ActiveAccessContextId == access.AccessContextId
                && session.Status == AuthSessionStatus.Active
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > utcNow
                && session.ActiveAccessContext != null
                && session.ActiveAccessContext.UserId == access.UserId
                && session.ActiveAccessContext.PortfolioId == access.PortfolioId
                && session.ActiveAccessContext.AccessRevision == access.AccessRevision
                && session.ActiveAccessContext.Status == WorkspaceAccessContextStatus.Active
                && session.ActiveAccessContext.SuspendedAtUtc == null
                && session.ActiveAccessContext.RevokedAtUtc == null
                && session.ActiveAccessContext.Membership != null
                && session.ActiveAccessContext.Membership.PortfolioId == access.PortfolioId
                && session.ActiveAccessContext.Membership.Status == WorkspaceMembershipStatus.Active
                && session.ActiveAccessContext.Membership.SuspendedAtUtc == null
                && session.ActiveAccessContext.Membership.RevokedAtUtc == null
                && session.ActiveAccessContext.Membership.EffectiveFromUtc <= utcNow
                && (session.ActiveAccessContext.Membership.EffectiveToUtc == null
                    || session.ActiveAccessContext.Membership.EffectiveToUtc > utcNow)
                && session.ActiveAccessContext.Membership.RoleAssignments.Any(assignment =>
                    assignment.PortfolioId == access.PortfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= utcNow
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow)
                    && assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                        profileCapability.CapabilityDefinition!.Key == capabilityKey
                        && profileCapability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope =>
                                scope.PortfolioId == access.PortfolioId
                                && scope.PropertyId == management.PropertyId))))));
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

    private static LedgerTransactionResponse ToLedgerResponse(
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
        PropertyId = header.PropertyId,
        PropertyName = header.PropertyName,
        Counterparty = string.IsNullOrWhiteSpace(header.TenantName) ? "Tenant" : header.TenantName,
        Category = entry.EntryType.ToString(),
        Status = "Posted",
        SourceHref = $"/tenant-accounts/{entry.TenantAccountId}/entries?entryId={entry.Id}",
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
