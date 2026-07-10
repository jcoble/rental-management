using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Authorization;

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
    private readonly TimeProvider _timeProvider;

    public CapabilityAuthorizationHandler(
        IActiveAccessContextResolver accessContextResolver,
        IWorkspaceAuthorizationEvaluator authorizationEvaluator,
        TimeProvider timeProvider)
    {
        _accessContextResolver = accessContextResolver;
        _authorizationEvaluator = authorizationEvaluator;
        _timeProvider = timeProvider;
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

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
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

        var resourceScope = context.Resource as ICapabilityAuthorizationResource;
        if (resourceScope is not null && resourceScope.PortfolioId != activeContext.PortfolioId)
        {
            return;
        }

        if (await _authorizationEvaluator.HasCapabilityAsync(
                activeContext, requirement.CapabilityKey, resourceScope?.PropertyId, utcNow))
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
               int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId) &&
               int.TryParse(principal.FindFirstValue("ctx"), out accessContextId) &&
               long.TryParse(principal.FindFirstValue("ar"), out accessRevision);
    }
}
