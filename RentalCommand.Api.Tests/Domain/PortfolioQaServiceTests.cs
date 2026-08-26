using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name1)]
public sealed class PortfolioQaServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private WorkspaceReadScope _scope;
    private MembershipRoleAssignment _assignment = null!;

    public PortfolioQaServiceTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync(
            [new RecordingCommandInterceptor(_commands)]);
        _db = _context.Db;
        _scope = SeedAdministratorScope();
    }

    public async Task DisposeAsync()
    {
        await ResetApiScopeAsync();
        await _context.DisposeAsync();
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
                OperationalScope = ExpenseOperationalScope.Property,
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
    public async Task RecentExpensesTool_UsesPaidCashBasisForRowsWindowAndTotal()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        _db.Expenses.AddRange(
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                Property = property,
                Description = "Paid repair",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Paid,
                Amount = 25m,
                IncurredAt = now.AddDays(-120),
                PaidAt = now.AddDays(-1),
                CreatedAt = now,
                UpdatedAt = now,
            },
            new Expense
            {
                PortfolioId = PortfolioId,
                OperationalScope = ExpenseOperationalScope.Property,
                Property = property,
                Description = "Pending repair",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Pending,
                Amount = 500m,
                IncurredAt = now.AddDays(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_recent_expenses",
            argsJson: """{"withinDays":90}""",
            question: "What did I spend on repairs recently?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("total").GetDecimal().Should().Be(25m);
        var expense = doc.RootElement.GetProperty("expenses").EnumerateArray().Single();
        expense.GetProperty("description").GetString().Should().Be("Paid repair");
        expense.GetProperty("status").GetString().Should().Be(nameof(ExpenseStatus.Paid));
        expense.GetProperty("date").GetString().Should().Be(now.AddDays(-1).ToString("yyyy-MM-dd"));
        _commands.Should().HaveCount(3);
        _commands.Should().OnlyContain(sql =>
            sql.Contains("PaidAt", StringComparison.Ordinal)
            && sql.Contains("IncurredAt", StringComparison.Ordinal)
            && sql.Contains("Status", StringComparison.Ordinal));
        _commands.Should().Contain(sql =>
            sql.Contains("PaidAt", StringComparison.Ordinal)
            && sql.Contains("Status", StringComparison.Ordinal)
            && sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
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
    public async Task OverdueRentTool_ExcludesReturnedPossessionLeaseRelationshipCharges()
    {
        var now = DateTime.UtcNow;
        var current = SeedLease(now);
        var stale = SeedLease(now);
        current.Agreement.AgreementNumber = "CURRENT-1";
        stale.Agreement.AgreementNumber = "STALE-1";
        stale.Agreement.TermEndOn = DateOnly.FromDateTime(now.AddDays(-1));
        SeedRentCharge(current, 1400m, now.AddDays(-5));
        SeedRentCharge(stale, 1400m, now.AddDays(-30));
        await _db.SaveChangesAsync();

        var returnedAtUtc = now.AddDays(-1);
        stale.Management.PossessionReturnedAtUtc = returnedAtUtc;
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
        overdue[0].GetProperty("leaseNumber").GetString().Should().Be(current.Account.AccountNumber);
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
                OperationalScope = ExpenseOperationalScope.Property,
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
                OperationalScope = ExpenseOperationalScope.Property,
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
            sql.Contains(
                "public.rc_api_effective_capability_scopes",
                StringComparison.OrdinalIgnoreCase));
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
        var atomic = new CapturingPortfolioQaAtomicUnitOfWork(
            new PortfolioQaDeliveryResult(["Email", "Sms"]));
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider("list_properties", "{}"),
            new ThrowingAccountingService(),
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance,
            TimeProvider.System,
            atomic);

        await ResetApiScopeAsync();
        await _context.ActivateApiScopeAsync(_scope);
        var response = await sut.AskAsync(
            _scope,
            "List properties",
            history: null,
            delivery: new QaDeliveryOptions(true, true, "portfolio-qa@example.test", "+16145550123"),
            deliveryOperationId: "portfolio-qa-delivery-test");

        response.DeliveredChannels.Should().BeEquivalentTo("Email", "Sms");
        var command = atomic.Command.Should().BeOfType<PortfolioQaDeliveryCommand>().Subject;
        command.ToEmail.Should().Be("portfolio-qa@example.test");
        command.ToSms.Should().Be("+16145550123");
        command.ActorUserId.Should().Be(_scope.UserId);
        command.ActorAuthSessionId.Should().Be(_scope.SessionId);
        command.ActorAccessContextId.Should().Be(_scope.AccessContextId);
        command.ActorAccessRevision.Should().Be(_scope.AccessRevision);
        atomic.Identity!.CommandType.Should().Be("portfolio.qa.delivery");
        atomic.Identity.IdempotencyKey.Should().Be($"{PortfolioId}:portfolio-qa-delivery-test");
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
        await ResetApiScopeAsync();
        await _context.ActivateApiScopeAsync(_scope);
        using var requestServices = new ServiceCollection()
            .AddSingleton<IWorkspaceAuthorizationEvaluator>(new WorkspaceAuthorizationEvaluator(_db))
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = requestServices,
        };
        var controller = new AiController(
            Mock.Of<IDailyBriefingService>(),
            qa,
            Mock.Of<IAssistantActionService>(),
            Mock.Of<IFairHousingReviewService>(),
            Mock.Of<ILlmProvider>(),
            _db,
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
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
            "qa-controller-delivery",
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        qa.Delivery.Should().NotBeNull();
        qa.Delivery!.ToEmail.Should().Be("current-user@example.test");
        qa.Delivery.ToSms.Should().Be("+16145550999");
        qa.DeliveryOperationId.Should().Be("qa-controller-delivery");
    }

    private async Task<string> AskToolAsync(
        string toolName,
        string argsJson,
        string question,
        IAccountingService? accounting = null,
        WorkspaceReadScope? scope = null)
    {
        var effectiveScope = scope ?? _scope;
        await ResetApiScopeAsync();
        await _context.ActivateApiScopeAsync(effectiveScope);
        _commands.Clear();
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider(toolName, argsJson),
            accounting ?? new ThrowingAccountingService(),
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance,
            TimeProvider.System,
            new CapturingPortfolioQaAtomicUnitOfWork(new PortfolioQaDeliveryResult([])));

        var response = await sut.AskAsync(effectiveScope, question, history: null);
        response.ToolsUsed.Should().ContainSingle().Which.Should().Be(toolName);
        return response.Answer;
    }

    private async Task ResetApiScopeAsync()
    {
        if (_db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await _db.Database.ExecuteSqlRawAsync("""
            RESET SESSION AUTHORIZATION;
            SELECT set_config('app.current_portfolio_id', '', false),
                   set_config('app.auth_session_id', '', false),
                   set_config('app.current_user_id', '', false),
                   set_config('app.current_access_context_id', '', false),
                   set_config('app.access_revision', '', false);
            """);
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

    private sealed class CapturingPortfolioQaService : IPortfolioQaService
    {
        public QaDeliveryOptions? Delivery { get; private set; }
        public string? DeliveryOperationId { get; private set; }

        public Task<AskResponse> AskAsync(
            WorkspaceReadScope scope,
            string question,
            IReadOnlyList<QaTurn>? history,
            QaDeliveryOptions? delivery = null,
            string? deliveryOperationId = null,
            CancellationToken ct = default)
        {
            Delivery = delivery;
            DeliveryOperationId = deliveryOperationId;
            return Task.FromResult(new AskResponse(
                "ok", [], true, 0, "test", DeliveredChannels: null));
        }
    }

    private sealed class CapturingPortfolioQaAtomicUnitOfWork : IWriteExecutor
    {
        private readonly object _result;

        public CapturingPortfolioQaAtomicUnitOfWork(object result) => _result = result;

        public AtomicCommandIdentity? Identity { get; private set; }
        public object? Command { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            string idempotencyKey,
            TransactionalWrite<TCommand, TResult> write,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            Identity = new AtomicCommandIdentity(write.OperationName, idempotencyKey);
            Command = write.Request;
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)_result,
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()));
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

}
