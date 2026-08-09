using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PortfolioGettingStartedSignalsPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private const int SeededUserId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<CapturedCommand> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private PortfolioService _service = null!;
    private int _otherUserId;

    public PortfolioGettingStartedSignalsPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);
        var now = DateTime.UtcNow;
        var otherUser = new ApplicationUser
        {
            UserName = "getting-started-other-user@example.test",
            NormalizedUserName = "GETTING-STARTED-OTHER-USER@EXAMPLE.TEST",
            Email = "getting-started-other-user@example.test",
            NormalizedEmail = "GETTING-STARTED-OTHER-USER@EXAMPLE.TEST",
            DisplayName = "Getting Started Other User",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        _context.Db.Users.Add(otherUser);
        await _context.Db.SaveChangesAsync();
        _otherUserId = otherUser.Id;
        _service = new PortfolioService(_context.Db, Mock.Of<IAtomicUnitOfWork>());
        _commands.Clear();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task NotificationSignal_IsPerUser_AndUsesOneServerSideProjection()
    {
        await SetPreferenceAsync(SeededUserId, enableEmail: true);
        await AssertSignalAndOneQueryAsync(SeededUserId, expected: true);

        await SetPreferenceAsync(SeededUserId, enableEmail: false);
        await AssertSignalAndOneQueryAsync(SeededUserId, expected: false);

        await RemovePreferencesAsync();
        await SetPreferenceAsync(_otherUserId, enableEmail: true);
        await AssertSignalAndOneQueryAsync(SeededUserId, expected: false);
        await AssertSignalAndOneQueryAsync(_otherUserId, expected: true);

        await RemovePreferencesAsync();
        await AssertSignalAndOneQueryAsync(_otherUserId, expected: false);
    }

    private async Task AssertSignalAndOneQueryAsync(int userId, bool expected)
    {
        _commands.Clear();
        var result = await _service.GetGettingStartedSignalsAsync(PortfolioId, userId);

        result.Should().NotBeNull();
        result!.HasNotificationEmail.Should().Be(expected);
        _commands.Should().ContainSingle();
        var command = _commands.Single();
        command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        command.Sql.Should().Contain("\"UserAlertPreferences\"");
        command.Sql.Should().MatchRegex("\"UserId\"\\s*=\\s*@userId\\b");
        command.Sql.Should().Contain("EXISTS");
        command.Parameters
            .Select(parameter => parameter.Value)
            .Should().Contain(userId);
    }

    private async Task SetPreferenceAsync(int userId, bool enableEmail)
    {
        await RemovePreferencesAsync();
        var now = DateTime.UtcNow;
        _context.Db.UserAlertPreferences.Add(new UserAlertPreference
        {
            PortfolioId = PortfolioId,
            UserId = userId,
            EnableInApp = true,
            EnableMobilePush = true,
            EnableEmail = enableEmail,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task RemovePreferencesAsync()
    {
        _context.Db.UserAlertPreferences.RemoveRange(_context.Db.UserAlertPreferences);
        await _context.Db.SaveChangesAsync();
    }

    private sealed record CapturedParameter(string Name, object? Value);

    private sealed record CapturedCommand(string Sql, IReadOnlyList<CapturedParameter> Parameters);

    private sealed class QueryRecorder(List<CapturedCommand> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command)
        {
            commands.Add(new CapturedCommand(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new CapturedParameter(parameter.ParameterName, parameter.Value))
                    .ToArray()));
        }
    }
}
