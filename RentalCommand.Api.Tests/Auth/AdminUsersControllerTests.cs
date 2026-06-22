using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Auth;

public class AdminUsersControllerTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;

    public AdminUsersControllerTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _ctx.Db.Portfolios.Add(new Portfolio
        {
            Id = 2,
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ctx.Db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPage_ReturnsSqlCountAndRequestedWindow()
    {
        SeedMembers("alpha@example.local", "bravo@example.local", "charlie@example.local", "delta@example.local");
        SeedMember("aaa-foreign@example.local", portfolioId: 2);

        var controller = CreateController();

        _commands.Clear();
        var result = await controller.ListPage(new ListQuery
        {
            Sort = "email",
            Skip = 1,
            Take = 2,
        }, CancellationToken.None);

        result.Value.Should().NotBeNull();
        var page = result.Value!;
        page.TotalCount.Should().Be(4);
        page.Skip.Should().Be(1);
        page.Take.Should().Be(2);
        page.Items.Select(m => m.Email).Should().Equal("bravo@example.local", "charlie@example.local");

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"UserAccounts\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("\"Email\"", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    private AdminUsersController CreateController() => new(
        CreateUserManager(),
        _ctx.Db,
        NullLogger<AdminUsersController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("portfolioId", PortfolioId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, "7"),
                    new Claim(ClaimTypes.Role, "Admin"),
                ], "test")),
            },
        },
    };

    private static UserManager<ApplicationUser> CreateUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!).Object;
    }

    private void SeedMembers(params string[] emails)
    {
        foreach (var email in emails)
        {
            SeedMember(email, PortfolioId);
        }
        _ctx.Db.SaveChanges();
    }

    private void SeedMember(string email, int portfolioId)
    {
        var now = DateTime.UtcNow;
        _ctx.Db.UserAccounts.Add(new UserAccount
        {
            PortfolioId = portfolioId,
            Email = email,
            DisplayName = email.Split('@')[0],
            PasswordHash = string.Empty,
            Role = UserRole.Manager,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
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
