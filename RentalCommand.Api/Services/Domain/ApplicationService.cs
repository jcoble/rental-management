using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IApplicationService"/>
public sealed class ApplicationService : IApplicationService
{
    private const string EntityType = "RentalApplication";

    private readonly RentalCommandDbContext _db;
    private readonly IDataUpdateService _dataUpdate;
    private readonly IAuditTrailService _audit;

    public ApplicationService(
        RentalCommandDbContext db,
        IDataUpdateService dataUpdate,
        IAuditTrailService audit)
    {
        _db = db;
        _dataUpdate = dataUpdate;
        _audit = audit;
    }

    // -------------------------------------------------------------------------
    // Public (token-resolved)
    // -------------------------------------------------------------------------

    public async Task<PublicApplicationFormInfo?> GetPublicFormInfoAsync(string token, CancellationToken ct = default)
    {
        var portfolio = await ResolvePortfolioByTokenAsync(token, ct);
        if (portfolio is null)
            return null;

        // Offer active properties (and their non-offline units) so the applicant can pick what they're
        // applying for. Soft-deleted rows are excluded by the entities' global query filters.
        var properties = await _db.Properties
            .AsNoTracking()
            .Where(p => p.PortfolioId == portfolio.Id && p.Status != PropertyStatus.Inactive)
            .OrderBy(p => p.Name)
            .Select(p => new PublicPropertyOption
            {
                Id = p.Id,
                Name = p.Name,
                AddressLine1 = p.AddressLine1,
                City = p.City,
                State = p.State,
                Units = p.Units
                    .Where(u => u.Status != UnitStatus.Offline)
                    .OrderBy(u => u.UnitNumber)
                    .Select(u => new PublicUnitOption { Id = u.Id, UnitNumber = u.UnitNumber })
                    .ToList(),
            })
            .ToListAsync(ct);

        return new PublicApplicationFormInfo
        {
            ManagementCompanyName = portfolio.ManagementCompanyName,
            Properties = properties,
        };
    }

    public async Task<SubmitApplicationResult?> SubmitAsync(
        string token, SubmitApplicationRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var portfolio = await ResolvePortfolioByTokenAsync(token, ct);
        if (portfolio is null)
            return null;

        var portfolioId = portfolio.Id;

        // IDOR guard: a client-supplied PropertyId/UnitId is honored only when it actually lives in
        // the token's portfolio; otherwise it is dropped (the applicant simply applied without a
        // specific property) rather than letting them reference another portfolio's record.
        int? propertyId = null;
        if (request.PropertyId is > 0 &&
            await _db.Properties.AnyAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct))
        {
            propertyId = request.PropertyId;
        }

        int? unitId = null;
        if (request.UnitId is > 0 &&
            await _db.Units.AnyAsync(u => u.Id == request.UnitId
                && u.Property != null && u.Property.PortfolioId == portfolioId
                && (propertyId == null || u.PropertyId == propertyId), ct))
        {
            unitId = request.UnitId;
        }

        var existingOpenApplicationId = await FindOpenApplicationIdByEmailAsync(portfolioId, request.Email, ct);
        if (existingOpenApplicationId is not null)
        {
            throw new DomainValidationException(
                $"An application for {NormalizeEmailForComparison(request.Email)} already exists as application #{existingOpenApplicationId}. Review the existing application before creating another.",
                statusCode: 409);
        }

        var now = DateTime.UtcNow;
        var entity = new RentalApplication
        {
            PortfolioId = portfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = request.Email,
            Phone = request.Phone,
            DateOfBirth = request.DateOfBirth.ToUtc(),
            CurrentAddressLine1 = request.CurrentAddressLine1,
            CurrentAddressLine2 = request.CurrentAddressLine2,
            CurrentCity = request.CurrentCity,
            CurrentState = request.CurrentState,
            CurrentPostalCode = request.CurrentPostalCode,
            // Keep the legacy single-line CurrentAddress in sync (composed from the structured
            // fields; falls back to any single-line value the caller still sends).
            CurrentAddress = AddressComposer.Compose(
                                 request.CurrentAddressLine1, request.CurrentAddressLine2,
                                 request.CurrentCity, request.CurrentState, request.CurrentPostalCode)
                             ?? request.CurrentAddress,
            Employer = request.Employer,
            MonthlyIncome = request.MonthlyIncome,
            DesiredMoveInDate = request.DesiredMoveInDate.ToUtc(),
            Notes = request.Notes,
            IdExtractedFields = request.IdExtractedFields,
            ConsentGiven = request.ConsentGiven,
            // Consent is required at the controller; record exactly when/where it was captured for FCRA.
            ConsentAtUtc = request.ConsentGiven ? now : null,
            ConsentIpAddress = request.ConsentGiven ? ipAddress : null,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RentalApplications.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Surface the new application to the landlord's live views.
        var response = ApplicationResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);

        return new SubmitApplicationResult
        {
            ApplicationId = entity.Id,
            Status = entity.Status.ToString(),
        };
    }

    // -------------------------------------------------------------------------
    // Authed (portfolio-scoped)
    // -------------------------------------------------------------------------

    public async Task<ApplicationResponse> CreateFromScanAsync(
        int portfolioId, CreateApplicationRequest request, int userId, CancellationToken ct = default)
    {
        // IDOR guard: honor a PropertyId/UnitId only when it actually lives in THIS portfolio; otherwise
        // drop it (the landlord simply filed the applicant without a specific property) rather than letting
        // a hallucinated/foreign id from the scan reference another portfolio's record. Same shape as the
        // public SubmitAsync guard.
        int? propertyId = null;
        if (request.PropertyId is > 0 &&
            await _db.Properties.AnyAsync(p => p.Id == request.PropertyId && p.PortfolioId == portfolioId, ct))
        {
            propertyId = request.PropertyId;
        }

        int? unitId = null;
        if (request.UnitId is > 0 &&
            await _db.Units.AnyAsync(u => u.Id == request.UnitId
                && u.Property != null && u.Property.PortfolioId == portfolioId
                && (propertyId == null || u.PropertyId == propertyId), ct))
        {
            unitId = request.UnitId;
        }

        var existingOpenApplicationId = await FindOpenApplicationIdByEmailAsync(portfolioId, request.Email, ct);
        if (existingOpenApplicationId is not null)
        {
            throw new DomainValidationException(
                $"An application for {NormalizeEmailForComparison(request.Email)} already exists as application #{existingOpenApplicationId}. Review the existing application before creating another.",
                statusCode: 409);
        }

        var now = DateTime.UtcNow;
        var entity = new RentalApplication
        {
            PortfolioId = portfolioId,
            PropertyId = propertyId,
            UnitId = unitId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = request.Phone,
            DateOfBirth = request.DateOfBirth.ToUtc(),
            // The scanned application carries a single-line current address; keep it on the legacy
            // CurrentAddress column (the structured line1/city/... fields stay null, same as a public
            // submit that only sends a single-line address).
            CurrentAddress = string.IsNullOrWhiteSpace(request.CurrentAddress) ? null : request.CurrentAddress.Trim(),
            Employer = request.Employer,
            MonthlyIncome = request.MonthlyIncome,
            DesiredMoveInDate = request.DesiredMoveInDate.ToUtc(),
            Notes = request.Notes,
            IdExtractedFields = request.IdExtractedFields,
            // A landlord-keyed paper application carries no in-app FCRA consent event.
            ConsentGiven = false,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.RentalApplications.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Audit the PII-touching create (an applicant record was created from a scanned application).
        await _audit.LogAsync(
            portfolioId,
            EntityType,
            entity.Id,
            AuditLogOperation.Created,
            userId: userId,
            changeReason: "Created from scanned rental application",
            ct: ct);

        var response = ApplicationResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<IReadOnlyList<ApplicationResponse>> ListAsync(
        int portfolioId, string? status, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(portfolioId, status, query, ct);
        return page.Items;
    }

    public async Task<ApplicationListResponse> ListPageAsync(
        int portfolioId, string? status, ListQuery query, CancellationToken ct = default)
    {
        var q = _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId);

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
            .Select(a => new ApplicationHomeProjection
            {
                Application = a,
                PropertyName = a.Property != null ? a.Property.Name : null,
                UnitNumber = a.Unit != null ? a.Unit.UnitNumber : null,
            })
            .ToListAsync(ct);

        return new ApplicationListResponse
        {
            Items = items
                .Select(i => ApplicationResponse.FromEntity(i.Application, i.PropertyName, i.UnitNumber))
                .ToList(),
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<ApplicationResponse?> GetAsync(int portfolioId, int id, CancellationToken ct = default)
    {
        var item = await _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.Id == id && a.PortfolioId == portfolioId)
            .Select(a => new ApplicationHomeProjection
            {
                Application = a,
                PropertyName = a.Property != null ? a.Property.Name : null,
                UnitNumber = a.Unit != null ? a.Unit.UnitNumber : null,
            })
            .FirstOrDefaultAsync(ct);

        return item == null
            ? null
            : ApplicationResponse.FromEntity(item.Application, item.PropertyName, item.UnitNumber);
    }

    public async Task<ApproveApplicationResult?> ApproveAsync(
        int portfolioId, int id, int userId, CancellationToken ct = default)
    {
        var entity = await _db.RentalApplications
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        if (entity.Status is ApplicationStatus.Approved)
            throw new InvalidOperationException("Application is already approved.");
        if (entity.Status is ApplicationStatus.Declined or ApplicationStatus.Withdrawn)
            throw new InvalidOperationException($"Application is {entity.Status.ToString().ToLowerInvariant()} and cannot be approved.");

        var now = DateTime.UtcNow;

        // Mirror Tenant creation: the approved applicant becomes a real tenant record.
        var tenant = new Tenant
        {
            PortfolioId = portfolioId,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            Email = entity.Email,
            Phone = entity.Phone,
            DateOfBirth = entity.DateOfBirth,
            Notes = BuildTenantNote(entity),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        entity.Status = ApplicationStatus.Approved;
        entity.ReviewedAtUtc = now;
        entity.ApprovedTenantId = tenant.Id;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        // Audit the PII-touching mutation: an application was approved and a tenant was created.
        await _audit.LogAsync(
            portfolioId,
            "Tenant",
            tenant.Id,
            AuditLogOperation.Created,
            userId: userId,
            changeReason: $"Created from approved rental application #{entity.Id}",
            ct: ct);

        var tenantResponse = TenantResponse.FromEntity(tenant);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, "Tenant", tenant.Id, tenantResponse, ct);

        var appResponse = ApplicationResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, appResponse, ct);

        return new ApproveApplicationResult
        {
            ApplicationId = entity.Id,
            Status = entity.Status.ToString(),
            TenantId = tenant.Id,
        };
    }

    public async Task<ApplicationResponse?> DeclineAsync(
        int portfolioId, int id, int userId, string? reason, CancellationToken ct = default)
    {
        var entity = await _db.RentalApplications
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        if (entity.Status is ApplicationStatus.Approved)
            throw new InvalidOperationException("An approved application cannot be declined.");

        var now = DateTime.UtcNow;
        entity.Status = ApplicationStatus.Declined;
        entity.DecisionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        entity.ReviewedAtUtc = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            portfolioId,
            EntityType,
            entity.Id,
            AuditLogOperation.Updated,
            userId: userId,
            changeReason: "Application declined" + (entity.DecisionReason is null ? "" : $": {entity.DecisionReason}"),
            ct: ct);

        var response = ApplicationResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<ApplicationResponse?> WithdrawAsync(
        int portfolioId, int id, int userId, CancellationToken ct = default)
    {
        var entity = await _db.RentalApplications
            .FirstOrDefaultAsync(a => a.Id == id && a.PortfolioId == portfolioId, ct);
        if (entity == null)
            return null;

        if (entity.Status is ApplicationStatus.Approved)
            throw new InvalidOperationException("An approved application cannot be withdrawn.");

        var now = DateTime.UtcNow;
        entity.Status = ApplicationStatus.Withdrawn;
        entity.ReviewedAtUtc = now;
        entity.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        var response = ApplicationResponse.FromEntity(entity);
        await _dataUpdate.BroadcastEntityUpdateAsync(portfolioId, EntityType, entity.Id, response, ct);
        return response;
    }

    public async Task<ApplicationLinkResult> GenerateLinkAsync(int portfolioId, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct)
            ?? throw new InvalidOperationException("Portfolio not found.");

        // URL-safe opaque token. Rotating invalidates the previous link, which is the intended
        // "regenerate to revoke the old link" behavior.
        var token = GenerateToken();
        portfolio.PublicApplicationToken = token;
        portfolio.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new ApplicationLinkResult
        {
            Token = token,
            ApplyPath = $"/apply/{token}",
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

    private static string GenerateToken()
    {
        // 32 random bytes → ~43-char URL-safe string, well within the 64-char column.
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string? NormalizeEmailForComparison(string? email)
    {
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    private async Task<int?> FindOpenApplicationIdByEmailAsync(
        int portfolioId,
        string? email,
        CancellationToken ct)
    {
        var normalizedEmail = NormalizeEmailForComparison(email);
        if (normalizedEmail is null)
        {
            return null;
        }

        return await _db.RentalApplications
            .AsNoTracking()
            .Where(a => a.PortfolioId == portfolioId
                && a.Email != null
                && (a.Status == ApplicationStatus.Submitted
                    || a.Status == ApplicationStatus.UnderReview
                    || a.Status == ApplicationStatus.Approved))
            .Where(a => a.Email!.Trim().ToLower() == normalizedEmail)
            .OrderBy(a => a.Id)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);
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
}
