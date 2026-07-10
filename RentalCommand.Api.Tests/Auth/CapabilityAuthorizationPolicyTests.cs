using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class CapabilityAuthorizationPolicyTests
{
    [Fact]
    public async Task DynamicPolicy_UsesStableCapabilityRequirement_NotRoleAliases()
    {
        var services = new ServiceCollection();
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, CapabilityAuthorizationPolicyProvider>();
        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await policyProvider.GetPolicyAsync(
            CapabilityPolicy.For(CapabilityKeys.MoneyPaymentsManage));

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<CapabilityRequirement>()
            .Should().ContainSingle(requirement =>
                requirement.CapabilityKey == CapabilityKeys.MoneyPaymentsManage);
        policy.Requirements.OfType<RolesAuthorizationRequirement>().Should().BeEmpty();
    }

    [Fact]
    public async Task CapabilityHandler_WithoutTypedTarget_FailsClosedBeforeEvaluation()
    {
        var sessionId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sid", sessionId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "41"),
            new Claim("ctx", "73"),
            new Claim("ar", "5"),
        ], "Test"));
        var requirement = new CapabilityRequirement(CapabilityKeys.MoneyPaymentsManage);
        var authorizationContext = new AuthorizationHandlerContext(
            [requirement], principal, resource: null);
        var evaluator = new RecordingAuthorizationEvaluator();
        IAuthorizationHandler handler = new CapabilityAuthorizationHandler(
            new StubAccessContextResolver(new ActiveAccessContext(
                sessionId,
                UserId: 41,
                AccessContextId: 73,
                PortfolioId: 19,
                AccessRevision: 5,
                LastAuthorizedExperience: null,
                WorkspaceMembershipId: 91,
                DefaultExperience: null)),
            evaluator,
            TimeProvider.System);

        await handler.HandleAsync(authorizationContext);

        authorizationContext.HasSucceeded.Should().BeFalse();
        evaluator.CallCount.Should().Be(0);
    }

    private sealed class StubAccessContextResolver : IActiveAccessContextResolver
    {
        private readonly ActiveAccessContext _context;

        public StubAccessContextResolver(ActiveAccessContext context) => _context = context;

        public Task<ActiveAccessContext> ResolveAsync(
            Guid sessionId,
            int userId,
            int accessContextId,
            long presentedAccessRevision,
            DateTime utcNow,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_context);
    }

    private sealed class RecordingAuthorizationEvaluator : IWorkspaceAuthorizationEvaluator
    {
        public int CallCount { get; private set; }

        public Task<bool> HasCapabilityAsync(
            ActiveAccessContext accessContext,
            string capabilityKey,
            WorkspaceAuthorizationTarget? target,
            DateTime utcNow,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(true);
        }
    }
}
