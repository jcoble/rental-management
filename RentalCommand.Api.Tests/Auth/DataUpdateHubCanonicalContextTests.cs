using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Hubs;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Tests.Auth;

public sealed class DataUpdateHubCanonicalContextTests
{
    [Fact]
    public async Task OnConnectedAsync_JoinsOnlyGroupsFromCanonicalAccessContext()
    {
        var active = ValidAccessContext(userId: 23, portfolioId: 47, workspaceMembershipId: 9);
        var (hub, groups) = CreateHub(active, claimsUserId: 999, claimsPortfolioId: 888);

        await hub.OnConnectedAsync();

        groups.Verify(manager => manager.AddToGroupAsync(
            "connection-1",
            DataUpdateHub.SessionRevisionGroup(active.SessionId, active.AccessRevision),
            CancellationToken.None), Times.Once);
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), "user-999", It.IsAny<CancellationToken>()), Times.Never);
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), "portfolio-888", It.IsAny<CancellationToken>()), Times.Never);
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), "portfolio-47", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_RelationshipOnlyContext_JoinsOnlyItsSessionRevisionGroup()
    {
        var relationshipOnly = ValidAccessContext(userId: 23, portfolioId: 47, workspaceMembershipId: null);
        var (hub, groups) = CreateHub(relationshipOnly);

        await hub.OnConnectedAsync();

        groups.Verify(manager => manager.AddToGroupAsync(
            "connection-1",
            DataUpdateHub.SessionRevisionGroup(relationshipOnly.SessionId, relationshipOnly.AccessRevision),
            CancellationToken.None), Times.Once);
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), It.Is<string>(group => group.StartsWith("portfolio-")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnConnectedAsync_StaleOrRevokedCanonicalContext_FailsClosed(bool stale)
    {
        var presented = ValidAccessContext(workspaceMembershipId: 7);
        UnauthorizedAccessException failure = stale
            ? new StaleAccessRevisionException(presented.AccessRevision, presented.AccessRevision + 1)
            : new AccessContextUnavailableException();
        var (hub, groups) = CreateHub(presented, resolverFailure: failure);

        var action = () => hub.OnConnectedAsync();

        await action.Should().ThrowAsync<HubException>()
            .WithMessage("The validated access context is unavailable.");
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_WithoutCanonicalAccessContext_FailsClosed()
    {
        var (hub, groups) = CreateHub(canonicalItem: null, claimsUserId: 23, claimsPortfolioId: 47);

        var action = () => hub.OnConnectedAsync();

        await action.Should().ThrowAsync<HubException>()
            .WithMessage("The validated access context is unavailable.");
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(InvalidCanonicalContexts))]
    public async Task OnConnectedAsync_WithInvalidCanonicalCoordinates_FailsClosed(
        ActiveAccessContext invalid)
    {
        var (hub, groups) = CreateHub(invalid);

        var action = () => hub.OnConnectedAsync();

        await action.Should().ThrowAsync<HubException>()
            .WithMessage("The validated access context is unavailable.");
        groups.Verify(manager => manager.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<ActiveAccessContext> InvalidCanonicalContexts => new()
    {
        ValidAccessContext() with { SessionId = Guid.Empty },
        ValidAccessContext() with { UserId = 0 },
        ValidAccessContext() with { AccessContextId = 0 },
        ValidAccessContext() with { PortfolioId = 0 },
        ValidAccessContext() with { AccessRevision = 0 },
    };

    private static (DataUpdateHub Hub, Mock<IGroupManager> Groups) CreateHub(
        object? canonicalItem,
        int claimsUserId = 1,
        int claimsPortfolioId = 1,
        Exception? resolverFailure = null)
    {
        var httpContext = new DefaultHttpContext();
        if (canonicalItem is not null)
        {
            httpContext.Items[CanonicalAccessContextHttpItem.Key] = canonicalItem;
        }

        var httpContextFeature = new Mock<IHttpContextFeature>();
        httpContextFeature.SetupGet(feature => feature.HttpContext).Returns(httpContext);
        var features = new FeatureCollection();
        features.Set(httpContextFeature.Object);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, claimsUserId.ToString()),
            new Claim("portfolioId", claimsPortfolioId.ToString()),
        ], "test"));
        var callerContext = new Mock<HubCallerContext>();
        callerContext.SetupGet(context => context.ConnectionId).Returns("connection-1");
        callerContext.SetupGet(context => context.Features).Returns(features);
        callerContext.SetupGet(context => context.User).Returns(principal);

        var groups = new Mock<IGroupManager>();
        groups.Setup(manager => manager.AddToGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(manager => manager.RemoveFromGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var resolver = new Mock<IActiveAccessContextResolver>();
        if (canonicalItem is ActiveAccessContext active)
        {
            var setup = resolver.Setup(service => service.ResolveAsync(
                active.SessionId,
                active.UserId,
                active.AccessContextId,
                active.AccessRevision,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()));
            if (resolverFailure is null)
            {
                setup.ReturnsAsync(active);
            }
            else
            {
                setup.ThrowsAsync(resolverFailure);
            }
        }

        var hub = new DataUpdateHub(
            resolver.Object,
            TimeProvider.System,
            Mock.Of<ILogger<DataUpdateHub>>())
        {
            Context = callerContext.Object,
            Groups = groups.Object,
        };

        return (hub, groups);
    }

    private static ActiveAccessContext ValidAccessContext(
        int userId = 1,
        int portfolioId = 1,
        int? workspaceMembershipId = null) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            userId,
            AccessContextId: 3,
            portfolioId,
            AccessRevision: 5,
            LastAuthorizedExperience: null,
            WorkspaceMembershipId: workspaceMembershipId,
            DefaultExperience: null);
}
