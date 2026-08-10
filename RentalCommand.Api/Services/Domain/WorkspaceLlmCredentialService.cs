using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.AiIntegrations;
using RentalCommand.Core.Atomic;
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
    Task RemoveAsync(
        ActiveAccessContext access,
        string? clientOperationId = null,
        CancellationToken ct = default);
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
    private readonly IAtomicUnitOfWork _atomic;
    private static readonly AtomicJsonResultCodec<AiIntegrationStatusResult> StatusCodec =
        new("ai.integration.status.v1");
    private static readonly AtomicJsonResultCodec<RemoveWorkspaceLlmCredentialResult> RemoveCodec =
        new("ai.integration.remove.v1");
    private static readonly AtomicJsonResultCodec<RecordLlmUsageEvidenceResult> UsageCodec =
        new("ai.integration.usage.v1");

    public WorkspaceLlmCredentialService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        IEnumerable<ILlmCredentialProbe> probes,
        IWorkspaceAuthorizationEvaluator authorization,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _probes = probes.ToDictionary(probe => probe.ProviderKey, StringComparer.OrdinalIgnoreCase);
        _authorization = authorization;
        _timeProvider = timeProvider;
        _atomic = atomic;
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
        var command = new ActivateWorkspaceLlmCredentialCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            normalized.Provider,
            normalized.ModelId,
            ApiKeyIntentDigest(normalized.Provider, normalized.ModelId, normalized.ApiKey),
            Encrypt(normalized.ApiKey),
            now);
        var identity = new AtomicCommandIdentity(
            "ai.integration.credential.activate",
            MutationIdentity(access.PortfolioId, request.ClientOperationId));
        var outcome = await _atomic.ExecuteAsync(identity, command, StatusCodec, ct);
        return MapStatus(outcome.Value);
    }

    public async Task<AiIntegrationStatusDto> RotateAsync(
        ActiveAccessContext access,
        RotateAiCredentialRequest request,
        CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var normalized = NormalizeAndValidate(request.Provider, request.ModelId, request.ApiKey);
        await DemandValidCredentialAsync(normalized, ct);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var command = new RotateWorkspaceLlmCredentialCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            normalized.Provider,
            normalized.ModelId,
            ApiKeyIntentDigest(normalized.Provider, normalized.ModelId, normalized.ApiKey),
            Encrypt(normalized.ApiKey),
            now);
        var identity = new AtomicCommandIdentity(
            "ai.integration.credential.rotate",
            MutationIdentity(access.PortfolioId, request.ClientOperationId));
        var outcome = await _atomic.ExecuteAsync(identity, command, StatusCodec, ct);
        return MapStatus(outcome.Value);
    }

    public async Task RemoveAsync(
        ActiveAccessContext access,
        string? clientOperationId = null,
        CancellationToken ct = default)
    {
        await DemandAuthorityAsync(access, ct);
        var command = new RemoveWorkspaceLlmCredentialCommand(
            access.PortfolioId,
            access.UserId,
            access.SessionId,
            access.AccessContextId,
            access.AccessRevision,
            _timeProvider.GetUtcNow().UtcDateTime);
        var identity = new AtomicCommandIdentity(
            "ai.integration.credential.remove",
            MutationIdentity(access.PortfolioId, clientOperationId));
        await _atomic.ExecuteAsync(identity, command, RemoveCodec, ct);
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
        CancellationToken ct = default,
        string? usageEventIdentity = null)
    {
        var stableIdentity = Required(
            usageEventIdentity ?? throw new ArgumentException(
                "A stable LLM usage event identity is required.",
                nameof(usageEventIdentity)),
            nameof(usageEventIdentity),
            200);
        if (latencyMilliseconds < 0 ||
            inputUnits < 0 ||
            outputUnits < 0 ||
            estimatedCostUsd < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latencyMilliseconds),
                "LLM usage amounts must be greater than or equal to zero.");
        }

        var command = new RecordLlmUsageEvidenceCommand(
            portfolioId,
            stableIdentity,
            NormalizeUsageProvider(provider),
            Required(modelId, nameof(modelId), 128),
            Required(feature, nameof(feature), 80),
            latencyMilliseconds,
            inputUnits,
            outputUnits,
            estimatedCostUsd,
            _timeProvider.GetUtcNow().UtcDateTime);
        await _atomic.ExecuteAsync(
            new AtomicCommandIdentity("ai.integration.usage.record", $"{portfolioId}:{stableIdentity}"),
            command,
            UsageCodec,
            ct);
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

    private static string Required(string? value, string name, int maxLength, string? message = null)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
        {
            throw new ArgumentException(
                message ?? $"{name} is required and must be at most {maxLength} characters.",
                name);
        }
        return normalized;
    }

    private static string MutationIdentity(int portfolioId, string? clientOperationId) =>
        $"{portfolioId}:{Required(clientOperationId, nameof(clientOperationId), 160, "A request key is required and cannot exceed 160 characters.")}";

    private static string ApiKeyIntentDigest(string provider, string modelId, string apiKey)
    {
        var material = $"{provider}\n{modelId}\n{apiKey}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static AiIntegrationStatusDto MapStatus(AiIntegrationStatusResult result) =>
        new(result.Configured, result.Provider, result.ModelId, result.LastTestedAtUtc, result.UpdatedAtUtc);

    private sealed record NormalizedCredential(
        string Provider,
        string ModelId,
        string ApiKey);
}
