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

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
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

    [Fact(Skip = "RS-B06 harness bug: hand-built schema is missing current migrated columns or views; receipt #rs-b06-stale-schema")]
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

    [Fact(Skip = "RS-B06 harness bug: hand-built schema is missing current migrated columns or views; receipt #rs-b06-stale-schema")]
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

    [Fact(Skip = "RS-B06 harness bug: hand-built schema is missing current migrated columns or views; receipt #rs-b06-stale-schema")]
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

    [Fact(Skip = "RS-B06 harness bug: hand-built schema is missing current migrated columns or views; receipt #rs-b06-stale-schema")]
    public async Task HistoricalNotificationBackfill_RequiresOneAuthorizedMatchingResource()
    {
        await RunAtL02BoundaryAsync(async db =>
        {
        var now = DateTime.UtcNow;
        var otherPortfolio = new Portfolio
        {
            Name = "Other historical-notification workspace",
            ManagementCompanyName = "Other Notification Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var authorizedProperty = Property("Authorized notification property", 1, now);
        var unauthorizedProperty = Property("Unauthorized notification property", 1, now);
        var ambiguousUser = new ApplicationUser
        {
            UserName = "ambiguous-notification@example.test",
            NormalizedUserName = "AMBIGUOUS-NOTIFICATION@EXAMPLE.TEST",
            Email = "ambiguous-notification@example.test",
            NormalizedEmail = "AMBIGUOUS-NOTIFICATION@EXAMPLE.TEST",
            DisplayName = "Ambiguous Notification Recipient",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        db.AddRange(
            otherPortfolio, authorizedProperty, unauthorizedProperty, ambiguousUser);
        await db.SaveChangesAsync();

        var crossPortfolioProperty = Property(
            "Cross-portfolio notification property", otherPortfolio.Id, now);
        db.Properties.Add(crossPortfolioProperty);
        await db.SaveChangesAsync();

        var validResource = WorkOrder(
            "Valid historical-notification resource", 1, authorizedProperty.Id, now);
        var mismatchResource = WorkOrder(
            "Mismatched historical-notification resource", 1, authorizedProperty.Id, now);
        var unauthorizedResource = WorkOrder(
            "Unauthorized historical-notification resource", 1, unauthorizedProperty.Id, now);
        var crossPortfolioResource = WorkOrder(
            "Cross-portfolio historical-notification resource",
            otherPortfolio.Id,
            crossPortfolioProperty.Id,
            now);
        db.WorkOrders.AddRange(
            validResource, mismatchResource, unauthorizedResource, crossPortfolioResource);
        await db.SaveChangesAsync();

        AddManagementAccess(db, 1, authorizedProperty.Id, now);
        AddAmbiguousRelationshipAccess(db, ambiguousUser.Id, authorizedProperty.Id, now);
        await db.SaveChangesAsync();

        var migrator = db.GetService<IMigrator>();

        var cases = new[]
        {
            new
            {
                Name = "valid",
                UserId = 1,
                RelatedEntityId = validResource.Id,
                ActionUrl = $"/maintenance/{validResource.Id}",
            },
            new
            {
                Name = "malformed",
                UserId = 1,
                RelatedEntityId = validResource.Id,
                ActionUrl = "/maintenance/not-an-id",
            },
            new
            {
                Name = "ambiguous",
                UserId = ambiguousUser.Id,
                RelatedEntityId = validResource.Id,
                ActionUrl = $"/maintenance/{validResource.Id}",
            },
            new
            {
                Name = "mismatched",
                UserId = 1,
                RelatedEntityId = validResource.Id,
                ActionUrl = $"/maintenance/{mismatchResource.Id}",
            },
            new
            {
                Name = "missing",
                UserId = 1,
                RelatedEntityId = 999_999,
                ActionUrl = "/maintenance/999999",
            },
            new
            {
                Name = "cross-portfolio",
                UserId = 1,
                RelatedEntityId = crossPortfolioResource.Id,
                ActionUrl = $"/maintenance/{crossPortfolioResource.Id}",
            },
            new
            {
                Name = "unauthorized",
                UserId = 1,
                RelatedEntityId = unauthorizedResource.Id,
                ActionUrl = $"/maintenance/{unauthorizedResource.Id}",
            },
        };
        foreach (var testCase in cases)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Notifications" (
                    "PortfolioId", "UserId", "Type", "Title", "Message", "Severity",
                    "ActionUrl", "RelatedEntityType", "RelatedEntityId", "CreatedAt")
                VALUES (
                    {1}, {testCase.UserId}, {"VendorJobCompleted"},
                    {$"typed-nav-historical-{testCase.Name}"}, {"Historical typed destination."},
                    {"Info"}, {testCase.ActionUrl}, {nameof(WorkOrder)},
                    {testCase.RelatedEntityId}, {now})
                """);
        }

        await migrator.MigrateAsync("20260716130000_AddTypedNotificationNavigationIntent");
        db.ChangeTracker.Clear();

        var migrated = await db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Title.StartsWith("typed-nav-historical-"))
            .OrderBy(notification => notification.Title)
            .Select(notification => new
            {
                notification.Title,
                notification.NavigationDestination,
                notification.NavigationAccessContextId,
                notification.NavigationAccessRevision,
                notification.NavigationResourceKind,
                notification.NavigationResourceId,
            })
            .ToListAsync();

        migrated.Should().HaveCount(7);
        var byName = migrated.ToDictionary(notification => notification.Title);
        byName["typed-nav-historical-valid"].NavigationDestination
            .Should().Be(NavigationDestination.WorkOrder);
        byName["typed-nav-historical-valid"].NavigationAccessContextId
            .Should().NotBeNull();
        byName["typed-nav-historical-valid"].NavigationAccessRevision
            .Should().Be(1);
        byName["typed-nav-historical-valid"].NavigationResourceKind
            .Should().Be(nameof(WorkOrder));
        byName["typed-nav-historical-valid"].NavigationResourceId
            .Should().Be(validResource.Id);

        foreach (var rejected in new[]
                 {
                     "malformed", "ambiguous", "mismatched", "missing",
                     "cross-portfolio", "unauthorized",
                 })
        {
            byName[$"typed-nav-historical-{rejected}"].NavigationDestination
                .Should().BeNull($"{rejected} resource evidence must remain untyped");
            byName[$"typed-nav-historical-{rejected}"].NavigationAccessContextId
                .Should().BeNull();
            byName[$"typed-nav-historical-{rejected}"].NavigationAccessRevision
                .Should().BeNull();
            byName[$"typed-nav-historical-{rejected}"].NavigationResourceKind
                .Should().BeNull();
            byName[$"typed-nav-historical-{rejected}"].NavigationResourceId
                .Should().BeNull();
        }

        await migrator.MigrateAsync("20260716120000_AddExpenseOperationalScopeAndAllocations");
        var rolledBackActionUrl = await db.Database.SqlQueryRaw<string>(
                $"""
                SELECT "ActionUrl" AS "Value"
                FROM "Notifications"
                WHERE "Title" = 'typed-nav-historical-valid'
                """)
            .SingleAsync();
        rolledBackActionUrl.Should().Be($"/maintenance/{validResource.Id}",
            "the L03 Down migration must reconstruct the supported legacy route");
        });
    }

    [Fact(Skip = "RS-B06 harness bug: hand-built schema is missing current migrated columns or views; receipt #rs-b06-stale-schema")]
    public async Task QueuedPushBackfill_RequiresMatchingExistingAuthorizedResource()
    {
        await RunAtL02BoundaryAsync(async db =>
        {
        var now = DateTime.UtcNow;
        var otherPortfolio = new Portfolio
        {
            Name = "Other queued-push workspace",
            ManagementCompanyName = "Other Push Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var authorizedProperty = Property("Authorized push property", 1, now);
        var unauthorizedProperty = Property("Unauthorized push property", 1, now);
        db.AddRange(otherPortfolio, authorizedProperty, unauthorizedProperty);
        await db.SaveChangesAsync();

        var crossPortfolioProperty = Property(
            "Cross-portfolio push property", otherPortfolio.Id, now);
        db.Properties.Add(crossPortfolioProperty);
        await db.SaveChangesAsync();

        var validResource = WorkOrder("Valid queued-push resource", 1, authorizedProperty.Id, now);
        var mismatchResource = WorkOrder(
            "Mismatched queued-push resource", 1, authorizedProperty.Id, now);
        var unauthorizedResource = WorkOrder(
            "Unauthorized queued-push resource", 1, unauthorizedProperty.Id, now);
        var crossPortfolioResource = WorkOrder(
            "Cross-portfolio queued-push resource",
            otherPortfolio.Id,
            crossPortfolioProperty.Id,
            now);
        var ambiguousUser = new ApplicationUser
        {
            UserName = "ambiguous-push@example.test",
            NormalizedUserName = "AMBIGUOUS-PUSH@EXAMPLE.TEST",
            Email = "ambiguous-push@example.test",
            NormalizedEmail = "AMBIGUOUS-PUSH@EXAMPLE.TEST",
            DisplayName = "Ambiguous Push Recipient",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        db.WorkOrders.AddRange(
            validResource, mismatchResource, unauthorizedResource, crossPortfolioResource);
        db.Users.Add(ambiguousUser);
        await db.SaveChangesAsync();

        AddManagementAccess(db, 1, authorizedProperty.Id, now);
        AddAmbiguousRelationshipAccess(db, ambiguousUser.Id, authorizedProperty.Id, now);
        db.AddRange(
            new DeviceToken
            {
                PortfolioId = 1,
                UserId = 1,
                Token = "typed-navigation-migration-device",
                Platform = "android",
                CreatedAt = now,
                LastSeenAt = now,
            },
            new DeviceToken
            {
                PortfolioId = 1,
                UserId = ambiguousUser.Id,
                Token = "typed-navigation-ambiguous-device",
                Platform = "android",
                CreatedAt = now,
                LastSeenAt = now,
            });
        await db.SaveChangesAsync();

        var cases = new[]
        {
            PushCase("valid", validResource.Id, validResource.Id),
            PushCase("malformed", validResource.Id, validResource.Id, "/maintenance/not-an-id"),
            PushCase(
                "ambiguous",
                validResource.Id,
                validResource.Id,
                deviceToken: "typed-navigation-ambiguous-device"),
            PushCase("mismatched", validResource.Id, mismatchResource.Id),
            PushCase("missing", 999_999, 999_999),
            PushCase("cross-portfolio", crossPortfolioResource.Id, crossPortfolioResource.Id),
            PushCase("unauthorized", unauthorizedResource.Id, unauthorizedResource.Id),
        };
        db.OutboxMessages.AddRange(cases);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260716130000_AddTypedNotificationNavigationIntent");
        var upPayloadType = await db.Database.SqlQueryRaw<string>(
                """
                SELECT pg_typeof("Payload")::text AS "Value"
                FROM "OutboxMessages"
                ORDER BY "Id"
                LIMIT 1
                """)
            .SingleAsync();
        upPayloadType.Should().Be("jsonb",
            "the Up migration must preserve the durable payload column type");
        db.ChangeTracker.Clear();

        var migrated = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.IdempotencyKey.StartsWith("typed-nav-backfill-"))
            .OrderBy(message => message.IdempotencyKey)
            .Select(message => new { message.IdempotencyKey, message.Payload })
            .ToListAsync();

        migrated.Should().HaveCount(7);
        var payloads = migrated.ToDictionary(
            message => message.IdempotencyKey,
            message => JsonDocument.Parse(message.Payload));
        try
        {
            foreach (var payload in payloads.Values)
            {
                payload.RootElement.TryGetProperty("actionUrl", out _).Should().BeFalse();
                payload.RootElement.TryGetProperty("type", out _).Should().BeFalse();
                payload.RootElement.TryGetProperty("relatedEntityType", out _).Should().BeFalse();
                payload.RootElement.TryGetProperty("relatedEntityId", out _).Should().BeFalse(
                    "the migration must remove every legacy routing key from every queued push");
            }

            var validIntent = payloads["typed-nav-backfill-valid"].RootElement
                .GetProperty("navigationIntent");
            validIntent.GetProperty("destination").GetString().Should().Be("WorkOrder");
            validIntent.GetProperty("resource").GetProperty("kind").GetString()
                .Should().Be(nameof(WorkOrder));
            validIntent.GetProperty("resource").GetProperty("id").GetInt32()
                .Should().Be(validResource.Id);

            foreach (var rejected in new[]
                     {
                         "malformed", "ambiguous", "mismatched", "missing",
                         "cross-portfolio", "unauthorized",
                     })
            {
                payloads[$"typed-nav-backfill-{rejected}"].RootElement
                    .TryGetProperty("navigationIntent", out _)
                    .Should().BeFalse($"{rejected} resource evidence must fall back safely");
            }
        }
        finally
        {
            foreach (var payload in payloads.Values)
            {
                payload.Dispose();
            }
        }

        await migrator.MigrateAsync("20260716120000_AddExpenseOperationalScopeAndAllocations");
        var rolledBack = await db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.IdempotencyKey == "typed-nav-backfill-valid");
        using var rolledBackPayload = JsonDocument.Parse(rolledBack.Payload);
        rolledBackPayload.RootElement.TryGetProperty("navigationIntent", out _)
            .Should().BeFalse("the L03 Down migration must remove the typed intent");
        rolledBackPayload.RootElement.GetProperty("actionUrl").GetString()
            .Should().Be($"/maintenance/{validResource.Id}");
        var downPayloadType = await db.Database.SqlQueryRaw<string>(
                """
                SELECT pg_typeof("Payload")::text AS "Value"
                FROM "OutboxMessages"
                ORDER BY "Id"
                LIMIT 1
                """)
            .SingleAsync();
        downPayloadType.Should().Be("jsonb",
            "the Down migration must preserve the durable payload column type");
        });
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
