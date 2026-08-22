using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Owners;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Conversations;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class WorkspaceLegacyReceiptReplayCoverageTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2099, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const int AccessContextId = 7101;

    // Frozen BASE fingerprints computed once from the concrete legacy commands below. Never regenerate.
    private const string CredentialFingerprint = "5068bdf4a0886762e7a5451deda9abdd19eddad00b9c91f5c6ca769e59db8fa8";
    private const string RemoveCredentialFingerprint = "c9e1334bcb93cc847e3db4dc6437c3d4b66ef78607649f8e2782502ce8e86157";
    private const string UsageFingerprint = "d40b69c744e1874bcb80f18ebe87923591b90042e466921e10b64e28533050b1";
    private const string QaFingerprint = "2ce43de0d27d8b52f70df8ebf80605f10634fbd99a2981eb9868aadf4e65b855";
    private const string WorkspaceCoreFingerprint = "308fb113bed9154f56db98c777006402c1f7e615507a492c5557f49977899ecf";
    private const string GuidedTenantFingerprint = "e6d6cec53fc8f4214a3d6b6467de72fc64fc67a3a4fb8354b0f5f97a0d5b8431";
    private const string InspectionFingerprint = "1f09a283479b95ddb042c87bdeaddac406232fd80621efca9cba9f6e908110fb";
    private static readonly string[] RemainingFingerprints =
    [
        // Frozen BASE fingerprints computed once from the concrete legacy commands. Never regenerate.
        "84f5c603c7b6777f123ecc2b7fe3a66bfa50b66e4fe34ca3ef284d2feba08d07",
        "a1aaa63baa6115de747f779f88a5542aa14554197e9e16358a06ed15b3e4a8ae",
        "325e27d4fa56a713e7253f667a7860f981274f3890167c60f8ffb0495ea12ef3",
        "e2e1c7cfcb5019d8e3a28f777ddcdebe0437fec306ce2e408c96c72d94add57d",
        "b3c6edabf87d98377c7a4486d4492be214fb4b831673304da6f9b305396e7194",
        "64548965ed9a7cb254bb5af40fec4617b356d620ef73c77b53fb06124f5d4e86",
        "199cb3e52b3fd0250ed6075cc29f8741e409246ef1d937d5378cc6fec8b6d572",
        "2051913ea477b9c5492eed19e7031e3e8d8405cee9835f08a68f75ccfa793c42",
        "961ac114b2df17aeddeb7511c6f11c1249d62e3daf1d8080e6bf583ec54a30a9",
        "ec8709eebcc4291ece1c4f0758e5a6266c9d8e1433868d2d25afbf9a7183b216",
        "e627f3ba619907a3bd7953734cb45edbc4dac88439f83b6333adb0b21037b165",
        "0f4c5a3ac3522c6917b9df13f0768e8512603cd7e533b7fd637bbd228372040e",
    ];

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public WorkspaceLegacyReceiptReplayCoverageTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        await SeedAuthorityAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_context.ConnectionString).UseAtomicPersistenceKernel(provider));
        services.AddIdentityCore<ApplicationUser>()
            .AddEntityFrameworkStores<RentalCommandDbContext>();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task OwnerAndConversationFamily_ReplaysFiveFrozenLegacyReceipts_AndReauthorizes()
    {
        await SeedOwnerTeamAndConversationAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        const string ownerResult = "{\"SourceNotificationId\":7301,\"OwnerEntityId\":7201,\"StaffNotificationIds\":[],\"RecordedAtUtc\":\"2099-08-21T12:00:00Z\"}";
        await SeedReceiptAsync("owner-portal.approval-decision",
            "1:7301:fe783e27e5cf2c6912e11405fe2e341010b7e9fde9fdd9ea4f04b0bfb2758829",
            RemainingFingerprints[0], "owner-portal.approval-decision.v1", ownerResult);
        await SeedReceiptAsync("owner-portal.message-reply",
            "1:7302:ca997fd36a67773c4493b64423994d8c5b01e4aca208aaf1780c3371698c6fd0",
            RemainingFingerprints[1], "owner-portal.message-reply.v1",
            ownerResult.Replace("7301", "7302", StringComparison.Ordinal));
        await SeedReceiptAsync("conversation.post-message",
            "conversation:1:7401:none:a4c84b140b213bc9bc70f6e804454c3ed4527f8f39ab3c5c21c4c180e93d3726",
            RemainingFingerprints[2], "conversation-message-result.v1",
            "{\"Outcome\":0,\"ConversationId\":7401,\"MessageId\":9904,\"NotificationIds\":[]}");
        await SeedReceiptAsync("owner-entity.portal-access.activate",
            "1:7201:93cd3a7769ea8d2d621f87dd34516aae00dd96b9ff9672725be37fbff8d2cf40",
            RemainingFingerprints[3], "owner-portal-access.activation.v1",
            "{\"Outcome\":2,\"OwnerEntityId\":7201,\"OwnerEmail\":\"owner@example.test\",\"UserId\":2,\"TargetAccessContextId\":7102,\"OwnerUserAccessId\":7601,\"AccessRevision\":1,\"RequiresAccountActivation\":false,\"InvitationExpiresAtUtc\":null}");
        await SeedReceiptAsync("owner-entity.portal-access.revoke",
            "1:7201:6d2e9cb184086ca234c5a86f3ff72887bb693bdd03e1d6d386f623bc8fc3afb7",
            RemainingFingerprints[4], "owner-portal-access.revocation.v1",
            "{\"Outcome\":0,\"OwnerEntityId\":7201,\"OwnerEmail\":\"owner@example.test\",\"RevokedRelationshipCount\":1,\"TargetAccessContextId\":7102,\"OwnerUserAccessId\":7601,\"AccessRevision\":2}");
        var notifications = await db.Notifications.CountAsync();
        var messages = await db.ConversationMessages.CountAsync();
        var ownerAccesses = await db.OwnerUserAccesses.CountAsync();

        var ownerController = Controller(new OwnerPortalController(
            Mock.Of<IOwnerPortalService>(), Mock.Of<IOwnerStatementService>(),
            new FixedTimeProvider(Now), db, writes), OwnerAccess());
        (await ownerController.DecideApproval(7301, "frozen-owner-approval",
            new DecideOwnerApprovalRequest
            {
                Decision = OwnerApprovalDecision.Approved, Note = "Frozen approval",
            }, default)).Should().BeOfType<OkObjectResult>();
        (await ownerController.ReplyToMessage(7302, "frozen-owner-reply",
            new ReplyToOwnerMessageRequest { Body = "Frozen owner reply" }, default))
            .Should().BeOfType<OkObjectResult>();

        var conversations = new ConversationService(db, Mock.Of<IRealtimeInvalidationQueue>(),
            Mock.Of<IFairHousingReviewService>(), NullLogger<ConversationService>.Instance,
            new FixedTimeProvider(Now), writes);
        (await conversations.PostMessageAuthorizedAsync(Scope(), 7401,
            "Frozen conversation reply", [], "frozen-conversation"))!.Id.Should().Be(7401);
        var owners = new OwnerEntityService(db, Mock.Of<IDataUpdateService>(),
            new FixedTimeProvider(Now), new ConfigurationBuilder().Build(), writes);
        (await owners.ActivateOwnerPortalAccessAsync(Scope(), 7201,
            new ActivateOwnerPortalAccessRequest(Reason: "Frozen activate"),
            "frozen-owner-activate")).Replayed.Should().BeTrue();
        (await owners.RevokeOwnerPortalAccessAsync(Scope(), 7201,
            new RevokeOwnerPortalAccessRequest("Frozen revoke"), "frozen-owner-revoke"))
            .Replayed.Should().BeTrue();

        (await db.Notifications.CountAsync()).Should().Be(notifications);
        (await db.ConversationMessages.CountAsync()).Should().Be(messages);
        (await db.OwnerUserAccesses.CountAsync()).Should().Be(ownerAccesses);
        var relationship = await db.OwnerUserAccesses.SingleAsync(row => row.Id == 7601);
        relationship.RevokedAtUtc = Now;
        relationship.RevokedByUserId = 1;
        await db.SaveChangesAsync();
        (await ownerController.DecideApproval(7301, "frozen-owner-approval",
            new DecideOwnerApprovalRequest
            {
                Decision = OwnerApprovalDecision.Approved, Note = "Frozen approval",
            }, default)).Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task WorkspaceAuthorityFamily_ReplaysSevenFrozenLegacyReceipts_AndReauthorizes()
    {
        await SeedOwnerTeamAndConversationAsync();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        await SeedReceiptAsync("workspace-experience.select",
            "1:7101:9badd178cb025101079ee0bc40059829f571ad61bb5322ee9cd453eebc55c284",
            RemainingFingerprints[5], "workspace-experience-select-result:v1", "{\"Experience\":1}");
        await SeedReceiptAsync("workspace-team.membership.create",
            "1:2015c34ee8c7d985c33de8eb49a8d22951c6708852bcf684d19585e7d1cd8cc0",
            RemainingFingerprints[6], "workspace-team.membership.create.v1",
            "{\"UserId\":9905,\"AccessContextId\":9906,\"WorkspaceMembershipId\":9907,\"AssignmentId\":9908,\"AccessRevision\":1,\"RequiresAccountActivation\":true}");
        const string mutation = "{\"AccessContextId\":7102,\"WorkspaceMembershipId\":7102,\"AssignmentId\":7102,\"AccessRevision\":1,\"ContextStatus\":0,\"MembershipStatus\":0}";
        var teamReceipts = new[]
        {
            ("workspace-team.assignment.add", "afd31c79a630a46c7c24097d2a1e1a4d204014c60ac5a539935680ceaef404cc", RemainingFingerprints[7]),
            ("workspace-team.assignment.end", "7064771ed019209f9d70f1820c84fcd9dce09c0f8a68cc5c0c09d2ebb09b660f", RemainingFingerprints[8]),
            ("workspace-team.assignment.properties.replace", "402153430e0f1dd30ec94be4b88a9d6fe3dcc90b6b7a845e8b379fb38a5d2857", RemainingFingerprints[9]),
            ("workspace-team.membership.status", "aa19dfbb3344cd36a70d7e76380762c4fac8098abb0ec17ef525a8308d4d49e5", RemainingFingerprints[10]),
        };
        foreach (var receipt in teamReceipts)
            await SeedReceiptAsync(receipt.Item1, $"1:{receipt.Item2}", receipt.Item3,
                "workspace-team.mutation.v1", mutation);
        await SeedReceiptAsync("workspace-invitation.activate",
            "2:e5e99ba746fbfe24793f49a5804f50334819088bcbf4027a5abc850719a353bf",
            RemainingFingerprints[11], "workspace-invitation-activation-result:v1",
            "{\"Outcome\":1,\"UserId\":2,\"PortfolioId\":1,\"AccessContextId\":7102}");
        var memberships = await db.WorkspaceMemberships.CountAsync();
        var assignments = await db.MembershipRoleAssignments.CountAsync();
        var invitationAccepted = await db.WorkspaceInvitations
            .Where(row => row.Id == 7501).Select(row => row.AcceptedAtUtc).SingleAsync();

        var experience = Controller(new WorkspaceExperienceController(db, writes,
            Mock.Of<IAccessEnvelopeQuery>(), new FixedTimeProvider(Now)), ActiveAccess());
        await experience.Select("frozen-experience",
            new SelectWorkspaceExperienceRequest { Experience = WorkspaceExperience.Management }, default);
        var team = Controller(new TeamController(db, writes,
            new WorkspaceAccessRevisionGuard(), new MembershipAssignmentScopeValidator(db),
            new ConfigurationBuilder().Build()), ActiveAccess());
        await team.CreateMembership("frozen-team-create", new CreateWorkspaceMembershipRequest
        {
            Email = "frozen-member@example.test", DisplayName = "Frozen Member",
            RoleProfileKey = RoleProfileKeys.LeasingAgent,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties, EffectiveFromUtc = Now,
        }, default);
        await team.AddAssignment(7102, "frozen-team-add", new AddWorkspaceRoleAssignmentRequest
        {
            ExpectedAccessRevision = 1, RoleProfileKey = RoleProfileKeys.PropertyManager,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties, EffectiveFromUtc = Now,
        }, default);
        await team.EndAssignment(7102, 7102, "frozen-team-end",
            new EndWorkspaceRoleAssignmentRequest { ExpectedAccessRevision = 1, EffectiveToUtc = Now }, default);
        await team.ReplaceProperties(7102, 7102, "frozen-team-replace",
            new ReplaceWorkspaceAssignmentPropertyScopeRequest { ExpectedAccessRevision = 1 }, default);
        await team.ChangeStatus(7102, "frozen-team-status",
            new ChangeWorkspaceMembershipStatusRequest
            {
                ExpectedAccessRevision = 1, Action = WorkspaceMembershipStatusAction.Suspend,
            }, default);
        var invitation = new WorkspaceInvitationsController(db,
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(), writes);
        await invitation.Activate(new ActivateWorkspaceInvitationRequest
        {
            Token = "frozen-invitation-token", Password = "Password1!",
        }, default);

        (await db.WorkspaceMemberships.CountAsync()).Should().Be(memberships);
        (await db.MembershipRoleAssignments.CountAsync()).Should().Be(assignments);
        (await db.WorkspaceInvitations.Where(row => row.Id == 7501)
            .Select(row => row.AcceptedAtUtc).SingleAsync()).Should().Be(invitationAccepted);
        var session = await db.AuthSessions.SingleAsync(row => row.Id == SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = Now;
        await db.SaveChangesAsync();
        (await experience.Select("frozen-experience",
            new SelectWorkspaceExperienceRequest { Experience = WorkspaceExperience.Management }, default))
            .Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task AiFamily_ReplaysFiveFrozenLegacyReceipts_AndReauthorizes()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var access = ActiveAccess();
        var authorization = new Mock<IWorkspaceAuthorizationEvaluator>();
        authorization.Setup(evaluator => evaluator.HasCapabilityAsync(
                It.IsAny<ActiveAccessContext>(), It.IsAny<string>(),
                It.IsAny<WorkspaceAuthorizationTarget>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var credentials = new WorkspaceLlmCredentialService(
            db, new FrozenDataProtectionProvider(), [new SuccessfulProbe()], authorization.Object,
            new FixedTimeProvider(Now), writes);

        await SeedReceiptAsync("ai.integration.credential.activate", "1:frozen-activate",
            CredentialFingerprint, "ai.integration.status.v1", StatusJson);
        await SeedReceiptAsync("ai.integration.credential.rotate", "1:frozen-rotate",
            CredentialFingerprint, "ai.integration.status.v1", StatusJson);
        await SeedReceiptAsync("ai.integration.credential.remove", "1:frozen-remove",
            RemoveCredentialFingerprint, "ai.integration.remove.v1", "{\"Removed\":true}");
        await SeedReceiptAsync("ai.integration.usage.record", "1:frozen-usage",
            UsageFingerprint, "ai.integration.usage.v1", "{\"EvidenceId\":9901}");
        await SeedReceiptAsync("portfolio.qa.delivery", "1:frozen-qa", QaFingerprint,
            "portfolio.qa.delivery.v1", "{\"DeliveredChannels\":[\"Email\"]}");
        db.LlmUsageEvidence.Add(new LlmUsageEvidence
        {
            Id = 9901, PortfolioId = 1,
            Provider = "openai", ModelId = "gpt-4o", Feature = "portfolio.qa",
            LatencyMilliseconds = 0, InputUnits = 0, OutputUnits = 0,
            EstimatedCostUsd = 0, OccurredAtUtc = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var credentialRows = await db.WorkspaceLlmCredentials.CountAsync();
        var usageRows = await db.LlmUsageEvidence.CountAsync();
        var outboxRows = await db.OutboxMessages.CountAsync();

        (await credentials.ActivateAsync(access, new("openai", "gpt-4o", "sk-frozen")
            { ClientOperationId = "frozen-activate" })).Configured.Should().BeTrue();
        (await credentials.RotateAsync(access, new("openai", "gpt-4o", "sk-frozen")
            { ClientOperationId = "frozen-rotate" })).Configured.Should().BeTrue();
        await credentials.RemoveAsync(access, "frozen-remove");
        await credentials.RecordUsageAsync(1, "openai", "gpt-4o", "portfolio.qa",
            10, 20, 5, 0.01m, usageEventIdentity: "frozen-usage");

        var qa = new PortfolioQaService(db, new FrozenAnswerProvider(),
            Mock.Of<IAccountingService>(), Mock.Of<IKnowledgeBaseService>(),
            NullLogger<PortfolioQaService>.Instance, new FixedTimeProvider(Now), writes);
        var answer = await qa.AskAsync(Scope(), "Frozen question", null,
            new QaDeliveryOptions(true, false, "owner@example.test", null), "frozen-qa");
        answer.DeliveredChannels.Should().Equal("Email");

        (await db.WorkspaceLlmCredentials.CountAsync()).Should().Be(credentialRows);
        (await db.LlmUsageEvidence.CountAsync()).Should().Be(usageRows);
        (await db.OutboxMessages.CountAsync()).Should().Be(outboxRows);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType.StartsWith("ai.integration") || receipt.CommandType == "portfolio.qa.delivery"))
            .Should().Be(5);

        var context = await db.WorkspaceAccessContexts.SingleAsync(row => row.Id == AccessContextId);
        context.Status = WorkspaceAccessContextStatus.Revoked;
        context.RevokedAtUtc = Now;
        await db.SaveChangesAsync();
        var denied = () => credentials.ActivateAsync(access, new("openai", "gpt-4o", "sk-frozen")
            { ClientOperationId = "frozen-activate" });
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WorkspaceInspectionAndGuidedSetup_ReplayFrozenLegacyReceipts_AndReauthorize()
    {
        await using var serviceScope = _services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var scope = Scope();
        const string portfolioResult = "{\"Id\":1,\"Name\":\"Frozen stored portfolio\",\"Description\":null,\"ManagementCompanyName\":\"Rental Command\",\"TimeZone\":\"UTC\",\"Status\":1,\"Currency\":\"USD\",\"Settings\":null,\"IsSandbox\":false,\"CreatedAt\":\"2099-08-21T12:00:00Z\",\"UpdatedAt\":\"2099-08-21T12:00:00Z\"}";
        const string tenantResult = "[{\"Id\":9902,\"PortfolioId\":1,\"FirstName\":\"Frozen\",\"LastName\":\"Tenant\",\"Email\":null,\"Phone\":null,\"EmergencyContact\":null,\"DateOfBirth\":null,\"Notes\":null,\"CreatedAt\":\"2099-08-21T12:00:00Z\",\"UpdatedAt\":\"2099-08-21T12:00:00Z\"}]";
        const string inspectionResult = "{\"Id\":9903,\"PortfolioId\":1,\"Name\":\"Frozen template\",\"InspectionType\":2,\"IsBuiltIn\":false,\"Items\":[]}";
        await SeedReceiptAsync("rental.workspace.updateportfolio",
            "1:7101:UpdatePortfolio:frozen-workspace", WorkspaceCoreFingerprint,
            "rental.workspace-core-mutation.v1",
            $$"""{"Found":true,"Applied":true,"ResponseJson":{{System.Text.Json.JsonSerializer.Serialize(portfolioResult)}},"PublicApplicationToken":null}""");
        await SeedReceiptAsync("rental.tenant.guided-setup", "1:7101:frozen-guided",
            GuidedTenantFingerprint, "rental.guided-tenant-setup.v1",
            $$"""{"TenantsJson":{{System.Text.Json.JsonSerializer.Serialize(tenantResult)}}}""");
        await SeedReceiptAsync("rental.inspection.template.create",
            "1:7101:Template:Create:0:0:frozen-inspection", InspectionFingerprint,
            "rental.inspection-mutation.v1",
            $$"""{"Found":true,"Applied":true,"EntityId":9903,"ResponseJson":{{System.Text.Json.JsonSerializer.Serialize(inspectionResult)}},"Error":null}""");

        var portfolioName = await db.Portfolios.Where(row => row.Id == 1).Select(row => row.Name).SingleAsync();
        var tenantRows = await db.Tenants.CountAsync();
        var templateRows = await db.InspectionTemplates.CountAsync();
        (await new PortfolioService(db, writes).UpdateAsync(scope, 1,
            new UpdatePortfolioRequest { Name = "Frozen portfolio" }, "frozen-workspace"))!
            .Name.Should().Be("Frozen stored portfolio");
        var tenants = await new TenantService(db, Mock.Of<IDataUpdateService>(),
            new FixedTimeProvider(Now), writes).CreateGuidedSetupBatchAsync(scope,
            new GuidedTenantSetupRequest
            {
                Tenants = [new CreateTenantRequest { FirstName = "Frozen", LastName = "Tenant" }],
            }, "frozen-guided");
        tenants.Should().ContainSingle().Which.Id.Should().Be(9902);
        var inspections = new InspectionService(db, Mock.Of<IDataUpdateService>(),
            Mock.Of<IFileStorage>(), Mock.Of<IInspectionReportPdfGenerator>(),
            NullLogger<InspectionService>.Instance, new FixedTimeProvider(Now), writes);
        (await inspections.CreateTemplateAuthorizedAsync(scope,
            new CreateInspectionTemplateRequest
            {
                Name = "Frozen template", InspectionType = InspectionType.Routine, Items = [],
            }, "frozen-inspection"))!.Id.Should().Be(9903);

        (await db.Portfolios.Where(row => row.Id == 1).Select(row => row.Name).SingleAsync())
            .Should().Be(portfolioName);
        (await db.Tenants.CountAsync()).Should().Be(tenantRows);
        (await db.InspectionTemplates.CountAsync()).Should().Be(templateRows);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.IdempotencyKey.Contains("frozen-workspace")
            || receipt.IdempotencyKey.Contains("frozen-guided")
            || receipt.IdempotencyKey.Contains("frozen-inspection"))).Should().Be(3);

        var session = await db.AuthSessions.SingleAsync(row => row.Id == SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = Now;
        await db.SaveChangesAsync();
        var denied = () => inspections.CreateTemplateAuthorizedAsync(scope,
            new CreateInspectionTemplateRequest
            {
                Name = "Frozen template", InspectionType = InspectionType.Routine, Items = [],
            }, "frozen-inspection");
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private const string StatusJson = "{\"Configured\":true,\"Provider\":\"openai\",\"ModelId\":\"gpt-4o\",\"LastTestedAtUtc\":\"2099-08-21T12:00:00Z\",\"UpdatedAtUtc\":\"2099-08-21T12:00:00Z\"}";

    private ActiveAccessContext ActiveAccess() => new(
        SessionId, 1, AccessContextId, 1, 1, WorkspaceExperience.Management, 7101,
        WorkspaceExperience.Management);

    private static ActiveAccessContext OwnerAccess() => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), 2, 7102, 1, 1,
        WorkspaceExperience.Owner, 7102, WorkspaceExperience.Leasing);

    private static T Controller<T>(T controller, ActiveAccessContext access)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = access;
        return controller;
    }

    private WorkspaceReadScope Scope() => new(1, 1, SessionId, AccessContextId, 1);

    private async Task SeedReceiptAsync(
        string operation, string key, string fingerprint, string contract, string resultJson)
    {
        _context.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = operation,
            IdempotencyKey = key, RequestFingerprint = fingerprint,
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = contract,
            ResultJson = resultJson, StartedAt = Now, CompletedAt = Now,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task SeedOwnerTeamAndConversationAsync()
    {
        var manager = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var owner = new ApplicationUser
        {
            Id = 2, UserName = "owner@example.test", NormalizedUserName = "OWNER@EXAMPLE.TEST",
            Email = "owner@example.test", NormalizedEmail = "OWNER@EXAMPLE.TEST",
            DisplayName = "Frozen Owner", SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"), CreatedAt = Now,
        };
        var context = new WorkspaceAccessContext
        {
            Id = 7102, User = owner, PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Owner,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var membership = new WorkspaceMembership
        {
            Id = 7102, AccessContext = context, PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Leasing,
            EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var assignment = new MembershipRoleAssignment
        {
            Id = 7102, WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.LeasingAgent).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var ownerEntity = new OwnerEntity
        {
            Id = 7201, PortfolioId = 1, Name = "Frozen Owner",
            Email = "owner@example.test", CreatedAt = Now, UpdatedAt = Now,
        };
        var tenant = new Tenant
        {
            Id = 7402, PortfolioId = 1, FirstName = "Frozen", LastName = "Tenant",
            CreatedAt = Now, UpdatedAt = Now,
        };
        _context.Db.AddRange(
            assignment,
            new AuthSession
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                User = owner, ActiveAccessContext = context, Status = AuthSessionStatus.Active,
                CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddYears(1),
            },
            new OwnerUserAccess
            {
                Id = 7601, PublicId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                PortfolioId = 1, AccessContext = context, ApplicationUser = owner,
                OwnerEntity = ownerEntity,
                EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                GrantedAtUtc = Now, GrantedByUserId = 1, Reason = "Frozen owner access",
            },
            new Notification
            {
                Id = 7301, PortfolioId = 1, UserId = 2, Type = "OwnerApproval",
                Title = "Frozen approval", Message = "Frozen approval", Severity = "Info",
                RelatedEntityType = "OwnerEntity", RelatedEntityId = 7201, CreatedAt = Now,
            },
            new Notification
            {
                Id = 7302, PortfolioId = 1, UserId = 2, Type = "OwnerMessage",
                Title = "Frozen message", Message = "Frozen message", Severity = "Info",
                RelatedEntityType = "OwnerEntity", RelatedEntityId = 7201, CreatedAt = Now,
            },
            tenant,
            new Conversation
            {
                Id = 7401, PortfolioId = 1, Tenant = tenant, Subject = "Frozen conversation",
                StartedByLandlord = true, CreatedAt = Now, LastMessageAt = Now,
            },
            new WorkspaceInvitation
            {
                Id = 7501, PortfolioId = 1, WorkspaceMembership = membership,
                InvitedUser = owner, InvitedByUser = manager,
                TokenHash = "e5e99ba746fbfe24793f49a5804f50334819088bcbf4027a5abc850719a353bf",
                ExpiresAtUtc = Now.AddMonths(1), CreatedAtUtc = Now,
            });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task SeedAuthorityAsync()
    {
        var user = await _context.Db.Users.SingleAsync(row => row.Id == 1);
        var access = new WorkspaceAccessContext
        {
            Id = AccessContextId, User = user, PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var membership = new WorkspaceMembership
        {
            Id = 7101, AccessContext = access, PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var assignment = new MembershipRoleAssignment
        {
            Id = 7101, WorkspaceMembership = membership, PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = Now, UpdatedAtUtc = Now,
        };
        var session = new AuthSession
        {
            Id = SessionId, User = user, ActiveAccessContext = access,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddYears(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private sealed class SuccessfulProbe : ILlmCredentialProbe
    {
        public string ProviderKey => "openai";
        public Task<LlmCredentialProbeResult> TestCredentialAsync(
            string apiKey, string modelId, CancellationToken ct = default) =>
            Task.FromResult(new LlmCredentialProbeResult(true, "openai", modelId));
    }

    private sealed class FrozenDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new FrozenDataProtector();
    }

    private sealed class FrozenDataProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext.Reverse().ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.Reverse().ToArray();
    }

    private sealed class FrozenAnswerProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) =>
            Task.FromResult("Frozen answer");
        public Task<ExtractedFields> ExtractAsync(byte[] documentBytes, string contentType,
            string instructions, IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<LlmToolResult> ChatWithToolsAsync(string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages, IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default) => Task.FromResult(
            new LlmToolResult("end", "Frozen answer", [], 1, 1, "frozen-model"));
    }
}
