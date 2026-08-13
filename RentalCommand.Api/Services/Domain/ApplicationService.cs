using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IApplicationService"/>
public sealed class ApplicationService : IApplicationService
{
    private const string EntityType = "RentalApplication";
    private const string ScanEntityType = "Application";

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _files;
    private readonly IDataUpdateService _dataUpdate;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public ApplicationService(
        RentalCommandDbContext db,
        IFileStorage files,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _files = files;
        _dataUpdate = dataUpdate;
        _ = audit;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    // -------------------------------------------------------------------------
    // Public (token-resolved)
    // -------------------------------------------------------------------------

    public async Task<PublicApplicationFormInfo?> GetPublicFormInfoAsync(string token, CancellationToken ct = default)
    {
        var portfolio = await ResolvePortfolioByTokenAsync(token, ct);
        if (portfolio is null)
            return null;

        // PostgreSQL performs the property/unit join, operational filtering, ordering, and grouping
        // in one statement. In particular, the LEFT JOIN keeps active properties that currently
        // have no eligible units. Unit has no mutable availability field; the canonical occupancy
        // view is the only source for its presentation status.
        var rows = await _db.Database.SqlQuery<PublicPropertyOptionDatabaseRow>($"""
            WITH eligible_units AS (
                SELECT
                    unit."Id",
                    unit."PortfolioId",
                    unit."PropertyId",
                    unit."UnitNumber",
                    CASE
                        WHEN occupancy."IsOccupied" THEN {(int)DerivedUnitStatus.Occupied}
                        WHEN occupancy."HasScheduledMoveIn" THEN {(int)DerivedUnitStatus.Reserved}
                        ELSE {(int)DerivedUnitStatus.Vacant}
                    END AS "Status"
                FROM "Units" AS unit
                INNER JOIN "vw_unit_occupancy" AS occupancy
                    ON occupancy."PortfolioId" = unit."PortfolioId"
                    AND occupancy."UnitId" = unit."Id"
                WHERE unit."PortfolioId" = {portfolio.Id}
                    AND unit."DeletedAt" IS NULL
                    AND NOT occupancy."IsInTurnover"
                    AND NOT occupancy."IsOutOfService"
                    AND NOT occupancy."IsOnManagementHold"
            )
            SELECT
                property."Id",
                property."Name",
                property."AddressLine1",
                property."City",
                property."State",
                COALESCE(
                    jsonb_agg(
                        jsonb_build_object(
                            'Id', eligible."Id",
                            'UnitNumber', eligible."UnitNumber",
                            'Status', eligible."Status")
                        ORDER BY eligible."UnitNumber", eligible."Id")
                        FILTER (WHERE eligible."Id" IS NOT NULL),
                    '[]'::jsonb)::text AS "UnitsJson"
            FROM "Properties" AS property
            LEFT JOIN eligible_units AS eligible
                ON eligible."PortfolioId" = property."PortfolioId"
                AND eligible."PropertyId" = property."Id"
            WHERE property."PortfolioId" = {portfolio.Id}
                AND property."DeletedAt" IS NULL
                AND property."Status" <> {(int)PropertyStatus.Inactive}
            GROUP BY
                property."Id",
                property."Name",
                property."AddressLine1",
                property."City",
                property."State"
            ORDER BY property."Name", property."Id"
            """)
            .ToListAsync(ct);

        var properties = rows.Select(row => new PublicPropertyOption
        {
            Id = row.Id,
            Name = row.Name,
            AddressLine1 = row.AddressLine1,
            City = row.City,
            State = row.State,
            Units = JsonSerializer.Deserialize<PublicUnitOption[]>(row.UnitsJson) ?? [],
        }).ToList();

        return new PublicApplicationFormInfo
        {
            ManagementCompanyName = portfolio.ManagementCompanyName,
            Properties = properties,
        };
    }

    private sealed class PublicPropertyOptionDatabaseRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AddressLine1 { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string UnitsJson { get; set; } = "[]";
    }

    public async Task<SubmitApplicationResult?> SubmitAsync(
        string token,
        SubmitApplicationRequest request,
        string? ipAddress,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = new AtomicPublicApplicationSubmissionCommand(
            token, JsonSerializer.Serialize(request), ipAddress, operationKey);
        var outcome = await _atomic.ExecuteAsync(
            AtomicPublicApplicationSubmission.Identity(command), command,
            AtomicPublicApplicationSubmission.Codec, ct);
        return outcome.Value.Found
            ? new SubmitApplicationResult
            {
                ApplicationId = outcome.Value.ApplicationId,
                Status = outcome.Value.Status,
                Message = outcome.Value.Message,
            }
            : null;
    }

    // -------------------------------------------------------------------------
    // Authed (portfolio-scoped)
    // -------------------------------------------------------------------------

    public async Task<ApplicationResponse> CreateFromScanAsync(
        WorkspaceReadScope scope,
        CreateApplicationRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Create, 0, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.ResponseJson is { Length: > 0 } json
            ? JsonSerializer.Deserialize<ApplicationResponse>(json)
              ?? throw new InvalidOperationException("The application receipt snapshot is invalid.")
            : throw new InvalidOperationException("The application receipt has no response snapshot.");
    }

    public async Task<IReadOnlyList<ApplicationResponse>> ListAsync(
        int portfolioId, string? status, ListQuery query, int? unitId = null, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, status, query, unitId, ct);
        return page.Items;
    }

    public async Task<IReadOnlyList<ApplicationResponse>> ListAuthorizedAsync(
        WorkspaceReadScope scope,
        string? status,
        ListQuery query,
        int? unitId = null,
        CancellationToken ct = default)
    {
        var page = await ListPageAuthorizedAsync(scope, status, query, unitId, ct);
        return page.Items;
    }

    public Task<ApplicationListResponse> ListPageAuthorizedAsync(
        WorkspaceReadScope scope,
        string? status,
        ListQuery query,
        int? unitId = null,
        CancellationToken ct = default)
    {
        var applications = _db.RentalApplications
            .AsNoTracking()
            .WhereAuthorized(
                _db,
                scope,
                [CapabilityKeys.LeasingApplicationsManage],
                _timeProvider.UtcNow());
        return ListPageFromQueryAsync(applications, scope.PortfolioId, status, query, unitId, ct);
    }

    public Task<ApplicationListResponse> ListPageAsync(
        int portfolioId, string? status, ListQuery query, int? unitId = null, CancellationToken ct = default)
    {
        var q = _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

        return ListPageFromQueryAsync(q, portfolioId, status, query, unitId, ct);
    }

    private async Task<ApplicationListResponse> ListPageFromQueryAsync(
        IQueryable<RentalApplication> q,
        int portfolioId,
        string? status,
        ListQuery query,
        int? unitId,
        CancellationToken ct)
    {

        // Unit Command Center filter: scope to one unit's applications. Applied DB-side (translates to
        // a WHERE clause), never by materializing the portfolio's apps and filtering in memory.
        if (unitId is > 0)
        {
            q = q.Where(a => a.UnitId == unitId);
        }

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<ApplicationStatus>(status, ignoreCase: true, out var parsed))
        {
            q = q.Where(a => a.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                EF.Functions.ILike(a.FirstName, $"%{term}%") ||
                EF.Functions.ILike(a.LastName, $"%{term}%") ||
                EF.Functions.ILike(a.FirstName + " " + a.LastName, $"%{term}%") ||
                (a.Email != null && EF.Functions.ILike(a.Email, $"%{term}%")) ||
                (a.Phone != null && EF.Functions.ILike(a.Phone, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending
                ? q.OrderByDescending(a => a.LastName).ThenByDescending(a => a.FirstName)
                : q.OrderBy(a => a.LastName).ThenBy(a => a.FirstName),
            "lastname" => query.SortDescending ? q.OrderByDescending(a => a.LastName) : q.OrderBy(a => a.LastName),
            "email" => query.SortDescending ? q.OrderByDescending(a => a.Email) : q.OrderBy(a => a.Email),
            "monthlyincome" => query.SortDescending ? q.OrderByDescending(a => a.MonthlyIncome) : q.OrderBy(a => a.MonthlyIncome),
            "status" => query.SortDescending ? q.OrderByDescending(a => a.Status) : q.OrderBy(a => a.Status),
            "submittedat" => query.SortDescending ? q.OrderByDescending(a => a.SubmittedAtUtc) : q.OrderBy(a => a.SubmittedAtUtc),
            "submittedatutc" => query.SortDescending ? q.OrderByDescending(a => a.SubmittedAtUtc) : q.OrderBy(a => a.SubmittedAtUtc),
            // Newest applications first by default — the landlord works the freshest at the top.
            _ => query.SortDescending ? q.OrderBy(a => a.SubmittedAtUtc) : q.OrderByDescending(a => a.SubmittedAtUtc),
        };

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(a => new ApplicationResponse
            {
                Id = a.Id,
                PortfolioId = a.PortfolioId,
                PropertyId = a.PropertyId,
                UnitId = a.UnitId,
                PropertyName = a.Property != null ? a.Property.Name : null,
                UnitNumber = a.Unit != null ? a.Unit.UnitNumber : null,
                FirstName = a.FirstName,
                LastName = a.LastName,
                Email = a.Email,
                Phone = a.Phone,
                DateOfBirth = a.DateOfBirth,
                CurrentAddressLine1 = a.CurrentAddressLine1,
                CurrentAddressLine2 = a.CurrentAddressLine2,
                CurrentCity = a.CurrentCity,
                CurrentState = a.CurrentState,
                CurrentPostalCode = a.CurrentPostalCode,
                CurrentAddress = a.CurrentAddress,
                Employer = a.Employer,
                MonthlyIncome = a.MonthlyIncome,
                DesiredMoveInDate = a.DesiredMoveInDate,
                Notes = a.Notes,
                IdExtractedFields = a.IdExtractedFields,
                ConsentGiven = a.ConsentGiven,
                ConsentAtUtc = a.ConsentAtUtc,
                Status = a.Status == ApplicationStatus.Submitted ? "Submitted"
                    : a.Status == ApplicationStatus.UnderReview ? "UnderReview"
                    : a.Status == ApplicationStatus.Approved ? "Approved"
                    : a.Status == ApplicationStatus.Declined ? "Declined"
                    : "Withdrawn",
                DecisionReason = a.DecisionReason,
                SubmittedAtUtc = a.SubmittedAtUtc,
                ReviewedAtUtc = a.ReviewedAtUtc,
                ApprovedTenantId = a.ApprovedTenantId,
            })
            .ToListAsync(ct);

        return new ApplicationListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<ApplicationResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var applications = _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.Id == id && a.PortfolioId == portfolioId);
        return await GetFromQueryAsync(applications, portfolioId, id, ct);
    }

    public Task<ApplicationResponse?> GetAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        CancellationToken ct = default)
    {
        var applications = _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.Id == id)
            .WhereAuthorized(
                _db,
                scope,
                [CapabilityKeys.LeasingApplicationsManage],
                _timeProvider.UtcNow());
        return GetFromQueryAsync(applications, scope.PortfolioId, id, ct);
    }

    private async Task<ApplicationResponse?> GetFromQueryAsync(
        IQueryable<RentalApplication> applications,
        int portfolioId,
        int id,
        CancellationToken ct)
    {
        var item = await applications
            .Select(a => new ApplicationHomeProjection
            {
                Application = a,
                PropertyName = a.Property != null ? a.Property.Name : null,
                UnitNumber = a.Unit != null ? a.Unit.UnitNumber : null,
            })
            .FirstOrDefaultAsync(ct);

        if (item == null)
        {
            return null;
        }

        var response = ApplicationResponse.FromEntity(item.Application, item.PropertyName, item.UnitNumber);
        var scan = await _db.FindLatestAvailableEntityFileAsync(_files, portfolioId, ScanEntityType, id, ct);
        if (scan is not null)
        {
            response.HasScan = true;
            response.ScanIsImage = scan.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    public async Task<ApplicationResponse?> UpdateAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        UpdateApplicationRequest request,
        int userId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Update, id, operationKey, request);
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found ? await GetAsync(scope.PortfolioId, id, ct) : null;
    }
    public async Task<ApproveApplicationResult?> ApproveAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Approve, id, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found
            ? new ApproveApplicationResult
            {
                ApplicationId = outcome.Value.EntityId,
                Status = ApplicationStatus.Approved.ToString(),
                TenantId = outcome.Value.RelatedEntityId
                    ?? throw new InvalidOperationException("Approved application receipt has no tenant."),
            }
            : null;
    }
    public async Task<ApplicationResponse?> DeclineAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        string? reason,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Decline, id, operationKey, reason);
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found ? await GetAsync(scope.PortfolioId, id, ct) : null;
    }
    public async Task<ApplicationResponse?> WithdrawAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Withdraw, id, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found ? await GetAsync(scope.PortfolioId, id, ct) : null;
    }

    public async Task<bool> DeleteAuthorizedAsync(
        WorkspaceReadScope scope,
        int id,
        int userId,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicRentalMutation.Command(scope, AtomicRentalMutationDomain.Application,
            AtomicRentalMutationOperation.Delete, id, operationKey, new object());
        var outcome = await Atomic.ExecuteAsync(
            AtomicRentalMutation.Identity(command), command, AtomicRentalMutation.Codec, ct);
        return outcome.Value.Found;
    }
    public async Task<ApplicationLinkResult> GenerateLinkAsync(
        WorkspaceReadScope scope,
        string operationKey,
        CancellationToken ct = default)
    {
        var command = AtomicWorkspaceCoreMutation.Command(
            scope, AtomicWorkspaceCoreMutationOperation.RotateApplicationLink,
            operationKey, new { });
        var outcome = await Atomic.ExecuteAsync(
            AtomicWorkspaceCoreMutation.Identity(command), command,
            AtomicWorkspaceCoreMutation.Codec, ct);
        if (!outcome.Value.Found || outcome.Value.PublicApplicationToken is null)
            throw new InvalidOperationException("Portfolio not found.");

        return new ApplicationLinkResult
        {
            Token = outcome.Value.PublicApplicationToken,
            ApplyPath = $"/apply/{outcome.Value.PublicApplicationToken}",
        };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves a portfolio from a public link token. Tokens are matched exactly; an empty/whitespace
    /// token never matches. Soft-deleted portfolios are excluded by the global query filter.
    /// </summary>
    private async Task<Portfolio?> ResolvePortfolioByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PublicApplicationToken == token, ct);
    }

    private static string RequireNonBlank(string value, string fieldName)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException($"{fieldName} is required.");
        }
        return trimmed;
    }

    private static string? TrimToNull(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed class ApplicationHomeProjection
    {
        public required RentalApplication Application { get; init; }
        public string? PropertyName { get; init; }
        public string? UnitNumber { get; init; }
    }

    /// <summary>Folds the application's non-tenant fields (income, employer, address) into the tenant note.</summary>
    private static string? BuildTenantNote(RentalApplication a)
    {
        var parts = new List<string> { $"Created from rental application #{a.Id}." };
        if (!string.IsNullOrWhiteSpace(a.Employer))
            parts.Add($"Employer: {a.Employer}.");
        if (a.MonthlyIncome is > 0)
            parts.Add($"Stated monthly income: {a.MonthlyIncome:0.##}.");
        var priorAddress = AddressComposer.Compose(
                a.CurrentAddress,
                a.CurrentAddressLine2,
                a.CurrentCity,
                a.CurrentState,
                a.CurrentPostalCode)
            ?? a.CurrentAddress;
        if (!string.IsNullOrWhiteSpace(priorAddress))
            parts.Add($"Prior address: {priorAddress}.");
        if (!string.IsNullOrWhiteSpace(a.Notes))
            parts.Add($"Applicant notes: {a.Notes}");

        var note = string.Join(" ", parts);
        return note.Length > 2000 ? note[..2000] : note;
    }

    private IAtomicUnitOfWork Atomic => _atomic;
}
