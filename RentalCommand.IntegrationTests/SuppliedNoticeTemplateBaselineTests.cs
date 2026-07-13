using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data;
using RentalCommand.Data.Notifications;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// PostgreSQL proof that supplied notice content is migration-owned and concurrent workspace
/// bootstraps converge on one immutable v1 copy per system key. SQLite cannot prove ON CONFLICT or
/// transaction participation.
/// </summary>
public sealed class SuppliedNoticeTemplateBaselineTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private bool _dockerAvailable;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_supplied_notices")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _connectionString = _postgres.GetConnectionString();
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SeedSuppliedTemplates_ConcurrentCalls_CreateExactlyOneExactWorkspaceCopy()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice baseline proof skipped.");

        int portfolioId;
        int actorUserId;
        await using (var setup = NewContext())
        {
            var now = DateTime.UtcNow;
            var actor = new ApplicationUser
            {
                UserName = "template-owner@example.test",
                NormalizedUserName = "TEMPLATE-OWNER@EXAMPLE.TEST",
                Email = "template-owner@example.test",
                NormalizedEmail = "TEMPLATE-OWNER@EXAMPLE.TEST",
                DisplayName = "Template Owner",
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Template Workspace",
                ManagementCompanyName = "Template Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.Users.Add(actor);
            setup.Portfolios.Add(portfolio);
            await setup.SaveChangesAsync();
            portfolioId = portfolio.Id;
            actorUserId = actor.Id;
        }

        const int callers = 8;
        await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext();
            var service = new NotificationFoundationService(db, TimeProvider.System);
            await service.SeedSuppliedTemplatesAsync(portfolioId, actorUserId, CancellationToken.None);
        })));

        await using var verify = NewContext();
        var workspaceCount = await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            .CountAsync(row => row.PortfolioId == portfolioId);
        var mismatchedCopies = await (
            from workspace in verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            join system in verify.SystemNoticeTemplateVersions.AsNoTracking()
                on workspace.BasedOnSystemTemplateVersionId equals system.Id
            where workspace.PortfolioId == portfolioId &&
                (workspace.Version != SuppliedNoticeTemplateBaseline.Version ||
                 workspace.SystemKey != system.SystemKey ||
                 workspace.Subject != system.Subject ||
                 workspace.Body != system.Body ||
                 workspace.JurisdictionCode != system.JurisdictionCode ||
                 workspace.IsCustomized ||
                 workspace.CreatedByUserId != actorUserId)
            select workspace.Id).CountAsync();

        workspaceCount.Should().Be(SuppliedNoticeTemplateBaseline.V1.Count);
        mismatchedCopies.Should().Be(0);
    }

    [SkippableFact]
    public async Task SeedSuppliedTemplates_RollsBackWithCallerTransaction()
    {
        Skip.IfNot(_dockerAvailable, "Docker is unavailable; PostgreSQL notice baseline proof skipped.");

        int portfolioId;
        int actorUserId;
        await using (var setup = NewContext())
        {
            var now = DateTime.UtcNow;
            var actor = new ApplicationUser
            {
                UserName = "rollback-owner@example.test",
                NormalizedUserName = "ROLLBACK-OWNER@EXAMPLE.TEST",
                Email = "rollback-owner@example.test",
                NormalizedEmail = "ROLLBACK-OWNER@EXAMPLE.TEST",
                DisplayName = "Rollback Owner",
                CreatedAt = now,
            };
            var portfolio = new Portfolio
            {
                Name = "Rollback Workspace",
                ManagementCompanyName = "Rollback Workspace",
                Status = PortfolioStatus.Active,
                Currency = "USD",
                CreatedAt = now,
                UpdatedAt = now,
            };
            setup.Users.Add(actor);
            setup.Portfolios.Add(portfolio);
            await setup.SaveChangesAsync();
            portfolioId = portfolio.Id;
            actorUserId = actor.Id;
        }

        await using (var command = NewContext())
        await using (var transaction = await command.Database.BeginTransactionAsync())
        {
            var service = new NotificationFoundationService(command, TimeProvider.System);
            await service.SeedSuppliedTemplatesAsync(portfolioId, actorUserId, CancellationToken.None);
            (await command.WorkspaceNoticeTemplateVersions.AsNoTracking()
                .CountAsync(row => row.PortfolioId == portfolioId))
                .Should().Be(SuppliedNoticeTemplateBaseline.V1.Count);
            await transaction.RollbackAsync();
        }

        await using var verify = NewContext();
        (await verify.WorkspaceNoticeTemplateVersions.AsNoTracking()
            .CountAsync(row => row.PortfolioId == portfolioId)).Should().Be(0);
    }

    private RentalCommandDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new RentalCommandDbContext(options);
    }
}
