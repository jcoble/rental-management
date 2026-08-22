using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection4.Name)]
public sealed class OwnerCutoverPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const string PreCutoverMigration = "20260716140000_AddWorkspaceLlmCredentials";
    private const string CutoverMigration = "20260716160000_RemoveLegacyOwnerAndRouteAliases";
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public OwnerCutoverPostgreSqlTests(
        MigratedPostgreSqlFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task OwnershipQueries_PageInPostgreSql()
    {
        var scope = await SeedAdministratorScopeAsync();
        await _context.ActivateApiScopeAsync(scope);
        var now = DateTime.UtcNow;
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie" })
        {
            var owner = new OwnerEntity
            {
                PortfolioId = PortfolioId,
                Name = $"Cutover Paging Owner {name}",
                Email = $"{name.ToLowerInvariant()}-owner@example.test",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var property = Property(null, $"{name} Cutover Property", now);
            property.PortfolioId = PortfolioId;
            property.Ownerships.Add(new PropertyOwnership
            {
                PortfolioId = PortfolioId,
                OwnerEntity = owner,
                OwnershipSharePercent = 100m,
                EffectiveFromUtc = now.AddDays(-1),
                StatementRecipientName = $"{name} Statements",
                StatementRecipientEmail = $"{name.ToLowerInvariant()}-statements@example.test",
                PayeeName = $"{name} Payee LLC",
            });
            _context.Db.Properties.Add(property);
        }
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var service = new PropertyService(
            _context.Db,
            Mock.Of<IDataUpdateService>(),
            TimeProvider.System);
        _commands.Clear();

        var page = await service.ListPageAsync(scope, new PropertyListQuery
        {
            Search = "Cutover Paging Owner",
            Sort = "-name",
            Skip = 1,
            Take = 1,
        });

        page.TotalCount.Should().Be(3);
        page.Items.Should().ContainSingle(item => item.Name == "Bravo Cutover Property");
        page.Items[0].Ownerships.Should().ContainSingle(ownership =>
            ownership.OwnerName == "Cutover Paging Owner Bravo"
            && ownership.OwnershipSharePercent == 100m
            && ownership.StatementRecipientName == "Bravo Statements"
            && ownership.PayeeName == "Bravo Payee LLC");
        _commands.Should().HaveCount(
            2,
            "authorization, ownership filtering, aggregation, sorting, and paging stay in the translated count/page statements");

        for (var index = 0; index < _commands.Count; index++)
        {
            CaptureSql($"OWNERSHIP_PAGE_{index + 1}", _commands[index]);
        }

        var countSql = _commands.Single(IsTopLevelCountCommand);
        countSql.Should().Contain("rc_api_effective_capability_scopes");
        countSql.Should().Contain("PropertyOwnerships");
        countSql.Should().Contain("ILIKE");
        countSql.Should().Contain("WHERE");
        countSql.Should().ContainEquivalentOf("join");

        var pageSql = _commands.Single(IsBoundedPageCommand);
        pageSql.Should().Contain("rc_api_effective_capability_scopes");
        pageSql.Should().Contain("PropertyOwnerships");
        pageSql.Should().Contain("OwnerEntities");
        pageSql.Should().Contain("ILIKE");
        pageSql.Should().Contain("WHERE");
        pageSql.Should().ContainEquivalentOf("join");
        pageSql.Should().Contain("ORDER BY");
        pageSql.Should().Contain("LIMIT");
        pageSql.Should().Contain("OFFSET");
        pageSql.Should().ContainEquivalentOf("COUNT", "unit aggregation must stay in the page SQL");
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var now = DateTime.UtcNow;
        var email = $"owner-cutover-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Owner cutover verifier",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
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
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        return new WorkspaceReadScope(
            PortfolioId,
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision);
    }

    private static Property Property(Portfolio? portfolio, string name, DateTime now) => new()
    {
        Portfolio = portfolio,
        Name = name,
        AddressLine1 = $"{name} Street",
        City = "Columbus",
        State = "OH",
        PostalCode = "43215",
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        NpgsqlConnection connection,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private void CaptureSql(string label, string sql)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine(sql);
    }

    private static bool IsTopLevelCountCommand(string sql) =>
        sql.TrimStart().StartsWith("SELECT count(*)", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedPageCommand(string sql) =>
        !IsTopLevelCountCommand(sql)
        && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
        && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase);

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
