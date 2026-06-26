using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Admin-only CRUD for managing team members (users) within the caller's portfolio.
/// All operations are scoped to the <c>portfolioId</c> claim in the caller's JWT —
/// a <see cref="UserAccount"/> from another portfolio is treated as not found.
/// </summary>
[ApiController]
[Route("api/v1/admin/users")]
[Produces("application/json")]
[Authorize(Roles = "Admin")]
public class AdminUsersController : ManagementControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RentalCommandDbContext _db;
    private readonly IAuditTrailService _audit;
    private readonly ILogger<AdminUsersController> _logger;

    public AdminUsersController(
        UserManager<ApplicationUser> userManager,
        RentalCommandDbContext db,
        IAuditTrailService audit,
        ILogger<AdminUsersController> logger)
    {
        _userManager = userManager;
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────
    // GET /api/v1/admin/users
    // ──────────────────────────────────────────────────────────────

    /// <summary>List all team members in the caller's portfolio.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TeamMemberDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TeamMemberDto>>> List([FromQuery] ListQuery query, CancellationToken ct)
    {
        var page = await BuildListPageAsync(GetPortfolioId(), query, ct);
        return Ok(page.Items);
    }

    /// <summary>Page team members in the caller's portfolio with SQL-side count/sort/skip/take.</summary>
    [HttpGet("page")]
    [ProducesResponseType(typeof(TeamMemberListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TeamMemberListResponse>> ListPage([FromQuery] ListQuery query, CancellationToken ct)
        => await BuildListPageAsync(GetPortfolioId(), query, ct);

    private async Task<TeamMemberListResponse> BuildListPageAsync(int portfolioId, ListQuery query, CancellationToken ct)
    {
        var filtered = _db.UserAccounts
            .AsNoTracking()
            .Where(u => u.PortfolioId == portfolioId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            filtered = filtered.Where(u =>
                EF.Functions.ILike(u.Email, $"%{term}%") ||
                EF.Functions.ILike(u.DisplayName, $"%{term}%"));
        }

        var totalCount = await filtered.CountAsync(ct);

        var members = await ApplySort(filtered, query)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(u => new TeamMemberDto
            {
                Id = u.Id,
                Email = u.Email,
                DisplayName = u.DisplayName,
                Role = u.Role.ToString(),
                IsActive = u.IsActive,
                OwnerId = u.OwnerId,
                TenantId = u.TenantId,
                CreatedAt = u.CreatedAt,
            })
            .ToListAsync(ct);

        return new TeamMemberListResponse
        {
            Items = members,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    private static IQueryable<UserAccount> ApplySort(IQueryable<UserAccount> q, ListQuery query) =>
        query.SortField switch
        {
            "email" => query.SortDescending ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email),
            "displayname" or "name" => query.SortDescending ? q.OrderByDescending(u => u.DisplayName) : q.OrderBy(u => u.DisplayName),
            "role" => query.SortDescending ? q.OrderByDescending(u => u.Role) : q.OrderBy(u => u.Role),
            "status" or "isactive" => query.SortDescending ? q.OrderByDescending(u => u.IsActive) : q.OrderBy(u => u.IsActive),
            "createdat" => query.SortDescending ? q.OrderByDescending(u => u.CreatedAt) : q.OrderBy(u => u.CreatedAt),
            _ => query.SortDescending ? q.OrderByDescending(u => u.CreatedAt) : q.OrderBy(u => u.CreatedAt),
        };

    // ──────────────────────────────────────────────────────────────
    // POST /api/v1/admin/users
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Create a new team member. Mirrors the IdentitySeeder pattern exactly:
    /// ApplicationUser.CreateAsync → AddToRoleAsync → UserAccount row.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateTeamMemberResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateTeamMemberResponse>> Create(
        [FromBody] CreateTeamMemberRequest request,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        // Reject duplicate email across all Identity users (not just this portfolio).
        if (await _userManager.FindByEmailAsync(request.Email) != null)
        {
            return BadRequest(new { error = "A user with that email address already exists." });
        }

        if (request.Role == UserRole.Tenant && request.TenantId == null)
        {
            return BadRequest(new { error = "Tenant users must be linked to a tenant." });
        }

        if (request.TenantId.HasValue)
        {
            var tenantExists = await _db.Tenants
                .AnyAsync(t => t.Id == request.TenantId.Value && t.PortfolioId == portfolioId, ct);
            if (!tenantExists)
            {
                return BadRequest(new { error = "Tenant not found in this portfolio." });
            }
        }

        var now = DateTime.UtcNow;
        var passwordToUse = !string.IsNullOrWhiteSpace(request.TemporaryPassword)
            ? request.TemporaryPassword
            : GenerateTemporaryPassword();

        var wasGenerated = string.IsNullOrWhiteSpace(request.TemporaryPassword);

        // Step 1: create the Identity user (mirrors IdentitySeeder.EnsureAdminUserAsync).
        var identityUser = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            EmailConfirmed = true, // admin-created accounts skip the email-verify gate
            DisplayName = request.DisplayName ?? string.Empty,
            PortfolioId = portfolioId,
            TenantId = request.TenantId,
            CreatedAt = now,
        };

        var createResult = await _userManager.CreateAsync(identityUser, passwordToUse);
        if (!createResult.Succeeded)
        {
            return BadRequest(new
            {
                error = "Failed to create user account.",
                details = createResult.Errors.Select(e => e.Description)
            });
        }

        // Step 2: assign Identity role (mirrors IdentitySeeder.EnsureAdminUserAsync).
        var roleName = request.Role.ToString();
        var roleResult = await _userManager.AddToRoleAsync(identityUser, roleName);
        if (!roleResult.Succeeded)
        {
            _logger.LogError(
                "Created Identity user {UserId} but failed to assign role {Role}: {Errors}",
                identityUser.Id, roleName,
                string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            // Best-effort cleanup: remove the orphaned Identity user so the admin can retry.
            await _userManager.DeleteAsync(identityUser);
            return BadRequest(new { error = "Failed to assign role. The user was not created." });
        }

        // Step 3: create the linked UserAccount domain row.
        var account = new UserAccount
        {
            PortfolioId = portfolioId,
            Email = request.Email,
            DisplayName = request.DisplayName ?? string.Empty,
            PasswordHash = string.Empty, // Identity owns the credential; this field is legacy
            Role = request.Role,
            TenantId = request.TenantId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            portfolioId,
            nameof(UserAccount),
            account.Id,
            AuditLogOperation.Created,
            userId: GetUserId(),
            newValues: SerializeAudit(new
            {
                email = account.Email,
                displayName = account.DisplayName,
                role = account.Role.ToString(),
                isActive = account.IsActive,
                ownerId = account.OwnerId,
                tenantId = account.TenantId,
                identityUserId = identityUser.Id,
            }),
            changeReason: $"Team member created with role {roleName}.",
            ct: ct);

        _logger.LogInformation(
            "Admin {AdminId} created team member {Email} (UserAccount {AccountId}, Identity {IdentityId}) with role {Role} in portfolio {PortfolioId}.",
            GetUserId(), request.Email, account.Id, identityUser.Id, roleName, portfolioId);

        var dto = ToDto(account);
        var response = new CreateTeamMemberResponse
        {
            Member = dto,
            GeneratedPassword = wasGenerated ? passwordToUse : null,
        };

        return CreatedAtAction(nameof(List), null, response);
    }

    // ──────────────────────────────────────────────────────────────
    // PATCH /api/v1/admin/users/{id}/role
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Change a team member's role. Updates both the UserAccount row and the Identity role
    /// assignment (removes the old role, adds the new one). Guards against demoting the last
    /// remaining Admin in the portfolio.
    /// </summary>
    [HttpPatch("{id:int}/role")]
    [ProducesResponseType(typeof(TeamMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamMemberDto>> ChangeRole(
        int id,
        [FromBody] ChangeRoleRequest request,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == id && u.PortfolioId == portfolioId, ct);

        if (account == null)
        {
            return NotFound(new { error = "Team member not found." });
        }

        // Guard: cannot demote the last active Admin in the portfolio.
        if (account.Role == UserRole.Admin && request.Role != UserRole.Admin)
        {
            var activeAdminCount = await _db.UserAccounts
                .CountAsync(u => u.PortfolioId == portfolioId
                              && u.Role == UserRole.Admin
                              && u.IsActive, ct);

            if (activeAdminCount <= 1)
            {
                return BadRequest(new { error = "Cannot change the role of the last active Admin in this portfolio." });
            }
        }

        // Update the Identity role assignment.
        var identityUser = await _userManager.FindByEmailAsync(account.Email);
        if (identityUser != null)
        {
            var currentRoles = await _userManager.GetRolesAsync(identityUser);
            if (currentRoles.Any())
            {
                await _userManager.RemoveFromRolesAsync(identityUser, currentRoles);
            }
            await _userManager.AddToRoleAsync(identityUser, request.Role.ToString());
        }

        // Update the domain UserAccount.
        var oldRole = account.Role;
        account.Role = request.Role;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            portfolioId,
            nameof(UserAccount),
            account.Id,
            AuditLogOperation.Updated,
            userId: GetUserId(),
            oldValues: SerializeAudit(new
            {
                email = account.Email,
                role = oldRole.ToString(),
            }),
            newValues: SerializeAudit(new
            {
                email = account.Email,
                role = account.Role.ToString(),
            }),
            changeReason: $"Team member role changed from {oldRole} to {account.Role}.",
            ct: ct);

        _logger.LogInformation(
            "Admin {AdminId} changed role of UserAccount {AccountId} to {Role} in portfolio {PortfolioId}.",
            GetUserId(), account.Id, request.Role, portfolioId);

        return Ok(ToDto(account));
    }

    // ──────────────────────────────────────────────────────────────
    // PATCH /api/v1/admin/users/{id}/active
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Activate or deactivate a team member. Guards against deactivating the last active
    /// Admin in the portfolio. Only flips the <see cref="UserAccount.IsActive"/> flag —
    /// does not touch Identity user state or the auth pipeline.
    /// </summary>
    [HttpPatch("{id:int}/active")]
    [ProducesResponseType(typeof(TeamMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamMemberDto>> SetActive(
        int id,
        [FromBody] SetActiveRequest request,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();

        var account = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == id && u.PortfolioId == portfolioId, ct);

        if (account == null)
        {
            return NotFound(new { error = "Team member not found." });
        }

        // Guard: cannot deactivate the last active Admin in the portfolio.
        if (!request.IsActive && account.Role == UserRole.Admin && account.IsActive)
        {
            var activeAdminCount = await _db.UserAccounts
                .CountAsync(u => u.PortfolioId == portfolioId
                              && u.Role == UserRole.Admin
                              && u.IsActive, ct);

            if (activeAdminCount <= 1)
            {
                return BadRequest(new { error = "Cannot deactivate the last active Admin in this portfolio." });
            }
        }

        var oldIsActive = account.IsActive;
        account.IsActive = request.IsActive;
        account.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(
            portfolioId,
            nameof(UserAccount),
            account.Id,
            AuditLogOperation.Updated,
            userId: GetUserId(),
            oldValues: SerializeAudit(new
            {
                email = account.Email,
                isActive = oldIsActive,
            }),
            newValues: SerializeAudit(new
            {
                email = account.Email,
                isActive = account.IsActive,
            }),
            changeReason: BuildActiveAuditReason(oldIsActive, account.IsActive),
            ct: ct);

        _logger.LogInformation(
            "Admin {AdminId} set UserAccount {AccountId} IsActive={IsActive} in portfolio {PortfolioId}.",
            GetUserId(), account.Id, request.IsActive, portfolioId);

        return Ok(ToDto(account));
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static TeamMemberDto ToDto(UserAccount u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        DisplayName = u.DisplayName,
        Role = u.Role.ToString(),
        IsActive = u.IsActive,
        OwnerId = u.OwnerId,
        TenantId = u.TenantId,
        CreatedAt = u.CreatedAt,
    };

    private static string BuildActiveAuditReason(bool oldIsActive, bool newIsActive)
    {
        if (oldIsActive == newIsActive)
        {
            return $"Team member active status confirmed as {(newIsActive ? "active" : "inactive")}.";
        }

        return newIsActive
            ? "Team member reactivated."
            : "Team member deactivated.";
    }

    private static string SerializeAudit(object values) => JsonSerializer.Serialize(values);

    /// <summary>
    /// Generates a cryptographically random password that satisfies ASP.NET Identity's
    /// default requirements: at least 8 characters with upper, lower, digit, and symbol.
    /// </summary>
    private static string GenerateTemporaryPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^&*";
        const string all = upper + lower + digits + symbols;

        // Guarantee at least one character from each required class.
        var sb = new StringBuilder();
        sb.Append(upper[RandomNumberGenerator.GetInt32(upper.Length)]);
        sb.Append(lower[RandomNumberGenerator.GetInt32(lower.Length)]);
        sb.Append(digits[RandomNumberGenerator.GetInt32(digits.Length)]);
        sb.Append(symbols[RandomNumberGenerator.GetInt32(symbols.Length)]);

        // Fill to 16 characters total.
        for (var i = 4; i < 16; i++)
        {
            sb.Append(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        // Shuffle to avoid predictable class-ordering.
        var chars = sb.ToString().ToCharArray();
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
