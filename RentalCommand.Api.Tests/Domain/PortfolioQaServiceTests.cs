using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PortfolioQaServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;
    private readonly WorkspaceReadScope _scope;
    private MembershipRoleAssignment _assignment = null!;

    public PortfolioQaServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new PortfolioQaTestDbContext(options);
        _db.Database.EnsureCreated();
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Users.Add(new ApplicationUser
        {
            Id = 1,
            UserName = "portfolio-qa@example.test",
            NormalizedUserName = "PORTFOLIO-QA@EXAMPLE.TEST",
            Email = "portfolio-qa@example.test",
            NormalizedEmail = "PORTFOLIO-QA@EXAMPLE.TEST",
            DisplayName = "Portfolio QA Actor",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _scope = SeedAdministratorScope();
        _db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT management."PortfolioId" AS "PortfolioId",
                   management."Id" AS "LeaseManagementId",
                   management."PropertyId" AS "PropertyId",
                   management."UnitId" AS "UnitId",
                   CASE WHEN agreement."TermEndOn" IS NOT NULL
                             AND agreement."TermEndOn" < date('now')
                        THEN 'Closed' ELSE 'Occupied' END AS "Lifecycle",
                   agreement."Id" AS "CurrentAgreementId",
                   trim(tenant."FirstName" || ' ' || tenant."LastName") AS "CurrentPrimaryTenantName"
            FROM "LeaseManagements" AS management
            JOIN "LeaseAgreements" AS agreement
              ON agreement."PortfolioId" = management."PortfolioId"
             AND agreement."LeaseManagementId" = management."Id"
             AND agreement."VoidedAtUtc" IS NULL
             AND agreement."DraftCanceledAtUtc" IS NULL
            LEFT JOIN "LeaseManagementParties" AS party
              ON party."PortfolioId" = management."PortfolioId"
             AND party."LeaseManagementId" = management."Id"
             AND party."Role" = 'PrimaryTenant'
             AND party."EffectiveThrough" IS NULL
            LEFT JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
            WHERE management."CanceledAtUtc" IS NULL
              AND management."AccountClosedAtUtc" IS NULL
            """);
        _db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_tenant_charge_balances" AS
            SELECT entry."PortfolioId" AS "PortfolioId",
                   entry."TenantAccountId" AS "TenantAccountId",
                   entry."Id" AS "TenantLedgerEntryId",
                   date('now') AS "BusinessDate",
                   entry."EntryType" AS "EntryType",
                   entry."Currency" AS "Currency",
                   entry."EffectiveOn" AS "EffectiveOn",
                   entry."DueOn" AS "DueOn",
                   entry."Amount" AS "OriginalAmount",
                   0 AS "ReversedAmount",
                   COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0) AS "NetAllocations",
                   max(entry."Amount" - COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0), 0) AS "OpenAmount",
                   entry."DueOn" < date('now') AND entry."Amount" > COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0) AS "IsPastDue"
            FROM "TenantLedgerEntries" AS entry
            WHERE entry."Direction" = 'Debit'
            """);
        _db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_unit_occupancy" AS
            SELECT unit."PortfolioId" AS "PortfolioId",
                   unit."PropertyId" AS "PropertyId",
                   unit."Id" AS "UnitId",
                   CURRENT_TIMESTAMP AS "EffectiveNowUtc",
                   CASE WHEN management."Id" IS NULL THEN 0 ELSE 1 END AS "IsOccupied",
                   management."Id" AS "CurrentLeaseManagementId",
                   0 AS "HasScheduledMoveIn",
                   NULL AS "NextPlannedPossessionAtUtc",
                   NULL AS "PlannedLeaseManagementId",
                   0 AS "IsInTurnover",
                   0 AS "IsOutOfService",
                   0 AS "IsOnManagementHold",
                   0 AS "HasGoverningAgreementWithoutPossession",
                   0 AS "HasPossessionWithoutGoverningAgreement",
                   NULL AS "OccupancyExceptionCode"
            FROM "Units" AS unit
            LEFT JOIN "LeaseManagements" AS management
              ON management."PortfolioId" = unit."PortfolioId"
             AND management."PropertyId" = unit."PropertyId"
             AND management."UnitId" = unit."Id"
             AND management."CanceledAtUtc" IS NULL
             AND management."PossessionGivenAtUtc" IS NOT NULL
             AND management."PossessionGivenAtUtc" <= CURRENT_TIMESTAMP
             AND (management."PossessionReturnedAtUtc" IS NULL
                  OR management."PossessionReturnedAtUtc" > CURRENT_TIMESTAMP)
            WHERE unit."DeletedAt" IS NULL
            """);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RecentExpensesTool_AggregatesFullFilteredWindowInSql_NotReturnedPageOnly()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        for (var i = 0; i < 51; i++)
        {
            _db.Expenses.Add(new Expense
            {
                PortfolioId = PortfolioId,
                Property = property,
                Description = $"Repair {i + 1}",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Paid,
                Amount = 1m,
                IncurredAt = now.AddMinutes(-i),
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_recent_expenses",
            argsJson: """{"withinDays":90}""",
            question: "Show me my recent expenses");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("total").GetDecimal().Should().Be(51m);
        _commands.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecentPaymentsTool_AggregatesFullFilteredWindowInSql_NotReturnedPageOnly()
    {
        var now = DateTime.UtcNow;
        var lease = SeedLease(now);
        for (var i = 0; i < 51; i++)
        {
            SeedReceipt(lease.Account, 1m, now.AddMinutes(-i));
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_recent_payments",
            argsJson: """{"withinDays":30}""",
            question: "Show me my recent rent payments");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("total").GetDecimal().Should().Be(51m);
        _commands.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OverdueRentTool_ExcludesClosedLeaseRelationshipCharges()
    {
        var now = DateTime.UtcNow;
        var current = SeedLease(now);
        var stale = SeedLease(now);
        current.Agreement.AgreementNumber = "CURRENT-1";
        stale.Agreement.AgreementNumber = "STALE-1";
        stale.Agreement.TermEndOn = DateOnly.FromDateTime(now.AddDays(-1));
        stale.Management.PossessionReturnedAtUtc = now.AddDays(-1);
        stale.Management.AccountClosedAtUtc = now.AddDays(-1);
        SeedRentCharge(current, 1400m, now.AddDays(-5));
        SeedRentCharge(stale, 1400m, now.AddDays(-30));
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_overdue_rent",
            argsJson: "{}",
            question: "Who is overdue?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var overdue = doc.RootElement.GetProperty("overdue");
        overdue.GetArrayLength().Should().Be(1);
        overdue[0].GetProperty("leaseNumber").GetString().Should().Be("CURRENT-1");
    }

    [Fact]
    public async Task OverdueRentTool_PagesAndDetectsTruncationInSql()
    {
        var now = DateTime.UtcNow;
        var lease = SeedLease(now);
        for (var i = 0; i < 51; i++)
        {
            SeedRentCharge(lease, 1000m + i, now.Date.AddDays(-60 + i));
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_overdue_rent",
            argsJson: "{}",
            question: "Who is overdue?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("overdue").GetArrayLength().Should().Be(50);
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase)));
        _commands.Should().Contain(sql => sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FinancialSummaryTool_ReturnsScopedPeriodLabelsAndExcludesUnassignedBankContext()
    {
        var now = DateTime.UtcNow;
        var connection = new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Manual",
            InstitutionName = "Sample Bank",
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.BankConnections.Add(connection);
        _db.BankTransactions.Add(new BankTransaction
        {
            PortfolioId = PortfolioId,
            BankConnection = connection,
            ProviderTransactionId = "manual-deposit-1",
            PostedAt = now,
            Description = "Rent deposit",
            Amount = 1200m,
            IsoCurrencyCode = "USD",
            MatchStatus = "Unmatched",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();

        var accounting = new StubAccountingService(
            summary: new AccountingSummaryResponse
            {
                PortfolioId = PortfolioId,
                Payments = new PaymentRollup
                {
                    Collected = 2325m,
                    Outstanding = 0m,
                    Overdue = 0m,
                    OverdueCount = 0,
                },
                TotalExpenses = 63.75m,
                ExpensesByCategory =
                [
                    new ScheduleECategoryTotal
                    {
                        Category = ScheduleECategory.Repairs,
                        CategoryName = "Repairs",
                        Total = 63.75m,
                        Count = 1,
                    },
                ],
            },
            snapshot: new MoneySnapshotResponse
            {
                PortfolioId = PortfolioId,
                PeriodLabel = "June 2026 (so far)",
                PeriodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodEnd = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
                Collected = 1200m,
                Spent = 0m,
                Net = 1200m,
                PastDueAmount = 0m,
                PastDueCount = 0,
                CollectedLast30Days = 1200m,
                SpentLast30Days = 0m,
                NetLast30Days = 1200m,
            });

        var answer = await AskToolAsync(
            toolName: "get_financial_summary",
            argsJson: "{}",
            question: "How much did I collect?",
            accounting: accounting);

        using var doc = JsonDocument.Parse(answer);
        var root = doc.RootElement;

        root.GetProperty("currentLedger").GetProperty("scope").GetString()
            .Should().Contain("not limited to this month");
        root.GetProperty("currentLedger").GetProperty("collected").GetDecimal().Should().Be(2325m);
        root.GetProperty("monthToDate").GetProperty("periodLabel").GetString().Should().Be("June 2026 (so far)");
        root.GetProperty("monthToDate").GetProperty("collected").GetDecimal().Should().Be(1200m);
        root.GetProperty("last30Days").GetProperty("collected").GetDecimal().Should().Be(1200m);
        root.GetProperty("unassignedBankTransactionsExcluded").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UpcomingEventsTool_MergesSortsAndCapsAppointmentsAndInspectionsInSql()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        for (var i = 0; i < 26; i++)
        {
            _db.Appointments.Add(new Appointment
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = $"Appointment {i + 1}",
                Type = AppointmentType.MaintenanceVisit,
                Status = AppointmentStatus.Scheduled,
                ScheduledStart = now.AddMinutes(10 + (i * 2)),
                CreatedAt = now,
                UpdatedAt = now,
            });
            _db.Inspections.Add(new Inspection
            {
                PortfolioId = PortfolioId,
                Property = property,
                Type = InspectionType.Routine,
                Status = InspectionStatus.Scheduled,
                ScheduledFor = now.AddMinutes(11 + (i * 2)),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_upcoming_events",
            argsJson: """{"withinDays":2}""",
            question: "What is on my schedule?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        var events = doc.RootElement.GetProperty("events");
        events[0].GetProperty("kind").GetString().Should().Be("Appointment");
        events[1].GetProperty("kind").GetString().Should().Be("Inspection");
        _commands.Should().Contain(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task WorkOrdersTool_OrdersByRequestedAtInSql_ThenFormatsDatesForAnswer()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        _db.WorkOrders.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = "Older lock issue",
                Description = "The lock sticks.",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2),
            },
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = "Newest faucet issue",
                Description = "The faucet leaks.",
                Status = WorkOrderStatus.Scheduled,
                Priority = WorkOrderPriority.High,
                RequestedAt = now.AddDays(-1),
                ScheduledFor = now.AddDays(1),
                UpdatedAt = now.AddDays(-1),
            });
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_work_orders",
            argsJson: """{"openOnly":true}""",
            question: "What maintenance is open?");

        using var doc = JsonDocument.Parse(answer);
        var rows = doc.RootElement;
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("title").GetString().Should().Be("Newest faucet issue");
        rows[0].GetProperty("requestedAt").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");

        _commands.Should().Contain(sql => sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectedPropertyScope_FiltersRentalsMoneyAndWorkToolsInSql()
    {
        var now = DateTime.UtcNow;
        var allowed = SeedProperty(now);
        allowed.Name = "Allowed Property";
        var decoy = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Decoy Property",
            AddressLine1 = "999 Hidden Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(decoy);
        await _db.SaveChangesAsync();
        LimitScopeTo(allowed);

        _db.WorkOrders.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = allowed.Id,
                Title = "Allowed work",
                Description = "Visible",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now,
                UpdatedAt = now,
            },
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = decoy.Id,
                Title = "Decoy work",
                Description = "Hidden",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now,
                UpdatedAt = now,
            });
        _db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = PortfolioId,
                PropertyId = allowed.Id,
                Description = "Allowed expense",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Paid,
                Amount = 25m,
                IncurredAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                PropertyId = decoy.Id,
                Description = "Decoy expense",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Paid,
                Amount = 999m,
                IncurredAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _db.SaveChangesAsync();

        var propertiesJson = await AskToolAsync("list_properties", "{}", "List properties");
        using var properties = JsonDocument.Parse(propertiesJson);
        properties.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        properties.RootElement.GetProperty("properties")[0].GetProperty("name").GetString()
            .Should().Be("Allowed Property");

        var workJson = await AskToolAsync("list_work_orders", "{}", "List work");
        using var work = JsonDocument.Parse(workJson);
        work.RootElement.GetArrayLength().Should().Be(1);
        work.RootElement[0].GetProperty("title").GetString().Should().Be("Allowed work");

        var expensesJson = await AskToolAsync("list_recent_expenses", "{}", "List expenses");
        using var expenses = JsonDocument.Parse(expensesJson);
        expenses.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        expenses.RootElement.GetProperty("total").GetDecimal().Should().Be(25m);
        _commands.Should().Contain(sql =>
            sql.Contains("AuthSessions", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("MembershipRoleAssignmentProperties", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RevokedSessionScope_ReturnsNoPropertyRows()
    {
        SeedProperty(DateTime.UtcNow);
        var session = await _db.AuthSessions.SingleAsync(session => session.Id == _scope.SessionId);
        session.Status = AuthSessionStatus.Revoked;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var json = await AskToolAsync("list_properties", "{}", "List properties");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task StaleAccessRevisionScope_ReturnsNoPropertyRows()
    {
        SeedProperty(DateTime.UtcNow);
        var staleScope = _scope with { AccessRevision = _scope.AccessRevision + 1 };

        var json = await AskToolAsync("list_properties", "{}", "List properties", scope: staleScope);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ResolvedDeliveryRecipients_UseCurrentUserEmailAndPhoneWithoutOwnerFallback()
    {
        SeedProperty(DateTime.UtcNow);
        var publisher = new CapturingMessagePublisher();
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider("list_properties", "{}"),
            new ThrowingAccountingService(),
            publisher,
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance,
            TimeProvider.System,
            new TestAtomicInfrastructureUnitOfWork(_db));

        var response = await sut.AskAsync(
            _scope,
            "List properties",
            history: null,
            delivery: new QaDeliveryOptions(true, true, "portfolio-qa@example.test", "+16145550123"));

        response.DeliveredChannels.Should().BeEquivalentTo("Email", "Sms");
        publisher.Messages.Should().HaveCount(2);
        publisher.Messages.Single(message => message.Type == "email").Payload
            .Should().Contain("portfolio-qa@example.test");
        using var smsPayload = JsonDocument.Parse(
            publisher.Messages.Single(message => message.Type == "sms").Payload);
        smsPayload.RootElement.GetProperty("to").GetString()
            .Should().Be("+16145550123");
    }

    [Fact]
    public async Task AiController_ResolvesDeliveryOnlyFromAuthenticatedApplicationUser()
    {
        SeedProperty(DateTime.UtcNow);
        var user = await _db.Users.SingleAsync(user => user.Id == _scope.UserId);
        user.Email = "current-user@example.test";
        user.PhoneNumber = "+16145550999";
        await _db.SaveChangesAsync();

        var qa = new CapturingPortfolioQaService();
        var controller = new AiController(
            Mock.Of<IDailyBriefingService>(),
            qa,
            Mock.Of<IAssistantActionService>(),
            Mock.Of<IFairHousingReviewService>(),
            Mock.Of<ILlmProvider>(),
            _db,
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            _scope.SessionId,
            _scope.UserId,
            _scope.AccessContextId,
            _scope.PortfolioId,
            _scope.AccessRevision,
            WorkspaceExperience.Management,
            await _db.WorkspaceMemberships.Select(membership => (int?)membership.Id).SingleAsync(),
            WorkspaceExperience.Management);

        var result = await controller.Ask(
            new AskRequest
            {
                Question = "List properties",
                DeliverViaEmail = true,
                DeliverViaSms = true,
            },
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        qa.Delivery.Should().NotBeNull();
        qa.Delivery!.ToEmail.Should().Be("current-user@example.test");
        qa.Delivery.ToSms.Should().Be("+16145550999");
    }

    private async Task<string> AskToolAsync(
        string toolName,
        string argsJson,
        string question,
        IAccountingService? accounting = null,
        WorkspaceReadScope? scope = null)
    {
        _commands.Clear();
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider(toolName, argsJson),
            accounting ?? new ThrowingAccountingService(),
            new NoopMessagePublisher(),
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance,
            TimeProvider.System,
            new TestAtomicInfrastructureUnitOfWork(_db));

        var response = await sut.AskAsync(scope ?? _scope, question, history: null);
        response.ToolsUsed.Should().ContainSingle().Which.Should().Be(toolName);
        return response.Answer;
    }

    private WorkspaceReadScope SeedAdministratorScope()
    {
        var now = DateTime.UtcNow;
        var user = _db.Users.Single(user => user.Id == 1);
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role =>
                role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        _assignment = assignment;
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AddRange(assignment, session);
        _db.SaveChanges();
        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private void LimitScopeTo(params Property[] properties)
    {
        _assignment.ScopeKind = MembershipRoleAssignmentScopeKind.SelectedProperties;
        _assignment.SelectedProperties.Clear();
        foreach (var property in properties)
        {
            _assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                MembershipRoleAssignment = _assignment,
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
            });
        }
        _db.SaveChanges();
    }

    private LeaseFixture SeedLease(DateTime now)
    {
        var property = SeedProperty(now);
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            Property = property,
            UnitNumber = "A",
            MarketRent = 1400m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Avery",
            LastName = "Brooks",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AddRange(unit, tenant);
        _db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"MGD-A-{Guid.NewGuid():N}",
            PossessionGivenAtUtc = now.AddMonths(-1),
            PossessionAgreementExceptionReason = "Projection fixture with imported terms pending execution",
            PossessionAgreementExceptionAuthorizedByUserId = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"MGD-A-{management.Id}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 1400m,
            RentDueDay = 1,
            SecurityDepositObligation = 1400m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, now),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Canonical portfolio QA fixture",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        _db.AddRange(account, agreement, party);
        _db.SaveChanges();
        return new LeaseFixture(management, account, agreement);
    }

    private void SeedReceipt(TenantAccount account, decimal amount, DateTime effectiveAt)
    {
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(effectiveAt),
            PostedAtUtc = effectiveAt,
            Description = "Rent receipt",
            BusinessKey = $"qa-receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
    }

    private void SeedRentCharge(LeaseFixture lease, decimal amount, DateTime dueAt)
    {
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = lease.Account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueAt),
            DueOn = DateOnly.FromDateTime(dueAt),
            PostedAtUtc = dueAt,
            Description = "Rent charge",
            BusinessKey = $"qa-charge:{Guid.NewGuid():N}",
            LeaseAgreementId = lease.Agreement.Id,
            CreatedByUserId = 1,
        });
    }

    private sealed record LeaseFixture(
        LeaseManagement Management,
        TenantAccount Account,
        LeaseAgreement Agreement);

    private Property SeedProperty(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove Duplex",
            AddressLine1 = "10 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private sealed class ToolEchoLlmProvider : ILlmProvider
    {
        private readonly string _toolName;
        private readonly string _argsJson;
        private int _calls;

        public ToolEchoLlmProvider(string toolName, string argsJson)
        {
            _toolName = toolName;
            _argsJson = argsJson;
        }

        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
        {
            if (_calls++ == 0)
            {
                tools.Select(t => t.Name).Should().Contain(_toolName);
                return Task.FromResult(new LlmToolResult(
                    StopReason: "tool_use",
                    Text: null,
                    ToolCalls: [new LlmToolCall("call-1", _toolName, _argsJson)],
                    InputTokens: 0,
                    OutputTokens: 0,
                    ModelId: "fake"));
            }

            var toolResult = messages.Last(m => m.Role == "tool").Content ?? "{}";
            return Task.FromResult(new LlmToolResult(
                StopReason: "end",
                Text: toolResult,
                ToolCalls: [],
                InputTokens: 0,
                OutputTokens: 0,
                ModelId: "fake"));
        }
    }

    private sealed class CapturingMessagePublisher : IMessagePublisher
    {
        public List<(string Type, string Payload)> Messages { get; } = [];

        public Task PublishAsync<TPayload>(
            int portfolioId,
            string messageType,
            string idempotencyKey,
            TPayload payload,
            CancellationToken ct = default)
        {
            Messages.Add((messageType, JsonSerializer.Serialize(payload)));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingPortfolioQaService : IPortfolioQaService
    {
        public QaDeliveryOptions? Delivery { get; private set; }

        public Task<AskResponse> AskAsync(
            WorkspaceReadScope scope,
            string question,
            IReadOnlyList<QaTurn>? history,
            QaDeliveryOptions? delivery = null,
            CancellationToken ct = default)
        {
            Delivery = delivery;
            return Task.FromResult(new AskResponse(
                "ok", [], true, 0, "test", DeliveredChannels: null));
        }
    }

    private sealed class ThrowingAccountingService : IAccountingService
    {
        public Task<AccountingSummaryResponse> GetSummaryAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<MoneySnapshotResponse> GetSnapshotAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PastDueResponse> GetPastDueAsync(
            WorkspaceReadScope scope,
            PastDueQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AccountingReportsResponse> GetReportsAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AccountingTransactionsResponse> GetTransactionsAsync(
            WorkspaceReadScope scope,
            AccountingTransactionsQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<YearEndPacketData> GetYearEndPacketDataAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]> GetYearEndPacketAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAccountingService : IAccountingService
    {
        private readonly AccountingSummaryResponse _summary;
        private readonly MoneySnapshotResponse _snapshot;

        public StubAccountingService(AccountingSummaryResponse summary, MoneySnapshotResponse snapshot)
        {
            _summary = summary;
            _snapshot = snapshot;
        }

        public Task<AccountingSummaryResponse> GetSummaryAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<MoneySnapshotResponse> GetSnapshotAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            Task.FromResult(_snapshot);

        public Task<PastDueResponse> GetPastDueAsync(
            WorkspaceReadScope scope,
            PastDueQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AccountingReportsResponse> GetReportsAsync(WorkspaceReadScope scope, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AccountingTransactionsResponse> GetTransactionsAsync(
            WorkspaceReadScope scope,
            AccountingTransactionsQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<YearEndPacketData> GetYearEndPacketDataAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<byte[]> GetYearEndPacketAsync(WorkspaceReadScope scope, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopMessagePublisher : IMessagePublisher
    {
        public Task PublishAsync<TPayload>(int portfolioId, string messageType, string idempotencyKey, TPayload payload, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyKnowledgeBaseService : IKnowledgeBaseService
    {
        public IReadOnlyList<KbArticleSummary> ListArticles() => [];
        public KbArticle? GetArticle(string slug) => null;
        public IReadOnlyList<KbSnippet> Search(string query, int max) => [];
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public RecordingCommandInterceptor(List<string> commands) => _commands = commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class PortfolioQaTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
    {
        public PortfolioQaTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
    }
}
