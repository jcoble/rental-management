using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
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
}
