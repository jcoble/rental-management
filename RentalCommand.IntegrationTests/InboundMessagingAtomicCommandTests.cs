using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Conversations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Conversations;
using RentalCommand.Data.Operations;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for retry-safe tenant messages and inbound vendor completions.</summary>
public sealed class InboundMessagingAtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<SendConversationMessageResult> ConversationCodec =
        new("conversation-message-result.v1");
    private static readonly AtomicJsonResultCodec<CompleteVendorDispatchFromInboundResult> VendorDoneCodec =
        new("complete-vendor-dispatch-from-inbound-result.v1");

    private readonly CommandProbe _probe = new();
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private SeededFacts _facts = null!;
    private readonly DateTime _now = new(2026, 7, 11, 22, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_inbound_messaging")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        await using (var db = NewContext(includeProbe: false))
        {
            await db.Database.MigrateAsync();
            _facts = await SeedAsync(db);
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_probe);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            SendConversationMessageCommand,
            SendConversationMessageResult,
            SendConversationMessageHandler>();
        services.AddAtomicCommandHandler<
            CompleteVendorDispatchFromInboundCommand,
            CompleteVendorDispatchFromInboundResult,
            CompleteVendorDispatchFromInboundHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .AddInterceptors(provider.GetRequiredService<CommandProbe>())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task TenantStartAndPost_ReplayOneMessageNotificationAuditAndReceipt()
    {
        SkipIfNoDocker();
        _probe.Commands.Clear();
        var startIdentity = new AtomicCommandIdentity("conversation.tenant-start", "tenant:start:stable-1");
        var start = new SendConversationMessageCommand(
            _facts.PortfolioId,
            null,
            _facts.TenantId,
            "Kitchen leak",
            "Water is under the sink.",
            ConversationSenderRole.Tenant,
            [],
            _now);

        var first = await Atomic.ExecuteAsync(startIdentity, start, ConversationCodec);
        var replay = await Atomic.ExecuteAsync(startIdentity, start, ConversationCodec);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.ConversationId.Should().Be(first.Value.ConversationId);

        var postIdentity = new AtomicCommandIdentity("conversation.tenant-post-message", "tenant:post:stable-1");
        var post = start with
        {
            ConversationId = first.Value.ConversationId,
            Subject = string.Empty,
            Body = "It is getting worse.",
            CreatedAtUtc = _now.AddMinutes(2),
        };
        await Atomic.ExecuteAsync(postIdentity, post, ConversationCodec);
        await Atomic.ExecuteAsync(postIdentity, post, ConversationCodec);

        await using var db = NewContext();
        (await db.Conversations.CountAsync(conversation => conversation.Subject == "Kitchen leak"))
            .Should().Be(1);
        (await db.ConversationMessages.CountAsync(message =>
            message.ConversationId == first.Value.ConversationId)).Should().Be(2);
        (await db.Notifications.CountAsync(notification => notification.Type == "TenantMessage"))
            .Should().Be(2);
        (await db.Notifications
            .Where(notification => notification.Type == "TenantMessage")
            .Select(notification => notification.UserId)
            .Distinct()
            .ToListAsync()).Should().Equal((int?)_facts.AuthorizedUserId);
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "conversation.tenant-start"
            || receipt.CommandType == "conversation.tenant-post-message")).Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(log => log.EntityType == nameof(ConversationMessage)))
            .Should().Be(2);
        _probe.Commands.Count(sql => sql.Contains(
            "TSK-668 tenant relationship Team routing with exact property and administrator fallback",
            StringComparison.Ordinal)).Should().Be(2);
    }

    [SkippableFact]
    public async Task SameProviderEventReplaysAndConcurrentDifferentEventsCompleteOnce()
    {
        SkipIfNoDocker();
        _probe.Commands.Clear();
        var command = VendorDone("SM-provider-stable-1");
        var identity = VendorIdentity(command.ProviderEventId);

        var first = await Atomic.ExecuteAsync(identity, command, VendorDoneCodec);
        var replay = await Atomic.ExecuteAsync(identity, command, VendorDoneCodec);
        first.Value.Outcome.Should().Be(CompleteVendorDispatchFromInboundOutcome.Applied);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);

        await using (var db = NewContext())
        {
            await ResetOpenDispatchAsync(db);
        }

        var concurrent = await Task.WhenAll(
            Atomic.ExecuteAsync(
                VendorIdentity("SM-concurrent-a"), VendorDone("SM-concurrent-a", _now.AddMinutes(1)), VendorDoneCodec),
            Atomic.ExecuteAsync(
                VendorIdentity("SM-concurrent-b"), VendorDone("SM-concurrent-b", _now.AddMinutes(1)), VendorDoneCodec));
        concurrent.Count(result => result.Value.Outcome == CompleteVendorDispatchFromInboundOutcome.Applied)
            .Should().Be(1);
        concurrent.Count(result => result.Value.Outcome == CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch)
            .Should().Be(1);

        await using var verify = NewContext();
        (await verify.WorkOrderStatusEvents.CountAsync(status =>
            status.WorkOrderId == _facts.WorkOrderId && status.ToStatus == WorkOrderStatus.Completed))
            .Should().Be(2); // one before reset, one after reset; never one per duplicate event
        (await verify.Vendors.SingleAsync(vendor => vendor.Id == _facts.VendorId)).JobsCompleted.Should().Be(2);
        var notifications = await verify.Notifications
            .Where(row => row.Type == "VendorJobCompleted")
            .OrderBy(row => row.CreatedAt)
            .Select(row => new
            {
                row.CreatedAt,
                row.UserId,
                row.RelatedEntityType,
                row.RelatedEntityId,
                row.NavigationExperience,
                row.NavigationDestination,
                row.NavigationResourceKind,
                row.NavigationResourceId,
                row.NavigationAccessContextId,
                row.NavigationAccessRevision,
            })
            .ToListAsync();
        notifications.Should().HaveCount(2,
            "each distinct completed transition emits once while replay and the losing concurrent event emit nothing");
        notifications.Select(notification => notification.CreatedAt)
            .Should().Equal(_now, _now.AddMinutes(1));
        notifications.Should().OnlyContain(notification =>
            notification.UserId == _facts.AuthorizedUserId
            && notification.RelatedEntityType == nameof(WorkOrder)
            && notification.RelatedEntityId == _facts.WorkOrderId
            && notification.NavigationExperience == NavigationExperience.Management
            && notification.NavigationDestination == NavigationDestination.WorkOrder
            && notification.NavigationResourceKind == nameof(WorkOrder)
            && notification.NavigationResourceId == _facts.WorkOrderId
            && notification.NavigationAccessContextId == _facts.AuthorizedAccessContextId
            && notification.NavigationAccessRevision == _facts.AuthorizedAccessRevision);
        _probe.Commands.Count(sql => sql.Contains(
            "TSK-668 event-time team routing with direct responsibility and visible administrator fallback",
            StringComparison.Ordinal)).Should().Be(2);
    }

    [SkippableFact]
    public async Task SamePortfolioDuplicatePhoneAndMultipleDispatchesFailClosedWithOneBoundedQuery()
    {
        SkipIfNoDocker();
        await using (var db = NewContext())
        {
            var property = await db.Properties.SingleAsync(row => row.Id == _facts.PropertyId);
            var duplicateVendor = NewVendor(_facts.PortfolioId, "Duplicate phone vendor", "+1 614 555 0199");
            db.Vendors.Add(duplicateVendor);
            await db.SaveChangesAsync();
            var duplicateWork = NewWorkOrder(_facts.PortfolioId, property.Id, "Duplicate phone repair");
            db.WorkOrders.Add(duplicateWork);
            await db.SaveChangesAsync();
            db.VendorDispatches.Add(NewDispatch(
                _facts.PortfolioId,
                duplicateWork.Id,
                duplicateVendor.Id,
                _now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }
        _probe.Commands.Clear();

        var identity = VendorIdentity("SM-same-portfolio-ambiguous");
        var result = await Atomic.ExecuteAsync(
            identity,
            VendorDone("SM-same-portfolio-ambiguous"),
            VendorDoneCodec);

        result.Value.Outcome.Should().Be(CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch);
        var matchingQueries = _probe.Commands.Where(sql =>
            sql.Contains("InboundVendorPhoneMatch: bounded top-two", StringComparison.Ordinal)).ToArray();
        matchingQueries.Should().ContainSingle();
        matchingQueries[0].Should().Contain("LIMIT");

        await using var verify = NewContext();
        (await verify.VendorDispatches.CountAsync(row =>
            row.PortfolioId == _facts.PortfolioId
            && row.Status == VendorDispatchStatus.Dispatched)).Should().Be(2);
        (await verify.WorkOrders.CountAsync(row =>
            row.PortfolioId == _facts.PortfolioId
            && row.Status == WorkOrderStatus.Completed)).Should().Be(0);
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task VerifiedNonDoneEventCommitsReceiptAsNoOpWithoutRunningDispatchMatch()
    {
        SkipIfNoDocker();
        var identity = VendorIdentity("SM-not-done");
        _probe.Commands.Clear();

        var result = await Atomic.ExecuteAsync(
            identity,
            new CompleteVendorDispatchFromInboundCommand(
                "SM-not-done", "+16145550199", false, _now),
            VendorDoneCodec);

        result.Value.Outcome.Should().Be(CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch);
        _probe.Commands.Should().NotContain(sql =>
            sql.Contains("InboundVendorPhoneMatch: bounded top-two", StringComparison.Ordinal));
        await using var verify = NewContext();
        (await verify.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == identity.CommandType
            && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await verify.VendorDispatches.SingleAsync(row => row.Id == _facts.DispatchId)).Status
            .Should().Be(VendorDispatchStatus.Dispatched);
        (await verify.WorkOrders.SingleAsync(row => row.Id == _facts.WorkOrderId)).Status
            .Should().Be(WorkOrderStatus.InProgress);
    }

    [SkippableFact]
    public async Task SiblingDispatchAfterCompletedWorkOrderDoesNotInflateVendorJobsCompleted()
    {
        SkipIfNoDocker();
        await Atomic.ExecuteAsync(
            VendorIdentity("SM-first-completion"),
            VendorDone("SM-first-completion"),
            VendorDoneCodec);

        await using (var db = NewContext())
        {
            db.VendorDispatches.Add(NewDispatch(
                _facts.PortfolioId,
                _facts.WorkOrderId,
                _facts.VendorId,
                _now.AddMinutes(1)));
            await db.SaveChangesAsync();
        }

        (await Atomic.ExecuteAsync(
            VendorIdentity("SM-sibling-completion"),
            VendorDone("SM-sibling-completion", _now.AddMinutes(2)),
            VendorDoneCodec)).Value.Outcome.Should().Be(CompleteVendorDispatchFromInboundOutcome.Applied);

        await using var verify = NewContext();
        (await verify.Vendors.SingleAsync(row => row.Id == _facts.VendorId)).JobsCompleted.Should().Be(1);
        (await verify.WorkOrderStatusEvents.CountAsync(row =>
            row.WorkOrderId == _facts.WorkOrderId
            && row.ToStatus == WorkOrderStatus.Completed)).Should().Be(1);
        (await verify.VendorDispatches.CountAsync(row =>
            row.WorkOrderId == _facts.WorkOrderId
            && row.Status == VendorDispatchStatus.Completed)).Should().Be(2);
    }

    [SkippableFact]
    public async Task ConcurrentDifferentPhoneSiblingDispatchesCreditOnlyFirstWorkOrderTransition()
    {
        SkipIfNoDocker();
        int secondVendorId;
        await using (var db = NewContext())
        {
            var secondVendor = NewVendor(
                _facts.PortfolioId, "Second vendor", "+1 (614) 555-0200");
            db.Vendors.Add(secondVendor);
            await db.SaveChangesAsync();
            secondVendorId = secondVendor.Id;
            db.VendorDispatches.Add(NewDispatch(
                _facts.PortfolioId,
                _facts.WorkOrderId,
                secondVendorId,
                _now.AddMinutes(-30)));
            await db.SaveChangesAsync();
        }

        var completions = await Task.WhenAll(
            Atomic.ExecuteAsync(
                VendorIdentity("SM-first-phone"),
                VendorDone("SM-first-phone"),
                VendorDoneCodec),
            Atomic.ExecuteAsync(
                VendorIdentity("SM-second-phone"),
                new CompleteVendorDispatchFromInboundCommand(
                    "SM-second-phone", "+16145550200", true, _now),
                VendorDoneCodec));

        completions.Should().OnlyContain(result =>
            result.Value.Outcome == CompleteVendorDispatchFromInboundOutcome.Applied);
        await using var verify = NewContext();
        (await verify.Vendors
            .Where(row => row.Id == _facts.VendorId || row.Id == secondVendorId)
            .SumAsync(row => row.JobsCompleted)).Should().Be(1);
        (await verify.WorkOrderStatusEvents.CountAsync(row =>
            row.WorkOrderId == _facts.WorkOrderId
            && row.ToStatus == WorkOrderStatus.Completed)).Should().Be(1);
        (await verify.Notifications.CountAsync(row =>
            row.Type == "VendorJobCompleted"
            && row.RelatedEntityId == _facts.WorkOrderId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task FinalCompanionFailureRollsBackBusinessRowsAndReceipt_ThenRetrySucceeds()
    {
        SkipIfNoDocker();
        var command = VendorDone("SM-rollback-retry");
        var identity = VendorIdentity(command.ProviderEventId);
        _probe.FailOnAtomicAuditInsert = true;

        var attempt = async () => await Atomic.ExecuteAsync(identity, command, VendorDoneCodec);
        var failure = await attempt.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InjectedCompanionFailure>();
        _probe.FailOnAtomicAuditInsert = false;

        await using (var rolledBack = NewContext())
        {
            (await rolledBack.VendorDispatches.SingleAsync(row => row.Id == _facts.DispatchId))
                .Status.Should().Be(VendorDispatchStatus.Dispatched);
            (await rolledBack.WorkOrders.SingleAsync(row => row.Id == _facts.WorkOrderId))
                .Status.Should().Be(WorkOrderStatus.InProgress);
            (await rolledBack.AtomicCommandReceipts.AnyAsync(receipt =>
                receipt.CommandType == identity.CommandType
                && receipt.IdempotencyKey == identity.IdempotencyKey)).Should().BeFalse();
        }

        (await Atomic.ExecuteAsync(identity, command, VendorDoneCodec)).Value.Outcome
            .Should().Be(CompleteVendorDispatchFromInboundOutcome.Applied);
    }

    [SkippableFact]
    public async Task OpenDispatchMatchIsOneDbQueryAndCrossPortfolioDecoysRemainUntouched()
    {
        SkipIfNoDocker();
        await using (var db = NewContext())
        {
            await SeedCrossPortfolioDecoyAsync(db);
        }
        _probe.Commands.Clear();

        var result = await Atomic.ExecuteAsync(
            VendorIdentity("SM-query-count"),
            VendorDone("SM-query-count"),
            VendorDoneCodec);
        result.Value.Outcome.Should().Be(CompleteVendorDispatchFromInboundOutcome.NoOpenDispatch);

        var matchingQueries = _probe.Commands.Where(sql =>
            sql.Contains("InboundVendorPhoneMatch: bounded top-two", StringComparison.Ordinal))
            .ToArray();
        matchingQueries.Should().ContainSingle();
        matchingQueries[0].Should().Contain("ORDER BY").And.Contain("LIMIT");

        await using var verify = NewContext();
        (await verify.VendorDispatches.SingleAsync(row => row.Id == _facts.DispatchId)).Status
            .Should().Be(VendorDispatchStatus.Dispatched);
        var decoy = await verify.VendorDispatches
            .Where(row => row.PortfolioId != _facts.PortfolioId)
            .SingleAsync();
        decoy.Status.Should().Be(VendorDispatchStatus.Dispatched);
        (await verify.WorkOrders.SingleAsync(row => row.Id == decoy.WorkOrderId)).Status
            .Should().Be(WorkOrderStatus.InProgress);
    }

    [SkippableFact]
    public async Task TenantPostCannotCrossPortfolioOrTenantBoundary()
    {
        SkipIfNoDocker();
        var command = new SendConversationMessageCommand(
            _facts.OtherPortfolioId,
            _facts.ConversationId,
            _facts.OtherTenantId,
            string.Empty,
            "Cross-scope attempt",
            ConversationSenderRole.Tenant,
            [],
            _now);

        var result = await Atomic.ExecuteAsync(
            new AtomicCommandIdentity("conversation.tenant-post-message", "tenant:cross-scope"),
            command,
            ConversationCodec);
        result.Value.Outcome.Should().Be(SendConversationMessageOutcome.NotFound);

        await using var verify = NewContext();
        (await verify.ConversationMessages.CountAsync(message =>
            message.ConversationId == _facts.ConversationId)).Should().Be(1);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();

    private CompleteVendorDispatchFromInboundCommand VendorDone(string eventId, DateTime? receivedAt = null) =>
        new(eventId, "+16145550199", true, receivedAt ?? _now);

    private static AtomicCommandIdentity VendorIdentity(string eventId) =>
        new("sms.vendor-done", $"provider-event:{eventId}");

    private RentalCommandDbContext NewContext(bool includeProbe = true)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString());
        if (includeProbe) options.AddInterceptors(_probe);
        return new RentalCommandDbContext(options.Options);
    }

    private async Task<SeededFacts> SeedAsync(RentalCommandDbContext db)
    {
        var firstPortfolio = new Portfolio
        {
            Name = "Primary",
            ManagementCompanyName = "Primary",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var otherPortfolio = new Portfolio
        {
            Name = "Other",
            ManagementCompanyName = "Other",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Portfolios.AddRange(firstPortfolio, otherPortfolio);
        await db.SaveChangesAsync();

        var tenant = new Tenant
        {
            PortfolioId = firstPortfolio.Id,
            FirstName = "Emily",
            LastName = "Chen",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var otherTenant = new Tenant
        {
            PortfolioId = otherPortfolio.Id,
            FirstName = "Decoy",
            LastName = "Tenant",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Tenants.AddRange(tenant, otherTenant);
        await db.SaveChangesAsync();

        var admin = new ApplicationUser
        {
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            DisplayName = "Admin",
            CreatedAt = _now,
        };
        var decoyUser = new ApplicationUser
        {
            UserName = "decoy-admin@example.test",
            NormalizedUserName = "DECOY-ADMIN@EXAMPLE.TEST",
            Email = "decoy-admin@example.test",
            NormalizedEmail = "DECOY-ADMIN@EXAMPLE.TEST",
            DisplayName = "Unrelated property legacy admin",
            CreatedAt = _now,
        };
        db.Users.AddRange(admin, decoyUser);
        await db.SaveChangesAsync();
        var property = NewProperty(firstPortfolio.Id, "Primary property");
        var samePortfolioDecoyProperty = NewProperty(firstPortfolio.Id, "Unrelated same-portfolio property");
        var vendor = NewVendor(firstPortfolio.Id, "Primary vendor", "+1 (614) 555-0199");
        db.Properties.AddRange(property, samePortfolioDecoyProperty);
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();

        var unit = new Unit
        {
            PortfolioId = firstPortfolio.Id,
            PropertyId = property.Id,
            UnitNumber = "1A",
            Bedrooms = 1,
            Bathrooms = 1,
            MarketRent = 1000,
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = firstPortfolio.Id,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = "ACTIVE-PRIMARY",
            CreatedAtUtc = _now,
            CreatedByUserId = admin.Id,
            UpdatedAtUtc = _now,
            RowVersion = Guid.NewGuid(),
        };
        var primaryParty = new LeaseManagementParty
        {
            PortfolioId = firstPortfolio.Id,
            LeaseManagement = relationship,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(_now.AddMonths(-1)),
            ChangeReason = "Inbound messaging test fixture",
            CreatedAtUtc = _now,
            CreatedByUserId = admin.Id,
        };
        db.LeaseManagements.Add(relationship);
        db.LeaseManagementParties.Add(primaryParty);
        await db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = firstPortfolio.Id,
            LeaseManagementId = relationship.Id,
            AccountNumber = "TA-INBOUND-MESSAGING",
            Currency = "USD",
            OpenedAtUtc = _now.AddMonths(-1),
            CreatedAtUtc = _now.AddMonths(-1),
            CreatedByUserId = admin.Id,
        };
        var sourceVersion = new LegalDocumentSourceVersion
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = firstPortfolio.Id,
            SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
            BusinessKey = "inbound-messaging-agreement-source",
            RendererKey = "integration-inbound-messaging",
            RendererVersion = 1,
            SnapshotPayload = "{}",
            CreatedAtUtc = _now.AddMonths(-1),
            CreatedByUserId = admin.Id,
        };
        db.AddRange(account, sourceVersion);
        await db.SaveChangesAsync();

        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = firstPortfolio.Id,
            LeaseManagementId = relationship.Id,
            VersionNumber = 1,
            AgreementNumber = "AGR-INBOUND-MESSAGING",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(_now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(_now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(_now.AddMonths(-1)),
            BaseRentAmount = 1_000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1_000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersionId = sourceVersion.Id,
            CreatedAtUtc = _now.AddMonths(-1),
            UpdatedAtUtc = _now.AddMonths(-1),
            CreatedByUserId = admin.Id,
        };
        db.LeaseAgreements.Add(agreement);
        await db.SaveChangesAsync();
        db.LeaseAgreementSigners.Add(new LeaseAgreementSigner
        {
            PortfolioId = firstPortfolio.Id,
            LeaseAgreementId = agreement.Id,
            LeaseManagementPartyId = primaryParty.Id,
            TenantId = tenant.Id,
            SignerRole = LeaseLegalSignerRole.PrimaryTenant,
            NameSnapshot = "Emily Chen",
            EmailSnapshot = "emily.chen@example.test",
            SigningOrder = 1,
        });
        await db.SaveChangesAsync();

        var issuedFile = NewStoredFile(firstPortfolio.Id, "inbound-issued.pdf");
        var executedFile = NewStoredFile(firstPortfolio.Id, "inbound-executed.pdf");
        db.StoredFiles.AddRange(issuedFile, executedFile);
        await db.SaveChangesAsync();
        var issuedArtifact = NewArtifact(
            firstPortfolio.Id, admin.Id, issuedFile.Id,
            LegalDocumentArtifactKind.IssuedAgreement, "inbound-issued", 'a');
        var executedArtifact = NewArtifact(
            firstPortfolio.Id, admin.Id, executedFile.Id,
            LegalDocumentArtifactKind.ExecutedAgreement, "inbound-executed", 'b');
        db.LegalDocumentArtifacts.AddRange(issuedArtifact, executedArtifact);
        await db.SaveChangesAsync();
        agreement.IssuedArtifactId = issuedArtifact.Id;
        agreement.IssuedAtUtc = _now.AddDays(-10);
        agreement.ExecutedArtifactId = executedArtifact.Id;
        agreement.FullyExecutedAtUtc = _now.AddDays(-9);
        agreement.UpdatedAtUtc = _now.AddDays(-9);
        relationship.PossessionGivenAtUtc = _now.AddDays(-8);
        relationship.UpdatedAtUtc = _now.AddDays(-8);
        await db.SaveChangesAsync();

        var authorizedContext = NewAccessContext(admin.Id, firstPortfolio.Id);
        var decoyContext = NewAccessContext(decoyUser.Id, firstPortfolio.Id);
        db.WorkspaceAccessContexts.AddRange(authorizedContext, decoyContext);
        await db.SaveChangesAsync();
        var authorizedMembership = NewMembership(authorizedContext.Id, firstPortfolio.Id);
        var decoyMembership = NewMembership(decoyContext.Id, firstPortfolio.Id);
        db.WorkspaceMemberships.AddRange(authorizedMembership, decoyMembership);
        await db.SaveChangesAsync();
        var authorizedAssignment = NewAssignment(
            authorizedMembership.Id,
            firstPortfolio.Id,
            1,
            MembershipRoleAssignmentScopeKind.AllProperties);
        var decoyAssignment = NewAssignment(
            decoyMembership.Id,
            firstPortfolio.Id,
            2,
            MembershipRoleAssignmentScopeKind.SelectedProperties);
        db.MembershipRoleAssignments.AddRange(authorizedAssignment, decoyAssignment);
        await db.SaveChangesAsync();
        db.MembershipRoleAssignmentProperties.Add(new MembershipRoleAssignmentProperty
        {
            MembershipRoleAssignmentId = decoyAssignment.Id,
            PropertyId = samePortfolioDecoyProperty.Id,
            PortfolioId = firstPortfolio.Id,
        });
        db.TeamRoutingRules.AddRange(
            NewRoutingRule(
                firstPortfolio.Id,
                TeamRoutingTopic.ApplicationsAndLeasing,
                admin.Id,
                decoyUser.Id),
            NewRoutingRule(
                firstPortfolio.Id,
                TeamRoutingTopic.WorkOrders,
                admin.Id,
                decoyUser.Id));

        var workOrder = NewWorkOrder(firstPortfolio.Id, property.Id, "Primary repair");
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        var dispatch = NewDispatch(firstPortfolio.Id, workOrder.Id, vendor.Id, _now.AddHours(-1));
        var conversation = new Conversation
        {
            PortfolioId = firstPortfolio.Id,
            TenantId = tenant.Id,
            Subject = "Existing thread",
            StartedByLandlord = true,
            CreatedAt = _now.AddHours(-1),
            LastMessageAt = _now.AddHours(-1),
            LastMessagePreview = "Existing",
            Messages =
            [
                new ConversationMessage
                {
                    SenderRole = ConversationSenderRole.Landlord,
                    Body = "Existing",
                    Channels = "Portal",
                    CreatedAt = _now.AddHours(-1),
                },
            ],
        };
        db.VendorDispatches.Add(dispatch);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return new SeededFacts(
            firstPortfolio.Id,
            otherPortfolio.Id,
            tenant.Id,
            otherTenant.Id,
            vendor.Id,
            property.Id,
            workOrder.Id,
            dispatch.Id,
            conversation.Id,
            admin.Id,
            decoyUser.Id,
            authorizedContext.Id,
            authorizedContext.AccessRevision);
    }

    private TeamRoutingRule NewRoutingRule(
        int portfolioId,
        TeamRoutingTopic topic,
        int authorizedUserId,
        int decoyUserId) => new()
    {
        PortfolioId = portfolioId,
        Topic = topic,
        UseWorkspaceAdministratorFallback = true,
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
        Recipients =
        [
            new TeamRoutingRuleRecipient
            {
                PortfolioId = portfolioId,
                UserId = authorizedUserId,
                Reason = "Authorized event recipient",
            },
            new TeamRoutingRuleRecipient
            {
                PortfolioId = portfolioId,
                UserId = decoyUserId,
                Reason = "Out-of-scope recipient",
            },
        ],
    };

    private WorkspaceAccessContext NewAccessContext(int userId, int portfolioId) => new()
    {
        UserId = userId,
        PortfolioId = portfolioId,
        Status = WorkspaceAccessContextStatus.Active,
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private WorkspaceMembership NewMembership(int accessContextId, int portfolioId) => new()
    {
        AccessContextId = accessContextId,
        PortfolioId = portfolioId,
        Status = WorkspaceMembershipStatus.Active,
        DefaultExperience = WorkspaceExperience.Management,
        EffectiveFromUtc = _now.AddDays(-1),
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private MembershipRoleAssignment NewAssignment(
        int membershipId,
        int portfolioId,
        int roleProfileId,
        MembershipRoleAssignmentScopeKind scopeKind) => new()
    {
        WorkspaceMembershipId = membershipId,
        PortfolioId = portfolioId,
        RoleProfileId = roleProfileId,
        Status = MembershipRoleAssignmentStatus.Active,
        ScopeKind = scopeKind,
        EffectiveFromUtc = _now.AddDays(-1),
        CreatedAtUtc = _now,
        UpdatedAtUtc = _now,
    };

    private StoredFile NewStoredFile(int portfolioId, string name) => new()
    {
        PortfolioId = portfolioId,
        FileName = name,
        FilePath = $"tests/{name}",
        ContentType = "application/pdf",
        FileSize = 100,
        UploadedAt = _now.AddDays(-10),
    };

    private LegalDocumentArtifact NewArtifact(
        int portfolioId,
        int userId,
        int storedFileId,
        LegalDocumentArtifactKind kind,
        string key,
        char hashCharacter) => new()
    {
        PublicId = Guid.NewGuid(),
        PortfolioId = portfolioId,
        StoredFileId = storedFileId,
        ArtifactKind = kind,
        StorageKey = $"tests/{key}",
        FileName = $"{key}.pdf",
        ContentType = "application/pdf",
        ByteLength = 100,
        ContentSha256 = new string(hashCharacter, 64),
        LegalIssuanceFingerprint = kind == LegalDocumentArtifactKind.IssuedAgreement
            ? new string('c', 64)
            : null,
        CreatedAtUtc = _now.AddDays(-10),
        CreatedByUserId = userId,
    };

    private async Task ResetOpenDispatchAsync(RentalCommandDbContext db)
    {
        var dispatch = await db.VendorDispatches.SingleAsync(row => row.Id == _facts.DispatchId);
        dispatch.Status = VendorDispatchStatus.Dispatched;
        dispatch.RespondedAtUtc = null;
        var workOrder = await db.WorkOrders.SingleAsync(row => row.Id == _facts.WorkOrderId);
        workOrder.Status = WorkOrderStatus.InProgress;
        workOrder.CompletedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task SeedCrossPortfolioDecoyAsync(RentalCommandDbContext db)
    {
        var property = NewProperty(_facts.OtherPortfolioId, "Decoy property");
        var vendor = NewVendor(_facts.OtherPortfolioId, "Decoy vendor", "+1 (614) 555-0199");
        db.Properties.Add(property);
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        var workOrder = NewWorkOrder(_facts.OtherPortfolioId, property.Id, "Decoy repair");
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();
        db.VendorDispatches.Add(NewDispatch(
            _facts.OtherPortfolioId, workOrder.Id, vendor.Id, _now.AddMinutes(-1)));
        await db.SaveChangesAsync();
    }

    private Property NewProperty(int portfolioId, string name) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = "1 Main St",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private Vendor NewVendor(int portfolioId, string name, string phone) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        ServiceType = "Plumbing",
        Phone = phone,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private WorkOrder NewWorkOrder(int portfolioId, int propertyId, string title) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Title = title,
        Description = title,
        Status = WorkOrderStatus.InProgress,
        Priority = WorkOrderPriority.Normal,
        RequestedAt = _now.AddDays(-1),
        UpdatedAt = _now,
    };

    private VendorDispatch NewDispatch(
        int portfolioId,
        int workOrderId,
        int vendorId,
        DateTime dispatchedAt) => new()
    {
        PortfolioId = portfolioId,
        WorkOrderId = workOrderId,
        VendorId = vendorId,
        Status = VendorDispatchStatus.Dispatched,
        DispatchedAtUtc = dispatchedAt,
        Message = "Reply DONE when complete.",
    };

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is required for PostgreSQL inbound-messaging tests.");

    private sealed record SeededFacts(
        int PortfolioId,
        int OtherPortfolioId,
        int TenantId,
        int OtherTenantId,
        int VendorId,
        int PropertyId,
        int WorkOrderId,
        int DispatchId,
        int ConversationId,
        int AuthorizedUserId,
        int DecoyUserId,
        int AuthorizedAccessContextId,
        long AuthorizedAccessRevision);

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string ActorLabel => "inbound-messaging-test";
        public string? IpAddress => null;
    }

    private sealed class InjectedCompanionFailure : Exception
    {
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();
        public bool FailOnAtomicAuditInsert { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Inspect(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Inspect(string sql)
        {
            Commands.Enqueue(sql);
            if (FailOnAtomicAuditInsert
                && sql.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InjectedCompanionFailure();
            }
        }
    }
}
