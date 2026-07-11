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
            await db.Database.EnsureCreatedAsync();
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
            OccurredAtUtc = _now.AddMinutes(2),
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
        (await db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "conversation.tenant-start"
            || receipt.CommandType == "conversation.tenant-post-message")).Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(log => log.EntityType == nameof(ConversationMessage)))
            .Should().Be(2);
    }

    [SkippableFact]
    public async Task SameProviderEventReplaysAndConcurrentDifferentEventsCompleteOnce()
    {
        SkipIfNoDocker();
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
            sql.Contains("FROM \"VendorDispatches\"", StringComparison.Ordinal)
            && sql.Contains("\"NormalizedPhone\"", StringComparison.Ordinal))
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
        new(eventId, "+16145550199", receivedAt ?? _now);

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

        var adminRole = new IdentityRole<int>(nameof(UserRole.Admin))
        {
            NormalizedName = nameof(UserRole.Admin).ToUpperInvariant(),
        };
        db.Roles.Add(adminRole);
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
            PortfolioId = firstPortfolio.Id,
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            DisplayName = "Admin",
            CreatedAt = _now,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        db.UserRoles.Add(new IdentityUserRole<int> { UserId = admin.Id, RoleId = adminRole.Id });

        var property = NewProperty(firstPortfolio.Id, "Primary property");
        var vendor = NewVendor(firstPortfolio.Id, "Primary vendor", "+1 (614) 555-0199");
        db.Properties.Add(property);
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();

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
            workOrder.Id,
            dispatch.Id,
            conversation.Id);
    }

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
        int WorkOrderId,
        int DispatchId,
        int ConversationId);

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
