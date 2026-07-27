using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public interface IWorkspaceLlmCredentialService :
    IWorkspaceLlmCredentialResolver,
    ILlmUsageEvidenceRecorder
{
    Task<bool> AuthorizeAsync(ActiveAccessContext access, CancellationToken ct = default);
    Task<AiIntegrationStatusDto> GetStatusAsync(int portfolioId, CancellationToken ct = default);
    Task<AiCredentialTestResultDto> TestAsync(
        ActiveAccessContext access,
        TestAiCredentialRequest request,
        CancellationToken ct = default);
    Task<AiIntegrationStatusDto> ActivateAsync(
        ActiveAccessContext access,
        ActivateAiCredentialRequest request,
        CancellationToken ct = default);
    Task<AiIntegrationStatusDto> RotateAsync(
        ActiveAccessContext access,
        RotateAiCredentialRequest request,
        CancellationToken ct = default);
    Task RemoveAsync(ActiveAccessContext access, CancellationToken ct = default);
}

public sealed class WorkspaceLlmCredentialService : IWorkspaceLlmCredentialService
{
    private const string ProtectorPurpose = "RentalCommand.WorkspaceLlmCredential.v1";
    private static readonly HashSet<string> ApprovedProviders =
        new(StringComparer.OrdinalIgnoreCase) { "openai", "anthropic" };

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IReadOnlyDictionary<string, ILlmCredentialProbe> _probes;
    private readonly IWorkspaceAuthorizationEvaluator _authorization;
    private readonly TimeProvider _timeProvider;

    public WorkspaceLlmCredentialService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        IEnumerable<ILlmCredentialProbe> probes,
        IWorkspaceAuthorizationEvaluator authorization,
        TimeProvider timeProvider)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _probes = probes.ToDictionary(probe => probe.ProviderKey, StringComparer.OrdinalIgnoreCase);
        _authorization = authorization;
        _timeProvider = timeProvider;
    }

    public Task<bool> AuthorizeAsync(ActiveAccessContext access, CancellationToken ct = default) =>
        _authorization.HasCapabilityAsync(
            access,
            CapabilityKeys.IntegrationsManage,
            new WorkspaceCapabilityAuthorizationTarget(access.PortfolioId),
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);

    public Task<AiIntegrationStatusDto> GetStatusAsync(
        int portfolioId,
        CancellationToken ct = default) =>
        _db.WorkspaceLlmCredentials
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
            .Select(row => new AiIntegrationStatusDto(
                true,
                row.Provider,
                row.ModelId,
                row.LastTestedAtUtc,
                row.UpdatedAtUtc))
            .SingleOrDefaultAsync(ct)
            .ContinueWith(
                task => task.Result ?? new AiIntegrationStatusDto(false, null, null, null, null),
                ct,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    public async Task<AiCredentialTestResultDto> TestAsync(
        ActiveAccessContext access,
        TestAiCredentialRequest request,
        CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var normalized = NormalizeAndValidate(request.Provider, request.ModelId, request.ApiKey);
        var result = await _probes[normalized.Provider]
            .TestCredentialAsync(normalized.ApiKey, normalized.ModelId, ct);
        return new AiCredentialTestResultDto(
            result.Succeeded, result.Provider, result.ModelId, result.Error);
    }

    public async Task<AiIntegrationStatusDto> ActivateAsync(
        ActiveAccessContext access,
        ActivateAiCredentialRequest request,
        CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var normalized = NormalizeAndValidate(request.Provider, request.ModelId, request.ApiKey);
        await DemandValidCredentialAsync(normalized, ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _db.WorkspaceLlmCredentials
            .SingleOrDefaultAsync(row => row.PortfolioId == access.PortfolioId, ct);
        if (existing is null)
        {
            existing = new WorkspaceLlmCredential
            {
                PortfolioId = access.PortfolioId,
                CreatedAtUtc = now,
            };
            _db.WorkspaceLlmCredentials.Add(existing);
        }

        existing.Provider = normalized.Provider;
        existing.ModelId = normalized.ModelId;
        existing.ApiKeyCipherText = Encrypt(normalized.ApiKey);
        existing.LastTestedAtUtc = now;
        existing.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return MapStatus(existing);
    }

    public async Task<AiIntegrationStatusDto> RotateAsync(
        ActiveAccessContext access,
        RotateAiCredentialRequest request,
        CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var normalized = NormalizeAndValidate(request.Provider, request.ModelId, request.ApiKey);
        await DemandValidCredentialAsync(normalized, ct);

        var row = await _db.WorkspaceLlmCredentials
            .SingleOrDefaultAsync(item => item.PortfolioId == access.PortfolioId, ct)
            ?? throw new InvalidOperationException(
                "No AI provider is configured. Activate a credential before rotating it.");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        row.Provider = normalized.Provider;
        row.ModelId = normalized.ModelId;
        row.ApiKeyCipherText = Encrypt(normalized.ApiKey);
        row.LastTestedAtUtc = now;
        row.UpdatedAtUtc = now;
        row.RotatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return MapStatus(row);
    }

    public async Task RemoveAsync(ActiveAccessContext access, CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var row = await _db.WorkspaceLlmCredentials
            .SingleOrDefaultAsync(item => item.PortfolioId == access.PortfolioId, ct);
        if (row is null)
        {
            throw new InvalidOperationException("No AI provider credential is configured.");
        }

        _db.WorkspaceLlmCredentials.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<WorkspaceLlmRuntimeCredential?> ResolveActiveAsync(
        int portfolioId,
        CancellationToken ct = default)
    {
        var encrypted = await _db.WorkspaceLlmCredentials
            .AsNoTracking()
            .Where(row => row.PortfolioId == portfolioId)
            .Select(row => new
            {
                row.PortfolioId,
                row.Provider,
                row.ModelId,
                row.ApiKeyCipherText,
            })
            .SingleOrDefaultAsync(ct);
        if (encrypted is null)
        {
            return null;
        }

        try
        {
            return new WorkspaceLlmRuntimeCredential(
                encrypted.PortfolioId,
                encrypted.Provider,
                encrypted.ModelId,
                _protector.Unprotect(encrypted.ApiKeyCipherText));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public async Task RecordUsageAsync(
        int portfolioId,
        string provider,
        string modelId,
        string feature,
        int latencyMilliseconds,
        int inputUnits,
        int outputUnits,
        decimal estimatedCostUsd,
        CancellationToken ct = default)
    {
        _db.LlmUsageEvidence.Add(new LlmUsageEvidence
        {
            PortfolioId = portfolioId,
            Provider = NormalizeUsageProvider(provider),
            ModelId = Required(modelId, nameof(modelId), 128),
            Feature = Required(feature, nameof(feature), 80),
            LatencyMilliseconds = Math.Max(0, latencyMilliseconds),
            InputUnits = Math.Max(0, inputUnits),
            OutputUnits = Math.Max(0, outputUnits),
            EstimatedCostUsd = Math.Max(0, estimatedCostUsd),
            OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
        });
        await _db.SaveChangesAsync(ct);
    }

    public string Encrypt(string apiKey) => _protector.Protect(apiKey);

    private async Task DemandAuthorityAsync(
        ActiveAccessContext access,
        CancellationToken ct)
    {
        if (!await AuthorizeAsync(access, ct))
        {
            throw new UnauthorizedAccessException(
                "Workspace integration authority is required.");
        }
    }

    private async Task DemandValidCredentialAsync(
        NormalizedCredential credential,
        CancellationToken ct)
    {
        var tested = await _probes[credential.Provider]
            .TestCredentialAsync(credential.ApiKey, credential.ModelId, ct);
        if (!tested.Succeeded)
        {
            throw new InvalidOperationException(
                tested.Error ?? "The provider rejected this credential.");
        }
    }

    private NormalizedCredential NormalizeAndValidate(
        string provider,
        string modelId,
        string apiKey)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (!_probes.ContainsKey(normalizedProvider))
        {
            throw new InvalidOperationException(
                $"The {normalizedProvider} provider is not available.");
        }

        return new NormalizedCredential(
            normalizedProvider,
            Required(modelId, nameof(modelId), 128),
            Required(apiKey, nameof(apiKey), 4000));
    }

    private static string NormalizeProvider(string provider)
    {
        var normalized = Required(provider, nameof(provider), 32).ToLowerInvariant();
        if (!ApprovedProviders.Contains(normalized))
        {
            throw new ArgumentException("Provider must be OpenAI or Anthropic.", nameof(provider));
        }
        return normalized;
    }

    private static string NormalizeUsageProvider(string provider)
    {
        var normalized = Required(provider, nameof(provider), 32).ToLowerInvariant();
        if (!ApprovedProviders.Contains(normalized) && normalized != "claude-cli")
        {
            throw new ArgumentException(
                "Usage provider must be OpenAI, Anthropic, or claude-cli.",
                nameof(provider));
        }
        return normalized;
    }

    private static string Required(string value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
        {
            throw new ArgumentException(
                $"{name} is required and must be at most {maxLength} characters.",
                name);
        }
        return normalized;
    }

    private static AiIntegrationStatusDto MapStatus(WorkspaceLlmCredential row) =>
        new(true, row.Provider, row.ModelId, row.LastTestedAtUtc, row.UpdatedAtUtc);

    private sealed record NormalizedCredential(
        string Provider,
        string ModelId,
        string ApiKey);
}
