using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Sandbox;
using RentalCommand.Data;
using RentalCommand.Data.Sandbox;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISandboxService"/>
public sealed class SandboxService : ISandboxService
{
    private readonly RentalCommandDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly IWriteExecutor _writes;
    private readonly Auth.DemoDataSeeder _demoSeeder;

    public SandboxService(
        RentalCommandDbContext db,
        TimeProvider timeProvider,
        IWriteExecutor writes,
        Auth.DemoDataSeeder demoSeeder)
    {
        _db = db;
        _timeProvider = timeProvider;
        _writes = writes;
        _demoSeeder = demoSeeder;
    }

    public async Task<SandboxStateResponse?> GetStateAsync(int portfolioId, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        return portfolio is null ? null : ToState(portfolio);
    }

    public async Task<SandboxStateResponse?> GoLiveAsync(
        WorkspaceReadScope scope,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new SandboxLifecycleCommand(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            SandboxLifecycleOperation.GoLive,
            null,
            _timeProvider.GetUtcNow().UtcDateTime,
            idempotencyKey);
        var outcome = await _writes.ExecuteAsync(
            Identity(scope, idempotencyKey),
            SandboxLifecycleWriteSupport.Write(_db, command), ct);
        return ToState(outcome.Value);
    }

    public async Task<SandboxStateResponse?> ApplyOnboardingChoiceAsync(
        WorkspaceReadScope scope,
        OnboardingChoice choice,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var command = new SandboxLifecycleCommand(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            SandboxLifecycleOperation.ApplyOnboardingChoice,
            choice switch
            {
                OnboardingChoice.Sandbox => SandboxOnboardingChoice.Sandbox,
                OnboardingChoice.Live => SandboxOnboardingChoice.Live,
                _ => null,
            },
            _timeProvider.GetUtcNow().UtcDateTime,
            idempotencyKey);
        var outcome = await _writes.ExecuteAsync(
            Identity(scope, idempotencyKey),
            SandboxLifecycleWriteSupport.Write(_db, command), ct);
        if (choice == OnboardingChoice.Sandbox
            && outcome.Value.PortfolioFound
            && outcome.Value.IsSandbox)
        {
            await _demoSeeder.CompleteLegalArtifactsAsync(scope.PortfolioId, ct);
        }
        return ToState(outcome.Value);
    }

    // Kept beside the service contract so IntegrationTests can prove that this service and the
    // PostgreSQL grant/RLS contract classify exactly the same inventory. Order is child-to-parent.
    internal static IReadOnlyList<string> SandboxGraduationDeleteOrder =>
        SandboxLifecyclePersistence.SandboxGraduationDeleteOrder;

    private static string Identity(
        WorkspaceReadScope scope,
        string idempotencyKey)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(idempotencyKey.Trim())))
            .ToLowerInvariant();
        return $"{scope.PortfolioId}:{digest}";
    }

    private static SandboxStateResponse? ToState(SandboxLifecycleResult result) =>
        result.PortfolioFound
            ? new SandboxStateResponse
            {
                PortfolioId = result.PortfolioId,
                IsSandbox = result.IsSandbox,
                SandboxSeededAtUtc = result.SandboxSeededAtUtc,
                OnboardingChoicePending = result.OnboardingChoicePending,
            }
            : null;

    private static SandboxStateResponse ToState(Core.Entities.Portfolio p) => new()
    {
        PortfolioId = p.Id,
        IsSandbox = p.IsSandbox,
        SandboxSeededAtUtc = p.SandboxSeededAtUtc,
        OnboardingChoicePending = PortfolioOnboarding.IsPending(p.Settings),
    };
}
