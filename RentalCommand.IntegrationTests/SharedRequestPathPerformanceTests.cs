using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using RentalCommand.Api.Auth;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SharedRequestPathPerformanceCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Shared request-path performance PostgreSQL";
}

[Collection(SharedRequestPathPerformanceCollection.Name)]
public sealed class SharedRequestPathPerformanceTests
{
    private readonly MigratedPostgreSqlFixture _fixture;

    public SharedRequestPathPerformanceTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task UnitDashboard_NotFoundRequestPath_RecordsInformationalTimingAndQueryCount()
    {
        var commands = new List<string>();
        await using var context = await _fixture.CreateContextAsync([new QueryCounter(commands)]);
        var service = new UnitDashboardService(
            context.Db, new AuditDescriber(), new AuditDiffBuilder(), TimeProvider.System);

        commands.Clear();
        var stopwatch = Stopwatch.StartNew();
        var dashboard = await service.GetDashboardAsync(1, int.MaxValue, CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(dashboard);
        Assert.Single(commands);
        Assert.Contains("SELECT", commands[0], StringComparison.OrdinalIgnoreCase);

        var output = Environment.GetEnvironmentVariable("FOUNDATION_PERF_EVIDENCE_OUTPUT")
            ?? "Docs/Testing/Results/2026-07-16-foundation-parallel/L15-performance-evidence.md";
        output = ResolveArtifactPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.AppendAllTextAsync(output, $"""

            ## Informational request-path sample

            - Path: `UnitDashboardService.GetDashboardAsync` not-found request
            - Elapsed: {stopwatch.Elapsed.TotalMilliseconds:F3} ms
            - PostgreSQL statements: {commands.Count}
            - Query count is informational only; no endpoint tuning or forced query-count gate was applied.
            """);
    }

    [Fact]
    public async Task TenantAccountDeposits_FirstPage_RecordsSeparatedCountPageSqlAndPlans()
    {
        var commands = new List<CapturedCommand>();
        await using var context = await _fixture.CreateContextAsync([new QueryRecorder(commands)]);
        var scope = await SeedAuthenticatedScopeAsync(context);
        var service = new TenantAccountQueryService(context.Db, TimeProvider.System);
        var controller = new TenantAccountsController(
            service,
            Mock.Of<ITenantAccountMoveOutStatementService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.HttpContext.Items[CanonicalAccessContextHttpItem.Key] = new ActiveAccessContext(
            scope.SessionId,
            scope.UserId,
            scope.AccessContextId,
            scope.PortfolioId,
            scope.AccessRevision,
            WorkspaceExperience.Management,
            WorkspaceMembershipId: null,
            DefaultExperience: WorkspaceExperience.Management);

        commands.Clear();
        var capturedAtUtc = DateTimeOffset.UtcNow;
        var request = Stopwatch.StartNew();
        var response = await controller.DepositsPage(
            new TenantAccountDepositListQuery
            {
                Skip = 0,
                Take = 20,
                Sort = "-createdAtUtc",
            },
            CancellationToken.None);
        request.Stop();

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.IsType<TenantAccountDepositPageResponse>(ok.Value);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        Assert.Equal(2, commands.Count);
        Assert.Contains("count(*)", commands[0].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORDER BY", commands[0].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", commands[1].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", commands[1].Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(commands[0].CompletedAtUtc <= commands[1].StartedAtUtc,
            "the DB-side count must complete before the deterministic DB-side page statement starts");

        var plans = await ExplainAsync(context.ConnectionString, commands);
        await WriteRequestEvidenceAsync(capturedAtUtc, request.Elapsed, commands);
        await WriteSqlEvidenceAsync(commands);
        await WriteExplainEvidenceAsync(capturedAtUtc, commands, plans);
    }

    private static async Task<WorkspaceReadScope> SeedAuthenticatedScopeAsync(
        MigratedPostgreSqlTestContext context)
    {
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = "deposits-performance@example.test",
            NormalizedUserName = "DEPOSITS-PERFORMANCE@EXAMPLE.TEST",
            Email = "deposits-performance@example.test",
            NormalizedEmail = "DEPOSITS-PERFORMANCE@EXAMPLE.TEST",
            DisplayName = "Deposits Performance",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = 1,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = 1,
            RoleProfileId = AccessCatalog.Roles
                .Single(role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
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

        context.Db.AddRange(assignment, session);
        await context.Db.SaveChangesAsync();
        return new WorkspaceReadScope(
            1, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        string connectionString,
        IReadOnlyList<CapturedCommand> commands)
    {
        var plans = new List<string>(commands.Count);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var captured in commands)
        {
            await using var command = new NpgsqlCommand(
                $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {captured.Sql}",
                connection);
            foreach (var parameter in captured.Parameters)
            {
                command.Parameters.Add(new NpgsqlParameter
                {
                    ParameterName = parameter.Name,
                    Value = parameter.Value ?? DBNull.Value,
                });
            }

            plans.Add((string)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("PostgreSQL returned no JSON plan.")));
        }

        return plans;
    }

    private static async Task WriteRequestEvidenceAsync(
        DateTimeOffset capturedAtUtc,
        TimeSpan requestDuration,
        IReadOnlyList<CapturedCommand> commands)
    {
        var output = ResolveArtifactPath(
            Environment.GetEnvironmentVariable("FOUNDATION_PERF_EVIDENCE_OUTPUT")
            ?? "Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/request-path.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, $"""
            # Step 5 isolated deposits request-path capture

            - Capture time UTC: `{capturedAtUtc:O}`
            - Authenticated action: `GET /api/v1/tenant-accounts/deposits/page?skip=0&take=20`
            - Result: `200`
            - Request/action duration: `{requestDuration.TotalMilliseconds:F3} ms`
            - 401/token-refresh replay: `none observed`; no replay duration is combined with the authenticated 200 request.
            - PostgreSQL statements: `{commands.Count}`, executed sequentially as DB-side count then deterministic DB-side page.

            | Sequence | Shape | Started UTC | Completed UTC | Duration ms |
            |---:|---|---|---|---:|
            {string.Join(Environment.NewLine, commands.Select((command, index) =>
                $"| {index + 1} | {(index == 0 ? "count" : "page")} | {command.StartedAtUtc:O} | {command.CompletedAtUtc:O} | {command.Duration.TotalMilliseconds:F3} |"))}

            This isolated PostgreSQL capture supplies correlated SQL timing only. The deployed
            API/APK identity and ten UI-driven emulator samples are intentionally pending the
            contract's separate real-emulator verifier.
            """);
    }

    private static async Task WriteSqlEvidenceAsync(IReadOnlyList<CapturedCommand> commands)
    {
        var output = ResolveArtifactPath(
            Environment.GetEnvironmentVariable("FOUNDATION_PERF_SQL_OUTPUT")
            ?? "Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/generated-sql-and-bound-parameters.md");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var sections = commands.Select((command, index) => $"""
            ## Statement {index + 1}: {(index == 0 ? "DB-side count" : "deterministic DB-side page")}

            Duration: `{command.Duration.TotalMilliseconds:F3} ms`

            Bound parameters:

            {string.Join(Environment.NewLine, command.Parameters.Select(parameter =>
                $"- `{parameter.Name}` = `{FormatParameter(parameter.Value)}`"))}

            ```sql
            {command.Sql}
            ```
            """);
        await File.WriteAllTextAsync(output, $"""
            # Generated deposits SQL and bound parameters

            {string.Join($"{Environment.NewLine}{Environment.NewLine}", sections)}
            """);
    }

    private static async Task WriteExplainEvidenceAsync(
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<CapturedCommand> commands,
        IReadOnlyList<string> plans)
    {
        var output = ResolveArtifactPath(
            Environment.GetEnvironmentVariable("FOUNDATION_PERF_EXPLAIN_OUTPUT")
            ?? "Docs/Testing/Results/2026-07-25-tsk-750-step-5-deposits-performance/explain-analyze-buffers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var payload = new
        {
            captureTimeUtc = capturedAtUtc,
            statements = plans.Select((plan, index) => new
            {
                sequence = index + 1,
                shape = index == 0 ? "count" : "page",
                durationMs = commands[index].Duration.TotalMilliseconds,
                plan = JsonDocument.Parse(plan).RootElement.Clone(),
            }),
        };
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FormatParameter(object? value) =>
        value switch
        {
            null or DBNull => "NULL",
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O"),
            string[] values => $"[{string.Join(", ", values)}]",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                 ?? string.Empty,
        };

    private static string ResolveArtifactPath(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            path);
    }

    private sealed class QueryCounter(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record CapturedParameter(string Name, object? Value);

    private sealed record CapturedCommand(
        string Sql,
        IReadOnlyList<CapturedParameter> Parameters,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        TimeSpan Duration);

    private sealed class QueryRecorder(List<CapturedCommand> commands) : DbCommandInterceptor
    {
        private readonly Dictionary<Guid, PendingCommand> _pending = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CaptureStart(command, eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CaptureStart(command, eventData);
            return ValueTask.FromResult(result);
        }

        public override DbDataReader ReaderExecuted(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result)
        {
            CaptureEnd(eventData);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            CaptureEnd(eventData);
            return ValueTask.FromResult(result);
        }

        private void CaptureStart(DbCommand command, CommandEventData eventData)
        {
            _pending.Add(eventData.CommandId, new PendingCommand(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new CapturedParameter(
                        parameter.ParameterName,
                        parameter.Value))
                    .ToArray(),
                DateTimeOffset.UtcNow));
        }

        private void CaptureEnd(CommandExecutedEventData eventData)
        {
            var pending = _pending[eventData.CommandId];
            commands.Add(new CapturedCommand(
                pending.Sql,
                pending.Parameters,
                pending.StartedAtUtc,
                DateTimeOffset.UtcNow,
                eventData.Duration));
            _pending.Remove(eventData.CommandId);
        }

        private sealed record PendingCommand(
            string Sql,
            IReadOnlyList<CapturedParameter> Parameters,
            DateTimeOffset StartedAtUtc);
    }
}
