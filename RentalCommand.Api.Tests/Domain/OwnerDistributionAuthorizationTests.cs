using System.Reflection;
using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Time;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class OwnerDistributionAuthorizationTests : IAsyncLifetime
{
    private static int _nextPortfolioId = 900_000;
    private static readonly DateTime FrozenBusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private readonly int _portfolioId = Interlocked.Increment(ref _nextPortfolioId);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _atomicServices = null!;
    private OwnerDistributionService _service = null!;

    public OwnerDistributionAuthorizationTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _atomicServices = AtomicDomainTestKernel.CreateForMoneyPostgreSql(_ctx.ConnectionString);
        _service = new OwnerDistributionService(
            _ctx.Db,
            TimeProvider.System,
            _atomicServices.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
        SeedPortfolio();
    }

    public async Task DisposeAsync()
    {
        await _atomicServices.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    [Fact]
    public async Task ListPageCanFilterPropertyDistributionsDbSide()
    {
        var owner = SeedOwner("Assigned Owner");
        var assignedProperty = SeedProperty(owner.Id, "Assigned Property");
        var otherProperty = SeedProperty(owner.Id, "Other Property");
        var assigned = SeedDistribution(owner.Id, assignedProperty.Id, 100m);
        var other = SeedDistribution(owner.Id, otherProperty.Id, 200m);
        var page = await _service.ListPageAsync(_portfolioId, new OwnerDistributionListQuery
        {
            PropertyId = assignedProperty.Id,
        });
        var assignedDetail = await _service.GetAsync(_portfolioId, assigned.Id);

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.Id == assigned.Id);
        assignedDetail.Should().NotBeNull();
        page.Items.Should().NotContain(item => item.Id == other.Id);
    }

    [Fact]
    public async Task PropertyManagerDirectServiceAttemptsCannotMutateOwnerDistributions()
    {
        var owner = SeedOwner("Managed Owner");
        var property = SeedProperty(owner.Id, "Managed Property");
        var distribution = SeedDistribution(owner.Id, property.Id, 100m, OwnerDistributionStatus.Draft);
        var scope = _ctx.Db.SeedPropertyManagerScope(
            _portfolioId, property.Id, nameof(PropertyManagerDirectServiceAttemptsCannotMutateOwnerDistributions));

        var create = async () => await _service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = DateTime.UtcNow,
            Amount = 50m,
        }, $"pm-create-{Guid.NewGuid():N}");
        var update = async () => await _service.UpdateAsync(scope, distribution.Id,
            new UpdateOwnerDistributionRequest { Amount = 125m }, $"pm-update-{Guid.NewGuid():N}");
        var approve = async () => await _service.ApproveAsync(scope, distribution.Id,
            new ApproveOwnerDistributionRequest { BankReference = "DIST-PM", ExportReference = "DIST-PM" },
            $"pm-approve-{Guid.NewGuid():N}");
        var reject = async () => await _service.RejectAsync(scope, distribution.Id,
            new RejectOwnerDistributionRequest { Reason = "not allowed" }, $"pm-reject-{Guid.NewGuid():N}");
        var delete = async () => await _service.DeleteAsync(
            scope, distribution.Id, $"pm-delete-{Guid.NewGuid():N}");

        await create.Should().ThrowAsync<UnauthorizedAccessException>();
        await update.Should().ThrowAsync<UnauthorizedAccessException>();
        await approve.Should().ThrowAsync<UnauthorizedAccessException>();
        await reject.Should().ThrowAsync<UnauthorizedAccessException>();
        await delete.Should().ThrowAsync<UnauthorizedAccessException>();
        _ctx.Db.ChangeTracker.Clear();
        var persisted = _ctx.Db.OwnerDistributions.IgnoreQueryFilters()
            .Single(item => item.Id == distribution.Id);
        persisted.Amount.Should().Be(100m);
        persisted.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task AdministratorWithDisbursementAndDestructiveAuthorityCanDeleteDistribution()
    {
        var owner = SeedOwner("Administrator Owner");
        var property = SeedProperty(owner.Id, "Administrator Property");
        var distribution = SeedDistribution(owner.Id, property.Id, 100m, OwnerDistributionStatus.Draft);
        var scope = _ctx.Db.SeedAdministratorScope(
            _portfolioId, nameof(AdministratorWithDisbursementAndDestructiveAuthorityCanDeleteDistribution));

        var deleted = await _service.DeleteAsync(
            scope, distribution.Id, $"admin-delete-{Guid.NewGuid():N}");

        deleted.Should().BeTrue();
        _ctx.Db.ChangeTracker.Clear();
        _ctx.Db.OwnerDistributions.IgnoreQueryFilters()
            .Single(item => item.Id == distribution.Id)
            .DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public void EndpointsKeepPropertyScopedReadsInSqlAndDeclareMutationPolicies()
    {
        Policies(nameof(OwnerDistributionController.List)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.ListPage)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.Get)).Should().BeEmpty();
        Policies(nameof(OwnerDistributionController.Create))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Update))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Approve))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Reject))
            .Should().Equal(CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage);
        Policies(nameof(OwnerDistributionController.Delete)).Should().BeEquivalentTo(
            CapabilityPolicy.Prefix + CapabilityKeys.MoneyDisbursementsManage,
            CapabilityPolicy.Prefix + CapabilityKeys.MoneyReconciliationDestructive);
    }

    [Fact]
    public void ControllerRequiresTheActiveManagementExperienceForEveryAction()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RentalCommand.Api", "Controllers", "OwnerDistributionController.cs"));

        source.Should().NotContain("GetWorkspaceReadScope()");
        source.Split("TryReadManagementScope(out var scope)")
            .Should().HaveCount(9, "all eight owner-distribution actions must fail closed outside Management");
    }

    [Fact]
    public async Task FrozenClockLifecycleTimestampsCoverDraftUpdateApproveRejectReplayAuditsAndOutbox()
    {
        SeedFrozenBusinessDate();
        var clock = new FixedTimeProvider(new DateTimeOffset(FrozenBusinessNowUtc));
        await using var frozenServices = AtomicDomainTestKernel.CreateForMoneyPostgreSql(
            _ctx.ConnectionString,
            timeProvider: clock);
        var service = new OwnerDistributionService(
            _ctx.Db,
            clock,
            frozenServices.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
        var owner = SeedOwner("Frozen Lifecycle Owner");
        var property = SeedProperty(owner.Id, "Frozen Lifecycle Property");
        var scope = _ctx.Db.SeedAdministratorScope(
            _portfolioId, nameof(FrozenClockLifecycleTimestampsCoverDraftUpdateApproveRejectReplayAuditsAndOutbox));

        var draft = await service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = FrozenBusinessNowUtc,
            Amount = 3050m,
            Method = DistributionMethod.Ach,
            Memo = "Blue Door owner distribution",
        }, "ys175-draft-frozen");

        draft.Should().NotBeNull();
        draft!.Status.Should().Be(OwnerDistributionStatus.Draft);
        draft.CreatedAt.Should().Be(FrozenBusinessNowUtc);
        draft.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        (await service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(0m);

        var updated = await service.UpdateAsync(scope, draft.Id, new UpdateOwnerDistributionRequest
        {
            Amount = 3500m,
            Memo = "Blue Door owner distribution updated",
        }, "ys175-update-frozen");

        updated.Should().NotBeNull();
        updated!.CreatedAt.Should().Be(FrozenBusinessNowUtc);
        updated.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        updated.Amount.Should().Be(3500m);

        var approved = await service.ApproveAsync(scope, draft.Id, new ApproveOwnerDistributionRequest
        {
            BankReference = "DIST-YS175-BANK",
            ExportReference = "DIST-YS175-EXPORT",
        }, "ys175-approve-frozen");
        var replay = await service.ApproveAsync(scope, draft.Id, new ApproveOwnerDistributionRequest
        {
            BankReference = "DIST-YS175-BANK",
            ExportReference = "DIST-YS175-EXPORT",
        }, "ys175-approve-frozen");

        approved.Should().NotBeNull();
        approved!.Status.Should().Be(OwnerDistributionStatus.Approved);
        approved.CreatedAt.Should().Be(FrozenBusinessNowUtc);
        approved.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        approved.ApprovedAt.Should().Be(FrozenBusinessNowUtc);
        approved.ApprovedBusinessDate.Should().Be(FrozenBusinessNowUtc.Date);
        approved.ExportedAt.Should().Be(FrozenBusinessNowUtc);
        approved.BankReference.Should().Be("DIST-YS175-BANK");
        approved.ExportReference.Should().Be("DIST-YS175-EXPORT");
        replay!.Id.Should().Be(approved.Id);
        replay.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        (await service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(3500m);

        var rejectDraft = await service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = FrozenBusinessNowUtc,
            Amount = 100.01m,
            Method = DistributionMethod.Ach,
            Memo = "Blue Door rejected draft",
        }, "ys175-draft-reject-frozen");
        var rejected = await service.RejectAsync(scope, rejectDraft!.Id,
            new RejectOwnerDistributionRequest { Reason = "verified admin rejected the draft" },
            "ys175-reject-frozen");

        rejected.Should().NotBeNull();
        rejected!.Status.Should().Be(OwnerDistributionStatus.Rejected);
        rejected.CreatedAt.Should().Be(FrozenBusinessNowUtc);
        rejected.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        rejected.RejectedAt.Should().Be(FrozenBusinessNowUtc);
        rejected.RejectedByUserId.Should().Be(scope.UserId);
        (await service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(3500m);

        _ctx.Db.ChangeTracker.Clear();
        var lifecycleRows = await _ctx.Db.OwnerDistributions.AsNoTracking()
            .Where(distribution => distribution.Id == approved.Id || distribution.Id == rejected.Id)
            .OrderBy(distribution => distribution.Id)
            .Select(distribution => new
            {
                distribution.Id,
                distribution.Status,
                distribution.CreatedAt,
                distribution.UpdatedAt,
                distribution.ApprovedAt,
                distribution.RejectedAt,
                distribution.ExportedAt,
            })
            .ToListAsync();
        lifecycleRows.Should().HaveCount(2);
        lifecycleRows.Should().OnlyContain(row =>
            row.CreatedAt == FrozenBusinessNowUtc && row.UpdatedAt == FrozenBusinessNowUtc);
        lifecycleRows.Single(row => row.Id == approved.Id).ApprovedAt.Should().Be(FrozenBusinessNowUtc);
        lifecycleRows.Single(row => row.Id == approved.Id).ExportedAt.Should().Be(FrozenBusinessNowUtc);
        lifecycleRows.Single(row => row.Id == rejected.Id).RejectedAt.Should().Be(FrozenBusinessNowUtc);

        var auditRows = await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .Where(audit => audit.PortfolioId == _portfolioId &&
                audit.EntityType == nameof(OwnerDistribution) &&
                (audit.EntityId == approved.Id || audit.EntityId == rejected.Id))
            .Select(audit => new { audit.EntityId, audit.Operation, audit.Timestamp })
            .ToListAsync();
        auditRows.Should().HaveCount(5);
        auditRows.Should().OnlyContain(row => row.Timestamp == FrozenBusinessNowUtc);

        var outboxRows = await _ctx.Db.OutboxMessages.AsNoTracking()
            .Where(outbox => outbox.PortfolioId == _portfolioId &&
                outbox.MessageType == "data-update" &&
                (outbox.IdempotencyKey.Contains("ys175-draft-frozen") ||
                 outbox.IdempotencyKey.Contains("ys175-update-frozen") ||
                 outbox.IdempotencyKey.Contains("ys175-approve-frozen") ||
                 outbox.IdempotencyKey.Contains("ys175-draft-reject-frozen") ||
                 outbox.IdempotencyKey.Contains("ys175-reject-frozen")))
            .Select(outbox => new { outbox.CreatedAtUtc, outbox.NextAttemptAtUtc })
            .ToListAsync();
        outboxRows.Should().HaveCount(5);
        outboxRows.Should().OnlyContain(row =>
            row.CreatedAtUtc == FrozenBusinessNowUtc && row.NextAttemptAtUtc == FrozenBusinessNowUtc);
    }

    [Fact]
    public async Task DraftApproveAndRejectLifecycleControlsCashEffectAndReplay()
    {
        var owner = SeedOwner("Lifecycle Owner");
        var property = SeedProperty(owner.Id, "Lifecycle Property");
        var scope = _ctx.Db.SeedAdministratorScope(
            _portfolioId, nameof(DraftApproveAndRejectLifecycleControlsCashEffectAndReplay));

        var draft = await _service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = new DateTime(2027, 1, 25, 0, 0, 0, DateTimeKind.Utc),
            Amount = 3050m,
            Method = DistributionMethod.Ach,
            Memo = "Blue Door owner distribution",
        }, "draft-blue-door");

        draft.Should().NotBeNull();
        draft!.Status.Should().Be(OwnerDistributionStatus.Draft);
        (await _service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(0m);

        var approved = await _service.ApproveAsync(scope, draft.Id, new ApproveOwnerDistributionRequest
        {
            BankReference = "DIST-202701-O01",
            ExportReference = "DIST-202701-O01",
        }, "approve-blue-door");
        var replay = await _service.ApproveAsync(scope, draft.Id, new ApproveOwnerDistributionRequest
        {
            BankReference = "DIST-202701-O01",
            ExportReference = "DIST-202701-O01",
        }, "approve-blue-door");

        approved!.Status.Should().Be(OwnerDistributionStatus.Approved);
        approved.BankReference.Should().Be("DIST-202701-O01");
        approved.ExportReference.Should().Be("DIST-202701-O01");
        approved.ApprovedByUserId.Should().Be(scope.UserId);
        approved.ApprovedAt.Should().NotBeNull();
        approved.ApprovedBusinessDate.Should().NotBeNull();
        replay!.Id.Should().Be(approved.Id);
        (await _service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(3050m);

        var duplicateTransition = async () => await _service.ApproveAsync(scope, draft.Id,
            new ApproveOwnerDistributionRequest { BankReference = "DIST-OTHER", ExportReference = "DIST-OTHER" },
            "approve-blue-door-second-key");
        await duplicateTransition.Should().ThrowAsync<DomainValidationException>();

        var rejectDraft = await _service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = new DateTime(2027, 1, 25, 0, 0, 0, DateTimeKind.Utc),
            Amount = 999m,
            Method = DistributionMethod.Ach,
        }, "draft-reject-blue-door");
        var rejected = await _service.RejectAsync(scope, rejectDraft!.Id,
            new RejectOwnerDistributionRequest { Reason = "quarterly negative path" },
            "reject-blue-door");

        rejected!.Status.Should().Be(OwnerDistributionStatus.Rejected);
        (await _service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(3050m);
        var staleApprove = async () => await _service.ApproveAsync(scope, rejectDraft.Id,
            new ApproveOwnerDistributionRequest { BankReference = "DIST-REJECTED", ExportReference = "DIST-REJECTED" },
            "approve-rejected-blue-door");
        await staleApprove.Should().ThrowAsync<DomainValidationException>();
    }

    [Fact]
    public async Task ApprovalRollsBackWhenAtomicOutboxStagingFails()
    {
        SeedFrozenBusinessDate();
        var clock = new FixedTimeProvider(new DateTimeOffset(FrozenBusinessNowUtc));
        await using var frozenServices = AtomicDomainTestKernel.CreateForMoneyPostgreSql(
            _ctx.ConnectionString,
            timeProvider: clock);
        var service = new OwnerDistributionService(
            _ctx.Db,
            clock,
            frozenServices.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());
        var owner = SeedOwner("Rollback Owner");
        var property = SeedProperty(owner.Id, "Rollback Property");
        var scope = _ctx.Db.SeedAdministratorScope(
            _portfolioId, nameof(ApprovalRollsBackWhenAtomicOutboxStagingFails));
        var draft = await service.CreateAsync(scope, new CreateOwnerDistributionRequest
        {
            OwnerEntityId = owner.Id,
            PropertyId = property.Id,
            Date = new DateTime(2027, 1, 25, 0, 0, 0, DateTimeKind.Utc),
            Amount = 3400m,
            Method = DistributionMethod.Ach,
        }, "draft-rollback-coble");

        await using var failingServices = AtomicDomainTestKernel.CreateForMoneyPostgreSql(
            _ctx.ConnectionString,
            [new ThrowOnOwnerDistributionOutboxInterceptor()],
            clock);
        var failingService = new OwnerDistributionService(
            _ctx.Db,
            clock,
            failingServices.GetRequiredService<RentalCommand.Core.Atomic.IAtomicUnitOfWork>());

        var approve = async () => await failingService.ApproveAsync(scope, draft!.Id,
            new ApproveOwnerDistributionRequest { BankReference = "DIST-202701-O02", ExportReference = "DIST-202701-O02" },
            "approve-rollback-coble");

        var failure = await approve.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Injected owner-distribution outbox failure.");
        _ctx.Db.ChangeTracker.Clear();
        var persisted = await _ctx.Db.OwnerDistributions.SingleAsync(distribution => distribution.Id == draft!.Id);
        persisted.Status.Should().Be(OwnerDistributionStatus.Draft);
        persisted.BankReference.Should().BeNull();
        persisted.ApprovedAt.Should().BeNull();
        persisted.CreatedAt.Should().Be(FrozenBusinessNowUtc);
        persisted.UpdatedAt.Should().Be(FrozenBusinessNowUtc);
        (await service.SumForOwnerYearAsync(_portfolioId, owner.Id, 2027)).Should().Be(0m);
        var failedApprovalOutboxCount = await _ctx.Db.OutboxMessages.AsNoTracking().CountAsync(outbox =>
            outbox.PortfolioId == _portfolioId &&
            outbox.IdempotencyKey.Contains("approve-rollback-coble"));
        failedApprovalOutboxCount.Should().Be(0);
    }

    private static string[] Policies(string methodName) =>
        typeof(OwnerDistributionController).GetMethod(methodName)!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy!)
            .ToArray();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Rental Command repository root.");
    }

    private void SeedPortfolio()
    {
        var now = DateTime.UtcNow;
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = _portfolioId,
            Name = "Owner Distribution Authorization",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private void SeedFrozenBusinessDate()
    {
        var clock = _ctx.Db.SimulationClocks.SingleOrDefault(clock => clock.Id == 1);
        if (clock is null)
        {
            _ctx.Db.SimulationClocks.Add(new SimulationClock { Id = 1 });
            clock = _ctx.Db.SimulationClocks.Local.Single(clock => clock.Id == 1);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = FrozenBusinessNowUtc;
        clock.RealAnchorUtc = FrozenBusinessNowUtc;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = FrozenBusinessNowUtc;
        _ctx.Db.SaveChanges();
        _ctx.Db.ChangeTracker.Clear();
    }

    private OwnerEntity SeedOwner(string name)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = _portfolioId,
            OwnerEntityType = OwnerEntityType.Person,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(int ownerEntityId, string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = _portfolioId,
            Name = name,
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();
        _ctx.Db.PropertyOwnerships.Add(new PropertyOwnership
        {
            PortfolioId = _portfolioId,
            PropertyId = property.Id,
            OwnerEntityId = ownerEntityId,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = now,
            StatementRecipientName = "Owner",
            PayeeName = "Owner",
        });
        _ctx.Db.SaveChanges();
        return property;
    }

    private OwnerDistribution SeedDistribution(
        int ownerEntityId,
        int propertyId,
        decimal amount,
        OwnerDistributionStatus status = OwnerDistributionStatus.Approved)
    {
        var now = DateTime.UtcNow;
        var distribution = new OwnerDistribution
        {
            PortfolioId = _portfolioId,
            OwnerEntityId = ownerEntityId,
            PropertyId = propertyId,
            Date = now,
            Amount = amount,
            Method = DistributionMethod.Check,
            Status = status,
            ApprovedAt = status == OwnerDistributionStatus.Approved ? now : null,
            ApprovedBusinessDate = status == OwnerDistributionStatus.Approved ? now.Date : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerDistributions.Add(distribution);
        _ctx.Db.SaveChanges();
        return distribution;
    }

    private sealed class ThrowOnOwnerDistributionOutboxInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfTarget(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTarget(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfTarget(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"OutboxMessages\"", StringComparison.OrdinalIgnoreCase) &&
                command.Parameters.Cast<DbParameter>().Any(parameter =>
                    parameter.Value?.ToString()?.Contains("OwnerDistribution", StringComparison.Ordinal) == true))
            {
                throw new InvalidOperationException("Injected owner-distribution outbox failure.");
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
