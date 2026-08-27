using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name2)]
public class OwnerEntityServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private OwnerEntityService _sut = null!;
    private WorkspaceReadScope _scope;

    public OwnerEntityServiceTests(MigratedPostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync([new RecordingCommandInterceptor(_commands)]);
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(OwnerEntityServiceTests));
        await _ctx.ActivateApiScopeAsync(_scope);
        _sut = new OwnerEntityService(
            _ctx.Db, Mock.Of<IDataUpdateService>(), TimeProvider.System,
            Mock.Of<RentalCommand.Core.Atomic.IWriteExecutor>());
    }

    public async Task DisposeAsync() => await _ctx.DisposeAsync();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedOwner("Cedar Owner", OwnerEntityType.Person);
        SeedOwner("Delta Holdings", OwnerEntityType.LLC);

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, new ListQuery
        {
            Sort = "name",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(o => o.Name).Should().Equal("Bravo Trust", "Cedar Owner");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"OwnerEntities\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_ReturnsAssignedPropertyCountsDbSide()
    {
        var owner = SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        var otherOwner = SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedProperty(owner, "Alpha One");
        SeedProperty(owner, "Alpha Two");
        SeedProperty(otherOwner, "Bravo One");

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, new ListQuery
        {
            Sort = "name",
            Take = 10,
        });

        result.Items.Single(o => o.Id == owner.Id).AssignedPropertyCount.Should().Be(2);
        result.Items.Single(o => o.Id == otherOwner.Id).AssignedPropertyCount.Should().Be(1);
        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListPageAsync_FiltersByOwnerEntityTypeDbSide()
    {
        SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedOwner("Bravo Trust", OwnerEntityType.Trust);
        SeedOwner("Cedar Owner", OwnerEntityType.Person);
        SeedOwner("Delta Holdings", OwnerEntityType.LLC);

        _commands.Clear();
        var result = await _sut.ListPageAsync(_scope, new OwnerEntityListQuery
        {
            OwnerEntityType = OwnerEntityType.LLC,
            Sort = "name",
            Take = 10,
        });

        result.TotalCount.Should().Be(2);
        result.Items.Select(o => o.Name).Should().Equal("Alpha Holdings", "Delta Holdings");
        _commands.Should().Contain(sql =>
            sql.Contains("WHERE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OwnerEntityType", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_ReturnsAssignedPropertyCount()
    {
        var owner = SeedOwner("Alpha Holdings", OwnerEntityType.LLC);
        SeedProperty(owner, "Alpha One");
        SeedProperty(owner, "Alpha Two");

        var result = await _sut.GetAsync(_scope, owner.Id);

        result.Should().NotBeNull();
        result!.AssignedPropertyCount.Should().Be(2);
    }

    [Fact]
    public async Task Update_WithEmptyOptionalFields_ClearsThem()
    {
        await using var services = AtomicDomainTestKernel.CreateForCoreCrudPostgreSql(_ctx.ConnectionString);
        var service = new OwnerEntityService(
            services.GetRequiredService<RentalCommand.Data.RentalCommandDbContext>(),
            Mock.Of<IDataUpdateService>(), TimeProvider.System,
            services.GetRequiredService<RentalCommand.Core.Atomic.IWriteExecutor>());
        var created = await service.CreateAsync(
            _scope,
            new CreateOwnerEntityRequest
            {
                Name = "Clear Fields Holdings",
                OwnerEntityType = OwnerEntityType.LLC,
                TaxId = "12-3456789",
                Phone = "614-555-0100",
                Email = "owner@example.test",
            },
            "owner-empty-fields-create");

        await service.UpdateAsync(
            _scope,
            created!.Id,
            new UpdateOwnerEntityRequest { Phone = "", Email = "", TaxId = null },
            "owner-empty-fields-update");

        var persisted = await _ctx.Db.OwnerEntities.SingleAsync(owner => owner.Id == created.Id);
        persisted.Phone.Should().BeNull();
        persisted.Email.Should().BeNull();
        persisted.TaxId.Should().Be("12-3456789");
    }

    [Fact]
    public async Task OwnerEntityAuditAuthorization_IsTranslatedAndReturnsAuthorizedHistory()
    {
        var owner = SeedOwner("Audit Holdings", OwnerEntityType.LLC);
        var audit = new AtomicAuditLog
        {
            AttemptId = Guid.NewGuid(),
            CommandType = "owner-audit-test",
            CommandIdempotencyKey = Guid.NewGuid().ToString("N"),
            MutationOrdinal = 1,
            PortfolioId = PortfolioId,
            UserId = _scope.UserId,
            EntityType = nameof(OwnerEntity),
            EntityId = owner.Id,
            Operation = AuditLogOperation.Updated,
            ChangeReason = "Owner audit authorization proof",
            Timestamp = DateTime.UtcNow,
        };
        _ctx.Db.AtomicAuditLogs.Add(audit);
        await _ctx.Db.SaveChangesAsync();

        var query = _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .WhereAuthorized(_ctx.Db, _scope, DateTime.UtcNow)
            .Where(row => row.EntityType == nameof(OwnerEntity) && row.EntityId == owner.Id);
        var sql = query.ToQueryString();
        var rows = await query.ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(audit.Id);
        sql.Should().Contain("\"OwnerEntities\"");
        sql.Should().Contain("\"PropertyOwnerships\"");
        sql.Should().Contain("rc_api_effective_capability_scopes");
        sql.Count(character => character == ';').Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task LeaselessTenantAudit_VisibleToPortfolioWideCaller()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(SeedOwner("Scope Test Owner", OwnerEntityType.LLC), "Scope Test Property");
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId, FirstName = "Lease-less", LastName = "Tenant",
            CreatedAt = now, UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        await _ctx.Db.SaveChangesAsync();

        var audit = new AtomicAuditLog
        {
            AttemptId = Guid.NewGuid(), CommandType = "tenant-audit-test",
            CommandIdempotencyKey = Guid.NewGuid().ToString("N"), MutationOrdinal = 1,
            PortfolioId = PortfolioId, EntityType = nameof(Tenant), EntityId = tenant.Id,
            Operation = AuditLogOperation.Created, Timestamp = now,
        };
        _ctx.Db.AtomicAuditLogs.Add(audit);
        await _ctx.Db.SaveChangesAsync();

        var query = _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .WhereAuthorized(_ctx.Db, _scope, now)
            .Where(row => row.EntityType == nameof(Tenant) && row.EntityId == tenant.Id);
        var sql = query.ToQueryString();
        var rows = await query.ToListAsync();

        rows.Should().ContainSingle().Which.Id.Should().Be(audit.Id);
        sql.Count(character => character == ';').Should().BeLessThanOrEqualTo(1);

        var propertyScope = _ctx.Db.SeedPropertyManagerScope(
            PortfolioId, property.Id, nameof(LeaselessTenantAudit_VisibleToPortfolioWideCaller));
        await _ctx.ActivateApiScopeAsync(propertyScope);

        var limitedRows = await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .WhereAuthorized(_ctx.Db, propertyScope, now)
            .Where(row => row.EntityType == nameof(Tenant) && row.EntityId == tenant.Id).ToListAsync();

        limitedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerEntityAuditAuthorization_StaleScopeFailsClosed()
    {
        var owner = SeedOwner("Denied Audit Holdings", OwnerEntityType.LLC);
        _ctx.Db.AtomicAuditLogs.Add(new AtomicAuditLog
        {
            AttemptId = Guid.NewGuid(),
            CommandType = "owner-audit-denied-test",
            CommandIdempotencyKey = Guid.NewGuid().ToString("N"),
            MutationOrdinal = 1,
            PortfolioId = PortfolioId,
            UserId = _scope.UserId,
            EntityType = nameof(OwnerEntity),
            EntityId = owner.Id,
            Operation = AuditLogOperation.Created,
            ChangeReason = "Denied owner audit authorization proof",
            Timestamp = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();
        var stale = new WorkspaceReadScope(
            _scope.PortfolioId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision + 1);

        var rows = await _ctx.Db.AtomicAuditLogs.AsNoTracking()
            .WhereAuthorized(_ctx.Db, stale, DateTime.UtcNow)
            .Where(row => row.EntityType == nameof(OwnerEntity) && row.EntityId == owner.Id)
            .ToListAsync();

        rows.Should().BeEmpty();
    }

    private OwnerEntity SeedOwner(string name, OwnerEntityType type)
    {
        var now = DateTime.UtcNow;
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = name,
            OwnerEntityType = type,
            Email = $"{name.Replace(" ", ".", StringComparison.Ordinal).ToLowerInvariant()}@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.OwnerEntities.Add(owner);
        _ctx.Db.SaveChanges();
        return owner;
    }

    private Property SeedProperty(OwnerEntity owner, string name)
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
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
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            OwnerEntityId = owner.Id,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = now,
            StatementRecipientName = owner.Name,
            StatementRecipientEmail = owner.Email,
            PayeeName = owner.Name,
        });
        _ctx.Db.SaveChanges();
        return property;
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
