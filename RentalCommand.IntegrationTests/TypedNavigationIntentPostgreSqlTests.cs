using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection2.Name)]
public sealed class TypedNavigationIntentPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public TypedNavigationIntentPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task ListAndGet_EmitTypedIntentWithServerSidePaging()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Navigation property",
            AddressLine1 = "1 Typed Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        var workOrder = new WorkOrder
        {
            PortfolioId = 1,
            PropertyId = property.Id,
            Title = "Typed notification target",
            Description = "PostgreSQL projection proof",
            RequestedAt = now,
            UpdatedAt = now,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        var older = Notification(
            "Older notification", now.AddMinutes(-2), nameof(WorkOrder), workOrder.Id);
        var expected = Notification(
            "Expected notification", now.AddMinutes(-1), nameof(WorkOrder), workOrder.Id);
        var newest = Notification(
            "Newest notification", now, nameof(WorkOrder), workOrder.Id);
        _context.Db.Notifications.AddRange(older, expected, newest);
        await _context.Db.SaveChangesAsync();
        _context.Db.NotificationReadStates.Add(new NotificationReadState
        {
            PortfolioId = 1,
            NotificationId = older.Id,
            UserId = 1,
            ReadAt = now,
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var scope = new WorkspaceReadScope(1, 1, Guid.NewGuid(), 44, 9);
        var sut = new NotificationService(_context.Db, TimeProvider.System, null!);
        _commands.Clear();

        var page = await sut.ListAsync(
            scope, NavigationExperience.Management,
            unreadOnly: true, skip: 1, take: 1);

        page.Should().ContainSingle();
        page[0].Id.Should().Be(expected.Id);
        page[0].NavigationIntent.Should().NotBeNull();
        page[0].NavigationIntent!.Destination.Should().Be(NavigationDestination.WorkOrder);
        page[0].NavigationIntent.AccessContextId.Should().Be(44);
        page[0].NavigationIntent.AccessRevision.Should().Be(9);
        page[0].NavigationIntent.Resource.Should().BeEquivalentTo(new
        {
            Kind = nameof(WorkOrder),
            Id = workOrder.Id,
        });
        _commands.Should().ContainSingle(
            "authorization, filtering, sorting, paging, and projection execute in one statement");
        var listSql = _commands.Single();
        listSql.Should().Contain("NOT EXISTS");
        listSql.Should().Contain("ORDER BY");
        listSql.Should().Contain("LIMIT");
        listSql.Should().Contain("OFFSET");
        listSql.Should().Contain("\"NotificationReadStates\"");
        listSql.Should().Contain("\"WorkOrders\"");
        CaptureSql("LIST", listSql);

        _commands.Clear();
        var detail = await sut.GetAsync(
            scope, NavigationExperience.Management, expected.Id);

        detail.Should().NotBeNull();
        detail!.NavigationIntent!.Resource!.Id.Should().Be(workOrder.Id);
        _commands.Should().ContainSingle(
            "detail authorization and projection execute in one statement");
        var detailSql = _commands.Single();
        detailSql.Should().Contain("\"Id\"");
        detailSql.Should().Contain("\"WorkOrders\"");
        detailSql.Should().NotContain("\"ActionUrl\"");
        CaptureSql("DETAIL", detailSql);
    }

    [Fact]
    public async Task CrossContextIntent_IsNotProjected()
    {
        var now = DateTime.UtcNow;
        var otherPortfolio = new Portfolio
        {
            Name = "Other navigation workspace",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Portfolios.Add(otherPortfolio);
        await _context.Db.SaveChangesAsync();
        var crossContextOwner = new OwnerEntity
        {
            PortfolioId = otherPortfolio.Id,
            Name = "Other owner",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.OwnerEntities.Add(crossContextOwner);
        await _context.Db.SaveChangesAsync();
        var notification = Notification(
            "Cross-context target", now, nameof(OwnerEntity), crossContextOwner.Id);
        notification.NavigationAccessContextId = 55;
        notification.NavigationAccessRevision = 11;
        _context.Db.Notifications.Add(notification);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var scope = new WorkspaceReadScope(1, 1, Guid.NewGuid(), 55, 11);
        var sut = new NotificationService(_context.Db, TimeProvider.System, null!);
        _commands.Clear();

        var detail = await sut.GetAsync(
            scope, NavigationExperience.Management, notification.Id);

        detail.Should().NotBeNull("the notification row itself belongs to the current workspace");
        detail!.NavigationIntent.Should().BeNull(
            "a related identity from another workspace cannot become a destination");
        _commands.Should().ContainSingle();
        var sql = _commands.Single();
        sql.Should().Contain("\"OwnerEntities\"");
        sql.Should().Contain("\"PortfolioId\"");
        CaptureSql("CROSS_CONTEXT", sql);
    }

    [Fact]
    public async Task List_ProjectsAuthorizedTenantLedgerEntryIntentAndRejectsUnsafeVariants()
    {
        var now = DateTime.UtcNow;
        var target = await SeedTenantAccountLedgerEntryAsync(1, "tenant-ledger-valid", now);
        var otherPortfolio = new Portfolio
        {
            Name = "Other tenant ledger workspace",
            ManagementCompanyName = "Other Tenant Ledger Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Portfolios.Add(otherPortfolio);
        await _context.Db.SaveChangesAsync();
        var crossScope = await SeedTenantAccountLedgerEntryAsync(
            otherPortfolio.Id, "tenant-ledger-cross-scope", now);

        _context.Db.Notifications.AddRange(
            TenantLedgerNotification(
                "tenant-ledger-valid", now, target.Account.Id, target.Entry.Id),
            TenantLedgerNotification(
                "tenant-ledger-malformed-parent", now.AddSeconds(-1), null, target.Entry.Id),
            TenantLedgerNotification(
                "tenant-ledger-cross-scope", now.AddSeconds(-2), crossScope.Account.Id, crossScope.Entry.Id),
            TenantLedgerNotification(
                "tenant-ledger-expired", now.AddSeconds(-3), target.Account.Id, target.Entry.Id,
                expiresAtUtc: now.AddSeconds(-1)),
            TenantLedgerNotification(
                "tenant-ledger-management-experience", now.AddSeconds(-4), target.Account.Id, target.Entry.Id,
                experience: NavigationExperience.Management));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var scope = new WorkspaceReadScope(1, 1, Guid.NewGuid(), 6, 1);
        var sut = new NotificationService(_context.Db, TimeProvider.System, null!);
        _commands.Clear();

        var page = await sut.ListAsync(
            scope, NavigationExperience.Tenant, unreadOnly: false, skip: 0, take: 10);

        var byTitle = page
            .Where(notification => notification.Title.StartsWith("tenant-ledger-"))
            .ToDictionary(notification => notification.Title);
        byTitle.Should().HaveCount(5);
        byTitle["tenant-ledger-valid"].NavigationIntent.Should().NotBeNull();
        byTitle["tenant-ledger-valid"].NavigationIntent!.Destination
            .Should().Be(NavigationDestination.TenantLedgerEntry);
        byTitle["tenant-ledger-valid"].NavigationIntent!.Resource.Should().BeEquivalentTo(new
        {
            Kind = nameof(TenantLedgerEntry),
            Id = checked((int)target.Entry.Id),
        });
        byTitle["tenant-ledger-valid"].NavigationIntent!.ParentResource.Should().BeEquivalentTo(new
        {
            Kind = nameof(TenantAccount),
            Id = target.Account.Id,
        });

        foreach (var rejected in new[]
                 {
                     "tenant-ledger-malformed-parent",
                     "tenant-ledger-cross-scope",
                     "tenant-ledger-expired",
                     "tenant-ledger-management-experience",
                 })
        {
            byTitle[rejected].NavigationIntent.Should().BeNull(
                $"{rejected} must not become a tenant portal destination");
        }

        _commands.Should().ContainSingle(
            "tenant ledger intent authorization, paging, and projection execute in one statement");
        var sql = _commands.Single();
        sql.Should().Contain("\"TenantLedgerEntries\"");
        sql.Should().Contain("\"TenantAccountId\"");
        sql.Should().Contain("LIMIT");
        sql.Should().NotContain("\"ActionUrl\"");
        CaptureSql("TENANT_LEDGER_LIST", sql);
    }

    private async Task RunAtL02BoundaryAsync(Func<RentalCommandDbContext, Task> test)
    {
        var databaseName = $"rc_l03_boundary_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(_context.ConnectionString)
        {
            Database = "postgres",
            Pooling = false,
        }.ConnectionString;
        var boundaryConnection = new NpgsqlConnectionStringBuilder(_context.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;
        RentalCommandDbContext? db = null;
        ExceptionDispatchInfo? primaryFailure = null;
        Exception? cleanupFailure = null;

        await ExecuteAdminCommandAsync(
            adminConnection, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
                .UseNpgsql(boundaryConnection)
                .Options;
            db = new RentalCommandDbContext(options);
            await db.Database.MigrateAsync(
                "20260716120000_AddExpenseOperationalScopeAndAllocations");
            await db.Database.ExecuteSqlRawAsync("""
                ALTER TABLE "AspNetUsers" ADD COLUMN "TermsPrivacyAccepted" boolean NOT NULL DEFAULT FALSE;
                ALTER TABLE "AspNetUsers" ADD COLUMN "TermsPrivacyAcceptedAtUtc" timestamp with time zone NULL;
                ALTER TABLE "AspNetUsers" ADD COLUMN "TermsPrivacyVersion" character varying(100) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "AccessWarnings" character varying(2000) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "CallBeforeEntry" boolean NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "CallIfNotHome" boolean NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "ChronologyRepairOriginalUpdatedAtUtc" timestamp with time zone NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "EntryNotes" character varying(2000) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "PermissionToEnter" boolean NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "PetWarnings" character varying(2000) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "RequesterEmail" character varying(320) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "RequesterName" character varying(200) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "RequesterPhone" character varying(64) NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "ResidentMustBePresent" boolean NULL;
                ALTER TABLE "WorkOrders" ADD COLUMN "SubmittedByLabel" character varying(120) NULL;
                """);
            var seededAt = DateTime.UtcNow;
            db.AddRange(
                new Portfolio
                {
                    Name = "Typed navigation boundary portfolio",
                    ManagementCompanyName = "Boundary Co",
                    TimeZone = "UTC",
                    CreatedAt = seededAt,
                    UpdatedAt = seededAt,
                },
                new ApplicationUser
                {
                    UserName = "typed-navigation-boundary@example.test",
                    NormalizedUserName = "TYPED-NAVIGATION-BOUNDARY@EXAMPLE.TEST",
                    Email = "typed-navigation-boundary@example.test",
                    NormalizedEmail = "TYPED-NAVIGATION-BOUNDARY@EXAMPLE.TEST",
                    DisplayName = "Typed Navigation Boundary Actor",
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                    CreatedAt = seededAt,
                });
            await db.SaveChangesAsync();
            await test(db);
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (db is not null)
            {
                try
                {
                    await db.DisposeAsync();
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            try
            {
                await ExecuteAdminCommandAsync(
                    adminConnection, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanupFailure is null
                    ? exception
                    : new AggregateException(cleanupFailure, exception);
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                primaryFailure.SourceException.Data["L02BoundaryCleanupFailure"] = cleanupFailure;
            }

            primaryFailure.Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static async Task ExecuteAdminCommandAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddManagementAccess(
        RentalCommandDbContext db,
        int userId,
        int selectedPropertyId,
        DateTime now)
    {
        var accessContext = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = 2,
            ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties,
            Status = MembershipRoleAssignmentStatus.Active,
            EffectiveFromUtc = now.AddDays(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
        {
            PortfolioId = 1,
            PropertyId = selectedPropertyId,
        });
        db.AddRange(accessContext, membership, assignment);
    }

    private static void AddAmbiguousRelationshipAccess(
        RentalCommandDbContext db,
        int userId,
        int propertyId,
        DateTime now)
    {
        var unit = new Unit
        {
            PortfolioId = 1,
            PropertyId = propertyId,
            UnitNumber = $"ambiguous-{userId}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = 1,
            FirstName = "Ambiguous",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var leaseManagement = new LeaseManagement
        {
            PortfolioId = 1,
            PropertyId = propertyId,
            Unit = unit,
            RelationshipNumber = $"LM-AMBIGUOUS-{userId}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = 1,
            LeaseManagement = leaseManagement,
            Tenant = tenant,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
            ChangeReason = "Typed navigation ambiguity proof",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var owner = new OwnerEntity
        {
            PortfolioId = 1,
            Name = "Ambiguous Owner",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            UserId = userId,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.AddRange(
            new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                AccessContext = accessContext,
                ApplicationUserId = userId,
                LeaseManagementParty = party,
                GrantedAtUtc = now,
                GrantedByUserId = 1,
                Reason = "Typed navigation ambiguity proof",
            },
            new OwnerUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = 1,
                AccessContext = accessContext,
                ApplicationUserId = userId,
                OwnerEntity = owner,
                EffectiveFromUtc = now.AddDays(-1),
                GrantedAtUtc = now,
                GrantedByUserId = 1,
                Reason = "Typed navigation ambiguity proof",
            });
    }

    private static Property Property(string name, int portfolioId, DateTime now) => new()
    {
        PortfolioId = portfolioId,
        Name = name,
        AddressLine1 = "1 Migration Way",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static WorkOrder WorkOrder(
        string title,
        int portfolioId,
        int propertyId,
        DateTime now) => new()
    {
        PortfolioId = portfolioId,
        PropertyId = propertyId,
        Title = title,
        Description = "Queued push migration authority proof",
        RequestedAt = now,
        UpdatedAt = now,
    };

    private async Task<(TenantAccount Account, TenantLedgerEntry Entry)> SeedTenantAccountLedgerEntryAsync(
        int portfolioId,
        string suffix,
        DateTime now)
    {
        var property = Property($"Tenant ledger property {suffix}", portfolioId, now);
        var unit = new Unit
        {
            PortfolioId = portfolioId,
            Property = property,
            UnitNumber = $"TL-{suffix}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            Property = property,
            Unit = unit,
            RelationshipNumber = $"LM-TL-{suffix}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            LeaseManagement = relationship,
            AccountNumber = $"TA-TL-{suffix}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var entry = new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = portfolioId,
            TenantAccount = account,
            EntryType = TenantLedgerEntryType.ManualCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = 1250m,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(now),
            DueOn = DateOnly.FromDateTime(now.AddDays(7)),
            PostedAtUtc = now,
            Description = $"Tenant ledger notification proof {suffix}",
            BusinessKey = $"tenant-ledger-notification:{suffix}",
            CreatedByUserId = 1,
        };
        _context.Db.AddRange(property, unit, relationship, account, entry);
        if (portfolioId == 1 && suffix == "tenant-ledger-valid")
        {
            var tenant = new Tenant
            {
                PortfolioId = portfolioId,
                FirstName = "Typed",
                LastName = "Navigation",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var party = new LeaseManagementParty
            {
                PortfolioId = portfolioId,
                LeaseManagement = relationship,
                Tenant = tenant,
                Role = LeaseManagementPartyRole.PrimaryTenant,
                EffectiveFrom = DateOnly.FromDateTime(now.AddDays(-1)),
                ChangeReason = "Typed navigation access proof",
                CreatedAtUtc = now,
                CreatedByUserId = 1,
            };
            var accessContext = new WorkspaceAccessContext
            {
                Id = 6,
                UserId = 1,
                PortfolioId = portfolioId,
                Status = WorkspaceAccessContextStatus.Active,
                LastAuthorizedExperience = WorkspaceExperience.Tenant,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            _context.Db.AddRange(tenant, party, accessContext, new TenantUserAccess
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                AccessContext = accessContext,
                ApplicationUserId = 1,
                LeaseManagementParty = party,
                GrantedAtUtc = now,
                GrantedByUserId = 1,
                Reason = "Typed navigation access proof",
            });
        }
        await _context.Db.SaveChangesAsync();
        return (account, entry);
    }

    private static OutboxMessage PushCase(
        string name,
        int relatedEntityId,
        int routeId,
        string? actionUrl = null,
        string deviceToken = "typed-navigation-migration-device")
    {
        var now = DateTime.UtcNow;
        return new OutboxMessage
        {
            PortfolioId = 1,
            MessageType = "push",
            Payload = JsonSerializer.Serialize(new
            {
                deviceToken,
                actionUrl = actionUrl ?? $"/maintenance/{routeId}",
                type = "VendorJobCompleted",
                relatedEntityType = nameof(WorkOrder),
                relatedEntityId,
                preserved = name,
            }),
            IdempotencyKey = $"typed-nav-backfill-{name}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        };
    }

    private static Notification Notification(
        string title,
        DateTime createdAt,
        string relatedEntityType,
        int relatedEntityId) => new()
    {
        PortfolioId = 1,
        UserId = 1,
        Type = "VendorJobCompleted",
        Title = title,
        Message = "Open the typed destination.",
        Severity = "Info",
        NavigationExperience = NavigationExperience.Management,
        NavigationDestination = relatedEntityType == nameof(WorkOrder)
            ? NavigationDestination.WorkOrder
            : NavigationDestination.Owners,
        NavigationAccessContextId = 44,
        NavigationAccessRevision = 9,
        NavigationResourceKind = relatedEntityType,
        NavigationResourceId = relatedEntityId,
        NavigationAction = NavigationAction.Open,
        NavigationExpiresAtUtc = createdAt.AddDays(7),
        NavigationFallbackDestination = NavigationDestination.Home,
        RelatedEntityType = relatedEntityType,
        RelatedEntityId = relatedEntityId,
        CreatedAt = createdAt,
    };

    private static Notification TenantLedgerNotification(
        string title,
        DateTime createdAt,
        int? tenantAccountId,
        long tenantLedgerEntryId,
        NavigationExperience experience = NavigationExperience.Tenant,
        DateTime? expiresAtUtc = null) => new()
    {
        PortfolioId = 1,
        UserId = 1,
        Type = "TenantRentCharge",
        Title = title,
        Message = "Open the tenant ledger entry.",
        Severity = "Info",
        NavigationExperience = experience,
        NavigationDestination = NavigationDestination.TenantLedgerEntry,
        NavigationAccessContextId = 6,
        NavigationAccessRevision = 1,
        NavigationResourceKind = nameof(TenantLedgerEntry),
        NavigationResourceId = checked((int)tenantLedgerEntryId),
        NavigationParentResourceKind = tenantAccountId is null ? null : nameof(TenantAccount),
        NavigationParentResourceId = tenantAccountId,
        NavigationAction = NavigationAction.Open,
        NavigationExpiresAtUtc = expiresAtUtc ?? createdAt.AddDays(7),
        NavigationFallbackDestination = NavigationDestination.Notifications,
        RelatedEntityType = nameof(TenantLedgerEntry),
        RelatedEntityId = checked((int)tenantLedgerEntryId),
        CreatedAt = createdAt,
    };

    private void CaptureSql(string label, string sql)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(sql);
    }

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
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
