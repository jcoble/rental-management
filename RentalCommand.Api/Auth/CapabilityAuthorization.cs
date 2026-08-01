using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Auth;

/// <summary>Dynamic policy name for stable capability action keys.</summary>
public static class CapabilityPolicy
{
    public const string Prefix = "Capability:";

    public static string For(string capabilityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityKey);
        return Prefix + capabilityKey;
    }
}

public static class CanonicalManagementPolicy
{
    public const string Name = "CanonicalManagement";
}

public sealed record CanonicalManagementRequirement : IAuthorizationRequirement;

public sealed record CapabilityRequirement(string CapabilityKey) : IAuthorizationRequirement;

/// <summary>
/// Creates capability policies on demand while delegating every non-capability policy (including
/// PlatformAdmin) to ASP.NET's default provider. No role alias is accepted or translated.
/// </summary>
public sealed class CapabilityAuthorizationPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public CapabilityAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options) =>
        _fallback = new DefaultAuthorizationPolicyProvider(options);

    public bool AllowsCachingPolicies => true;

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(CapabilityPolicy.Prefix, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        var capabilityKey = policyName[CapabilityPolicy.Prefix.Length..];
        if (string.IsNullOrWhiteSpace(capabilityKey))
        {
            return Task.FromResult<AuthorizationPolicy?>(null);
        }

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new CapabilityRequirement(capabilityKey))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

/// <summary>
/// Resolves the signed-in session/context/revision claims and delegates to the DB-side same-assignment
/// evaluator. This handler is dormant until a controller explicitly uses a Capability policy.
/// </summary>
public sealed class CapabilityAuthorizationHandler : AuthorizationHandler<CapabilityRequirement>
{
    private readonly IActiveAccessContextResolver _accessContextResolver;
    private readonly IWorkspaceAuthorizationEvaluator _authorizationEvaluator;
    private readonly IAuthSecurityClock _securityClock;

    public CapabilityAuthorizationHandler(
        IActiveAccessContextResolver accessContextResolver,
        IWorkspaceAuthorizationEvaluator authorizationEvaluator,
        IAuthSecurityClock securityClock)
    {
        _accessContextResolver = accessContextResolver;
        _authorizationEvaluator = authorizationEvaluator;
        _securityClock = securityClock;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CapabilityRequirement requirement)
    {
        if (!TryReadKernelClaims(context.User, out var sessionId, out var userId, out var accessContextId,
                out var accessRevision))
        {
            return;
        }

        var utcNow = _securityClock.UtcNow();
        ActiveAccessContext activeContext;
        try
        {
            activeContext = await _accessContextResolver.ResolveAsync(
                sessionId, userId, accessContextId, accessRevision, utcNow);
        }
        catch (AccessContextUnavailableException)
        {
            return;
        }
        catch (StaleAccessRevisionException)
        {
            return;
        }

        var target = context.Resource switch
        {
            WorkspaceAuthorizationTarget typedTarget => typedTarget,
            HttpContext httpContext when
                httpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
                value is ActiveAccessContext active &&
                active.AccessContextId == activeContext.AccessContextId =>
                    new WorkspaceCapabilityAuthorizationTarget(active.PortfolioId),
            _ => null,
        };
        if (target is null ||
            target.PortfolioId != activeContext.PortfolioId)
        {
            return;
        }

        if (await _authorizationEvaluator.HasCapabilityAsync(
                activeContext, requirement.CapabilityKey, target, utcNow))
        {
            context.Succeed(requirement);
        }
    }

    private static bool TryReadKernelClaims(
        ClaimsPrincipal principal,
        out Guid sessionId,
        out int userId,
        out int accessContextId,
        out long accessRevision)
    {
        sessionId = default;
        userId = default;
        accessContextId = default;
        accessRevision = default;

        return Guid.TryParse(principal.FindFirstValue("sid"), out sessionId) &&
               principal.TryReadSubjectUserId(out userId) &&
               int.TryParse(principal.FindFirstValue("ctx"), out accessContextId) &&
               long.TryParse(principal.FindFirstValue("ar"), out accessRevision);
    }
}

/// <summary>
/// Coarse management-surface admission for canonical tokens. It proves in one SQL statement that
/// the selected context owns an effective Team assignment with at least one non-assigned-work
/// capability. Endpoint queries and commands still apply their typed capability/resource checks.
/// Relationship-only Tenant/Owner contexts and assigned-work-only technicians fail closed.
/// </summary>
public sealed class CanonicalManagementAuthorizationHandler
    : AuthorizationHandler<CanonicalManagementRequirement>
{
    private readonly RentalCommand.Data.RentalCommandDbContext _db;
    private readonly IAuthSecurityClock _securityClock;

    public CanonicalManagementAuthorizationHandler(
        RentalCommand.Data.RentalCommandDbContext db,
        IAuthSecurityClock securityClock)
    {
        _db = db;
        _securityClock = securityClock;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CanonicalManagementRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext ||
            !httpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) ||
            value is not ActiveAccessContext active ||
            active.WorkspaceMembershipId is null)
        {
            return;
        }

        var now = _securityClock.UtcNow();
        var allowed = await _db.MembershipRoleAssignments
            .AsNoTracking()
            .WhereEffective(now)
            .AnyAsync(assignment =>
                assignment.WorkspaceMembershipId == active.WorkspaceMembershipId.Value &&
                assignment.PortfolioId == active.PortfolioId &&
                assignment.WorkspaceMembership!.AccessContextId == active.AccessContextId &&
                assignment.WorkspaceMembership.AccessContext!.UserId == active.UserId &&
                assignment.WorkspaceMembership.AccessContext.AccessRevision == active.AccessRevision &&
                assignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.AuthorizationTargetKind !=
                    CapabilityAuthorizationTargetKind.WorkOrder));
        if (allowed)
        {
            context.Succeed(requirement);
        }
    }
}
