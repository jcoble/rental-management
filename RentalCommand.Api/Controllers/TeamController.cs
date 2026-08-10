using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Auth;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Canonical Team surface. It exposes workspace memberships and independently scoped assignments;
/// the removed single-role UserAccount/Identity-role API is intentionally not translated here.
/// </summary>
[ApiController]
[Route("api/v1/team")]
[Produces("application/json")]
public sealed class TeamController : AuthenticatedPortfolioControllerBase
{
    private static readonly AtomicJsonResultCodec<CreateWorkspaceMembershipResult> CreateCodec =
        new("workspace-team.membership.create.v1");
    private static readonly AtomicJsonResultCodec<WorkspaceTeamMutationResult> MutationCodec =
        new("workspace-team.mutation.v1");
    private readonly RentalCommandDbContext _db;
    private readonly IAtomicUnitOfWork _atomic;
    private readonly string _webBaseUrl;

    public TeamController(
        RentalCommandDbContext db,
        IAtomicUnitOfWork atomic,
        IConfiguration configuration)
    {
        _db = db;
        _atomic = atomic;
        _webBaseUrl = configuration["App:WebBaseUrl"] ?? "https://localhost:5667";
    }

    [HttpGet("members")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamRead)]
    public async Task<ActionResult<TeamMemberPageDto>> ListMembers(
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();
        var assignments = _db.MembershipRoleAssignments.AsNoTracking();
        var filtered =
            from context in _db.WorkspaceAccessContexts.AsNoTracking()
            join membership in _db.WorkspaceMemberships.AsNoTracking()
                on new { AccessContextId = context.Id, context.PortfolioId }
                equals new { membership.AccessContextId, membership.PortfolioId }
            join user in _db.Users.AsNoTracking() on context.UserId equals user.Id
            where context.PortfolioId == portfolioId
            select new { context, membership, user };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            filtered = filtered.Where(row =>
                EF.Functions.ILike(row.user.Email!, $"%{term}%") ||
                EF.Functions.ILike(row.user.DisplayName, $"%{term}%"));
        }

        var total = await filtered.CountAsync(ct);
        var sorted = query.SortField switch
        {
            "email" => query.SortDescending
                ? filtered.OrderByDescending(row => row.user.Email)
                : filtered.OrderBy(row => row.user.Email),
            "name" or "displayname" => query.SortDescending
                ? filtered.OrderByDescending(row => row.user.DisplayName)
                : filtered.OrderBy(row => row.user.DisplayName),
            "status" => query.SortDescending
                ? filtered.OrderByDescending(row => row.context.Status)
                : filtered.OrderBy(row => row.context.Status),
            _ => query.SortDescending
                ? filtered.OrderByDescending(row => row.context.CreatedAtUtc)
                : filtered.OrderBy(row => row.context.CreatedAtUtc),
        };
        var items = await sorted
            .ThenBy(row => row.context.Id)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(row => new TeamMemberSummaryDto(
                row.user.Id,
                row.context.Id,
                row.membership.Id,
                row.user.Email ?? string.Empty,
                row.user.DisplayName,
                row.context.Status,
                row.membership.Status,
                row.context.AccessRevision,
                assignments.Count(assignment =>
                    assignment.WorkspaceMembershipId == row.membership.Id &&
                    assignment.PortfolioId == portfolioId),
                string.Join(", ", assignments
                    .Where(assignment =>
                        assignment.WorkspaceMembershipId == row.membership.Id &&
                        assignment.PortfolioId == portfolioId &&
                        assignment.Status == MembershipRoleAssignmentStatus.Active)
                    .OrderBy(assignment => assignment.RoleProfile!.DisplayName)
                    .ThenBy(assignment => assignment.Id)
                    .Select(assignment => assignment.RoleProfile!.DisplayName)),
                row.user.PasswordHash == null || row.user.PasswordHash == string.Empty,
                row.context.CreatedAtUtc))
            .ToListAsync(ct);
        return Ok(new TeamMemberPageDto(items, total, query.NormalizedSkip, query.NormalizedTake));
    }

    [HttpGet("members/{accessContextId:int}/assignments")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamRead)]
    public async Task<ActionResult<TeamAssignmentPageDto>> ListAssignments(
        int accessContextId,
        [FromQuery] ListQuery query,
        CancellationToken ct)
    {
        var portfolioId = GetPortfolioId();
        var filtered = _db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment =>
                assignment.PortfolioId == portfolioId &&
                assignment.WorkspaceMembership!.AccessContextId == accessContextId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            filtered = filtered.Where(assignment =>
                EF.Functions.ILike(assignment.RoleProfile!.DisplayName, $"%{term}%") ||
                EF.Functions.ILike(assignment.RoleProfile.Key, $"%{term}%"));
        }
        var total = await filtered.CountAsync(ct);
        var sorted = query.SortField switch
        {
            "role" => query.SortDescending
                ? filtered.OrderByDescending(item => item.RoleProfile!.DisplayName)
                : filtered.OrderBy(item => item.RoleProfile!.DisplayName),
            "status" => query.SortDescending
                ? filtered.OrderByDescending(item => item.Status)
                : filtered.OrderBy(item => item.Status),
            _ => query.SortDescending
                ? filtered.OrderByDescending(item => item.EffectiveFromUtc)
                : filtered.OrderBy(item => item.EffectiveFromUtc),
        };
        var items = await sorted.ThenBy(item => item.Id)
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(item => new TeamAssignmentSummaryDto(
                item.Id,
                item.RoleProfile!.Key,
                item.RoleProfile.DisplayName,
                item.Status,
                item.ScopeKind,
                item.SelectedProperties.Count(),
                item.SelectedProperties
                    .OrderBy(scope => scope.PropertyId)
                    .Select(scope => scope.PropertyId)
                    .ToArray(),
                item.EffectiveFromUtc,
                item.EffectiveToUtc))
            .ToListAsync(ct);
        return Ok(new TeamAssignmentPageDto(items, total, query.NormalizedSkip, query.NormalizedTake));
    }

    [HttpGet("role-profiles")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamRead)]
    public async Task<ActionResult<IReadOnlyList<TeamRoleProfileDto>>> ListRoleProfiles(CancellationToken ct)
    {
        var items = await _db.RoleProfiles.AsNoTracking()
            .Where(profile =>
                profile.Key == RoleProfileKeys.WorkspaceAdministrator ||
                profile.Key == RoleProfileKeys.PropertyManager ||
                profile.Key == RoleProfileKeys.LeasingAgent ||
                profile.Key == RoleProfileKeys.MaintenanceTechnician)
            .OrderBy(profile => profile.Id)
            .Select(profile => new TeamRoleProfileDto(
                profile.Key, profile.DisplayName, profile.Description,
                profile.DefaultExperience, profile.DefaultScopeKind))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("memberships")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    public async Task<IActionResult> CreateMembership(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreateWorkspaceMembershipRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var envelope, out var failure)) return failure!;
        if (request.EffectiveFromUtc == default) return BadRequest(new { error = "EffectiveFromUtc is required." });
        var command = new CreateWorkspaceMembershipCommand(
            envelope.PortfolioId, envelope.UserId, envelope.SessionId,
            envelope.AccessContextId, envelope.AccessRevision,
            request.Email, request.DisplayName, request.RoleProfileKey, request.ScopeKind,
            request.SelectedPropertyIds.Distinct().Order().ToArray(),
            request.EffectiveFromUtc, _webBaseUrl);
        return await Execute("workspace-team.membership.create", envelope.KeyDigest,
            command, CreateCodec, StatusCodes.Status201Created, ct);
    }

    [HttpPost("members/{accessContextId:int}/assignments")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    public async Task<IActionResult> AddAssignment(
        int accessContextId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] AddWorkspaceRoleAssignmentRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveFromUtc == default || request.ExpectedAccessRevision <= 0)
            return BadRequest(new { error = "EffectiveFromUtc and ExpectedAccessRevision are required." });
        var command = new AddWorkspaceRoleAssignmentCommand(
            e.PortfolioId, e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            accessContextId, request.ExpectedAccessRevision, request.RoleProfileKey, request.ScopeKind,
            request.SelectedPropertyIds.Distinct().Order().ToArray(), request.EffectiveFromUtc);
        return await Execute("workspace-team.assignment.add", e.KeyDigest,
            command, MutationCodec, StatusCodes.Status201Created, ct);
    }

    [HttpPatch("members/{accessContextId:int}/assignments/{assignmentId:int}/end")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    public async Task<IActionResult> EndAssignment(
        int accessContextId,
        int assignmentId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] EndWorkspaceRoleAssignmentRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.EffectiveToUtc == default || request.ExpectedAccessRevision <= 0)
            return BadRequest(new { error = "EffectiveToUtc and ExpectedAccessRevision are required." });
        var command = new EndWorkspaceRoleAssignmentCommand(
            e.PortfolioId, e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            accessContextId, request.ExpectedAccessRevision, assignmentId,
            request.EffectiveToUtc);
        return await Execute("workspace-team.assignment.end", e.KeyDigest,
            command, MutationCodec, StatusCodes.Status200OK, ct);
    }

    [HttpPut("members/{accessContextId:int}/assignments/{assignmentId:int}/properties")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    public async Task<IActionResult> ReplaceProperties(
        int accessContextId,
        int assignmentId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReplaceWorkspaceAssignmentPropertyScopeRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.ExpectedAccessRevision <= 0)
            return BadRequest(new { error = "ExpectedAccessRevision is required." });
        var command = new ReplaceWorkspaceAssignmentPropertyScopeCommand(
            e.PortfolioId, e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            accessContextId, request.ExpectedAccessRevision, assignmentId,
            request.PropertyIds.Distinct().Order().ToArray());
        return await Execute("workspace-team.assignment.properties.replace", e.KeyDigest,
            command, MutationCodec, StatusCodes.Status200OK, ct);
    }

    [HttpPatch("members/{accessContextId:int}/status")]
    [Authorize(Policy = CapabilityPolicy.Prefix + CapabilityKeys.TeamManage)]
    public async Task<IActionResult> ChangeStatus(
        int accessContextId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ChangeWorkspaceMembershipStatusRequest request,
        CancellationToken ct)
    {
        if (!TryEnvelope(idempotencyKey, out var e, out var failure)) return failure!;
        if (request.ExpectedAccessRevision <= 0 || !Enum.IsDefined(request.Action))
            return BadRequest(new { error = "ExpectedAccessRevision and a valid status action are required." });
        var command = new ChangeWorkspaceMembershipStatusCommand(
            e.PortfolioId, e.UserId, e.SessionId, e.AccessContextId, e.AccessRevision,
            accessContextId, request.ExpectedAccessRevision, request.Action);
        return await Execute("workspace-team.membership.status", e.KeyDigest,
            command, MutationCodec, StatusCodes.Status200OK, ct);
    }

    private async Task<IActionResult> Execute<TCommand, TResult>(
        string commandType,
        string key,
        TCommand command,
        AtomicJsonResultCodec<TResult> codec,
        int successStatus,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(commandType, $"{GetPortfolioId()}:{key}"), command, codec, ct);
            return StatusCode(successStatus, new
            {
                outcome.Value,
                replayed = outcome.Disposition == AtomicCommandDisposition.Replayed,
            });
        }
        catch (StaleAccessRevisionException ex)
        {
            Response.Headers["X-Access-Envelope-Refresh"] = "required";
            return Conflict(new { error = ex.Message, ex.PresentedRevision, ex.CurrentRevision });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private bool TryEnvelope(string? idempotencyKey, out CommandEnvelope envelope, out IActionResult? failure)
    {
        envelope = default;
        failure = null;
        var normalized = idempotencyKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        {
            failure = BadRequest(new { error = "A request key is required and cannot exceed 200 characters." });
            return false;
        }
        if (!TryGetActiveAccessContext(out var active))
        {
            failure = Forbid();
            return false;
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        envelope = new CommandEnvelope(
            active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision, digest);
        return true;
    }

    private readonly record struct CommandEnvelope(
        int PortfolioId,
        int UserId,
        Guid SessionId,
        int AccessContextId,
        long AccessRevision,
        string KeyDigest);
}
