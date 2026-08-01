using System.Data.Common;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using Xunit.Abstractions;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class WorkspaceLlmCredentialPostgreSqlTests : IAsyncLifetime
{
    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public WorkspaceLlmCredentialPostgreSqlTests(
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
    public async Task CredentialCommands_AreAuthorizedAndWriteOnly()
    {
        var portfolioId = await CreatePortfolioAsync("credential-write-only");
        var service = CreateService();
        var access = Access(portfolioId);

        var status = await service.ActivateAsync(
            access,
            new ActivateAiCredentialRequest(
                "openai",
                "gpt-4o",
                "sk-do-not-return")
            {
                ClientOperationId = "credential-write-only-activate",
            });

        status.Configured.Should().BeTrue();
        status.Provider.Should().Be("openai");
        status.ToString().Should().NotContain("sk-do-not-return");

        var stored = await _context.Db.WorkspaceLlmCredentials
            .IgnoreQueryFilters()
            .SingleAsync(row => row.PortfolioId == portfolioId);
        stored.ApiKeyCipherText.Should().NotBe("sk-do-not-return");
        stored.ApiKeyCipherText.Should().NotContain("sk-do-not-return");

        var statusSql = _context.Db.WorkspaceLlmCredentials
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
            .Select(row => new
            {
                row.Provider,
                row.ModelId,
                row.LastTestedAtUtc,
                row.UpdatedAtUtc,
            })
            .ToQueryString();
        CaptureSql("CREDENTIAL_STATUS_WRITE_ONLY", statusSql);
        statusSql.Should().NotContain("ApiKeyCipherText");
    }

    [Fact]
    public async Task CredentialCommands_UseStableSecretIntentDigestAcrossRandomizedEncryption()
    {
        var portfolioId = await CreatePortfolioAsync("credential-stable-intent");
        var atomic = new CapturingCredentialAtomicUnitOfWork();
        var service = CreateService(
            atomic,
            new RandomizedTestDataProtectionProvider());
        var access = Access(portfolioId);

        await service.ActivateAsync(
            access,
            new ActivateAiCredentialRequest("openai", "gpt-4o", "sk-same-secret")
            {
                ClientOperationId = "credential-stable-intent-first",
            });
        await service.ActivateAsync(
            access,
            new ActivateAiCredentialRequest("openai", "gpt-4o", "sk-same-secret")
            {
                ClientOperationId = "credential-stable-intent-retry",
            });
        await service.ActivateAsync(
            access,
            new ActivateAiCredentialRequest("openai", "gpt-4o", "sk-changed-secret")
            {
                ClientOperationId = "credential-stable-intent-changed",
            });

        var first = atomic.Commands[0].Should().BeOfType<ActivateWorkspaceLlmCredentialCommand>().Which;
        var retry = atomic.Commands[1].Should().BeOfType<ActivateWorkspaceLlmCredentialCommand>().Which;
        var changed = atomic.Commands[2].Should().BeOfType<ActivateWorkspaceLlmCredentialCommand>().Which;

        retry.ApiKeyIntentDigest.Should().Be(first.ApiKeyIntentDigest);
        retry.ApiKeyCipherText.Should().NotBe(first.ApiKeyCipherText);
        changed.ApiKeyIntentDigest.Should().NotBe(first.ApiKeyIntentDigest);
    }

    [Fact]
    public async Task RuntimeResolution_IsWorkspaceScoped()
    {
        var firstId = await CreatePortfolioAsync("runtime-first");
        var secondId = await CreatePortfolioAsync("runtime-second");
        var service = CreateService();

        await service.ActivateAsync(
            Access(firstId),
            new ActivateAiCredentialRequest("openai", "gpt-4o", "first-secret")
            {
                ClientOperationId = "runtime-first-activate",
            });
        await service.ActivateAsync(
            Access(secondId),
            new ActivateAiCredentialRequest(
                "anthropic",
                "claude-3-5-sonnet-latest",
                "second-secret")
            {
                ClientOperationId = "runtime-second-activate",
            });

        var first = await service.ResolveActiveAsync(firstId);
        var second = await service.ResolveActiveAsync(secondId);

        first.Should().NotBeNull();
        first!.PortfolioId.Should().Be(firstId);
        first.Provider.Should().Be("openai");
        first.ApiKey.Should().Be("first-secret");
        second.Should().NotBeNull();
        second!.PortfolioId.Should().Be(secondId);
        second.Provider.Should().Be("anthropic");
        second.ApiKey.Should().Be("second-secret");

        await service.RecordUsageAsync(
            firstId,
            first.Provider,
            first.ModelId,
            "scan.extraction",
            420,
            120,
            30,
            0.00021m,
            usageEventIdentity: "scan:first:attempt:1");
        await service.RecordUsageAsync(
            firstId,
            first.Provider,
            first.ModelId,
            "scan.extraction",
            180,
            80,
            20,
            0.00009m,
            usageEventIdentity: "scan:first:attempt:2");
        await service.RecordUsageAsync(
            secondId,
            second.Provider,
            second.ModelId,
            "scan.extraction",
            250,
            200,
            50,
            0.00045m,
            usageEventIdentity: "scan:second:attempt:1");

        var usageAggregationQuery = _context.Db.LlmUsageEvidence
            .AsNoTracking()
            .Where(row => row.PortfolioId == firstId)
            .GroupBy(row => new
            {
                row.Provider,
                row.ModelId,
                row.Feature,
            })
            .Select(group => new
            {
                group.Key.Provider,
                group.Key.ModelId,
                group.Key.Feature,
                InvocationCount = group.Count(),
                TotalLatencyMilliseconds = group.Sum(row => row.LatencyMilliseconds),
                TotalInputUnits = group.Sum(row => row.InputUnits),
                TotalOutputUnits = group.Sum(row => row.OutputUnits),
                TotalEstimatedCostUsd = group.Sum(row => row.EstimatedCostUsd),
            });

        _commands.Clear();
        var usageAggregate = await usageAggregationQuery.SingleAsync();
        var usageCommands = _commands.ToArray();
        CaptureSql("USAGE_AGGREGATION_COMMAND_COUNT", usageCommands.Length.ToString());
        for (var index = 0; index < usageCommands.Length; index++)
            CaptureSql($"USAGE_AGGREGATION_COMMAND_{index + 1}", usageCommands[index]);

        usageCommands.Should().ContainSingle(
            "the usage aggregation must execute as one server-translated PostgreSQL command");
        usageCommands[0].Should().Contain("GROUP BY");
        usageCommands[0].Should().Contain("sum(");
        usageCommands[0].Should().Contain("LlmUsageEvidence");
        usageAggregate.Provider.Should().Be("openai");
        usageAggregate.ModelId.Should().Be("gpt-4o");
        usageAggregate.Feature.Should().Be("scan.extraction");
        usageAggregate.InvocationCount.Should().Be(2);
        usageAggregate.TotalLatencyMilliseconds.Should().Be(600);
        usageAggregate.TotalInputUnits.Should().Be(200);
        usageAggregate.TotalOutputUnits.Should().Be(50);
        usageAggregate.TotalEstimatedCostUsd.Should().Be(0.00030m);
        typeof(LlmUsageEvidence).GetProperties()
            .Select(property => property.Name)
            .Should().NotContain(name =>
                name.Contains("Key", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Prompt", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Document", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Response", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DevelopmentClaudeCli_RecordsZeroCostUsageButCannotBeActivatedAsApiCredential()
    {
        var portfolioId = await CreatePortfolioAsync("claude-cli-usage");
        var service = CreateService();

        await service.RecordUsageAsync(
            portfolioId,
            "claude-cli",
            "claude-cli:sonnet",
            "scan.extraction",
            42,
            0,
            0,
            0m,
            usageEventIdentity: "claude-cli-development-event");

        var usage = await _context.Db.LlmUsageEvidence
            .AsNoTracking()
            .SingleAsync(row => row.PortfolioId == portfolioId);
        usage.Provider.Should().Be("claude-cli");
        usage.ModelId.Should().Be("claude-cli:sonnet");
        usage.EstimatedCostUsd.Should().Be(0m);

        var activate = () => service.ActivateAsync(
            Access(portfolioId),
            new ActivateAiCredentialRequest(
                "claude-cli",
                "sonnet",
                "not-a-customer-api-key")
            {
                ClientOperationId = "claude-cli-activate-rejected",
            });
        await activate.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*OpenAI or Anthropic*");
    }

    [Fact]
    public async Task ScanCandidates_PageInPostgreSql()
    {
        var scanService = new ScanService(
            _context.Db,
            Mock.Of<RentalCommand.Core.Atomic.IAtomicUnitOfWork>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScanService>.Instance,
            TimeProvider.System);
        var scope = new WorkspaceReadScope(
            1,
            1,
            Guid.Parse("8c72f7a4-6346-4a4a-a3f4-2ee807ed8ce0"),
            44,
            9);

        var technicianQuery = scanService.BuildTechnicianCandidateQuery(
            scope, "repair", 20, 20);
        _commands.Clear();
        var technicianCandidates = await technicianQuery.ToListAsync();
        var technicianCommands = _commands.ToArray();

        var targetQuery = scanService.BuildTargetCandidateQuery(
            scope, "main", 20, 20);
        _commands.Clear();
        var targetCandidates = await targetQuery.ToListAsync();
        var targetCommands = _commands.ToArray();

        technicianCandidates.Should().BeEmpty();
        targetCandidates.Should().BeEmpty();
        technicianCommands.Should().ContainSingle(
            "the candidate page must execute as one PostgreSQL command");
        targetCommands.Should().ContainSingle(
            "the candidate page must execute as one PostgreSQL command");
        CaptureSql("TECHNICIAN_CANDIDATE_PAGE", technicianCommands[0]);
        CaptureSql("TARGET_CANDIDATE_PAGE", targetCommands[0]);

        foreach (var sql in new[] { technicianCommands[0], targetCommands[0] })
        {
            sql.Should().Contain("AuthSessions");
            sql.Should().Contain("ORDER BY");
            sql.Should().Contain("LIMIT");
            sql.Should().Contain("OFFSET");
            sql.Should().Contain("ILIKE");
        }
    }

    private WorkspaceLlmCredentialService CreateService(
        IAtomicUnitOfWork? atomic = null,
        IDataProtectionProvider? dataProtection = null)
    {
        var authorization = new Mock<IWorkspaceAuthorizationEvaluator>();
        authorization
            .Setup(service => service.HasCapabilityAsync(
                It.IsAny<ActiveAccessContext>(),
                CapabilityKeys.IntegrationsManage,
                It.IsAny<WorkspaceAuthorizationTarget>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new WorkspaceLlmCredentialService(
            _context.Db,
            dataProtection ?? new TestDataProtectionProvider(),
            [new SuccessfulProbe("openai"), new SuccessfulProbe("anthropic")],
            authorization.Object,
            TimeProvider.System,
            atomic ?? new InlineWorkspaceLlmAtomicUnitOfWork(_context.Db));
    }

    private async Task<int> CreatePortfolioAsync(string suffix)
    {
        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = $"L07 {suffix} {Guid.NewGuid():N}",
            ManagementCompanyName = "L07",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.Db.Portfolios.Add(portfolio);
        await _context.Db.SaveChangesAsync();
        return portfolio.Id;
    }

    private static ActiveAccessContext Access(int portfolioId) => new(
        Guid.NewGuid(),
        1,
        1,
        portfolioId,
        1,
        WorkspaceExperience.Management,
        1,
        WorkspaceExperience.Management);

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

    private sealed class SuccessfulProbe(string provider) : ILlmCredentialProbe
    {
        public string ProviderKey => provider;

        public Task<LlmCredentialProbeResult> TestCredentialAsync(
            string apiKey,
            string modelId,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmCredentialProbeResult(
                true,
                ProviderKey,
                modelId));
    }

    private sealed class InlineWorkspaceLlmAtomicUnitOfWork(RentalCommandDbContext db) : IAtomicUnitOfWork
    {
        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            AtomicJsonResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            object result = command switch
            {
                ActivateWorkspaceLlmCredentialCommand activate => await ActivateAsync(activate, ct),
                RotateWorkspaceLlmCredentialCommand rotate => await RotateAsync(rotate, ct),
                RemoveWorkspaceLlmCredentialCommand remove => await RemoveAsync(remove, ct),
                RecordLlmUsageEvidenceCommand usage => await RecordUsageAsync(usage, ct),
                _ => throw new NotSupportedException(command.GetType().Name),
            };
            return new AtomicCommandOutcome<TResult>((TResult)result, AtomicCommandDisposition.Executed, Guid.NewGuid());
        }

        private async Task<AiIntegrationStatusResult> ActivateAsync(
            ActivateWorkspaceLlmCredentialCommand command,
            CancellationToken ct)
        {
            var row = await db.WorkspaceLlmCredentials
                .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId, ct);
            if (row is null)
            {
                row = new WorkspaceLlmCredential
                {
                    PortfolioId = command.PortfolioId,
                    CreatedAtUtc = command.TestedAtUtc,
                };
                db.WorkspaceLlmCredentials.Add(row);
            }

            row.Provider = command.Provider;
            row.ModelId = command.ModelId;
            row.ApiKeyCipherText = command.ApiKeyCipherText;
            row.LastTestedAtUtc = command.TestedAtUtc;
            row.UpdatedAtUtc = command.TestedAtUtc;
            await db.SaveChangesAsync(ct);
            return new AiIntegrationStatusResult(true, row.Provider, row.ModelId, row.LastTestedAtUtc, row.UpdatedAtUtc);
        }

        private async Task<AiIntegrationStatusResult> RotateAsync(
            RotateWorkspaceLlmCredentialCommand command,
            CancellationToken ct)
        {
            var row = await db.WorkspaceLlmCredentials
                .SingleAsync(item => item.PortfolioId == command.PortfolioId, ct);
            row.Provider = command.Provider;
            row.ModelId = command.ModelId;
            row.ApiKeyCipherText = command.ApiKeyCipherText;
            row.LastTestedAtUtc = command.TestedAtUtc;
            row.UpdatedAtUtc = command.TestedAtUtc;
            row.RotatedAtUtc = command.TestedAtUtc;
            await db.SaveChangesAsync(ct);
            return new AiIntegrationStatusResult(true, row.Provider, row.ModelId, row.LastTestedAtUtc, row.UpdatedAtUtc);
        }

        private async Task<RemoveWorkspaceLlmCredentialResult> RemoveAsync(
            RemoveWorkspaceLlmCredentialCommand command,
            CancellationToken ct)
        {
            var row = await db.WorkspaceLlmCredentials
                .SingleAsync(item => item.PortfolioId == command.PortfolioId, ct);
            db.WorkspaceLlmCredentials.Remove(row);
            await db.SaveChangesAsync(ct);
            return new RemoveWorkspaceLlmCredentialResult(true);
        }

        private async Task<RecordLlmUsageEvidenceResult> RecordUsageAsync(
            RecordLlmUsageEvidenceCommand command,
            CancellationToken ct)
        {
            var row = new LlmUsageEvidence
            {
                PortfolioId = command.PortfolioId,
                Provider = command.Provider,
                ModelId = command.ModelId,
                Feature = command.Feature,
                LatencyMilliseconds = command.LatencyMilliseconds,
                InputUnits = command.InputUnits,
                OutputUnits = command.OutputUnits,
                EstimatedCostUsd = command.EstimatedCostUsd,
                OccurredAtUtc = command.OccurredAtUtc,
            };
            db.LlmUsageEvidence.Add(row);
            await db.SaveChangesAsync(ct);
            return new RecordLlmUsageEvidenceResult(row.Id);
        }
    }

    private sealed class CapturingCredentialAtomicUnitOfWork : IAtomicUnitOfWork
    {
        public List<IAtomicCommandData> Commands { get; } = [];

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            AtomicJsonResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            Commands.Add(command);
            object result = new AiIntegrationStatusResult(
                true,
                (command as ActivateWorkspaceLlmCredentialCommand)?.Provider,
                (command as ActivateWorkspaceLlmCredentialCommand)?.ModelId,
                DateTime.UtcNow,
                DateTime.UtcNow);
            return Task.FromResult(new AtomicCommandOutcome<TResult>(
                (TResult)result,
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()));
        }
    }

    private sealed class RandomizedTestDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) =>
            new RandomizedTestDataProtector(purpose);
    }

    private sealed class RandomizedTestDataProtector(string purpose) : IDataProtector
    {
        public IDataProtector CreateProtector(string nextPurpose) =>
            new RandomizedTestDataProtector($"{purpose}:{nextPurpose}");

        public byte[] Protect(byte[] plaintext)
        {
            var prefix = System.Text.Encoding.UTF8.GetBytes($"{purpose}:{Guid.NewGuid():N}:");
            return [.. prefix, .. plaintext.Reverse()];
        }

        public byte[] Unprotect(byte[] protectedData) => throw new NotSupportedException();
    }

    private sealed class TestDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) =>
            new TestDataProtector(purpose);
    }

    private sealed class TestDataProtector(string purpose) : IDataProtector
    {
        public IDataProtector CreateProtector(string nextPurpose) =>
            new TestDataProtector($"{purpose}:{nextPurpose}");

        public byte[] Protect(byte[] plaintext)
        {
            var prefix = System.Text.Encoding.UTF8.GetBytes($"{purpose}:");
            return [.. prefix, .. plaintext.Reverse()];
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            var prefixLength = System.Text.Encoding.UTF8.GetByteCount($"{purpose}:");
            if (protectedData.Length < prefixLength)
            {
                throw new CryptographicException();
            }
            return protectedData[prefixLength..].Reverse().ToArray();
        }
    }
}
