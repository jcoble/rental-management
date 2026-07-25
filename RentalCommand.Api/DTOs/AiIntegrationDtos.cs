namespace RentalCommand.Api.DTOs;

public sealed record AiIntegrationStatusDto(
    bool Configured,
    string? Provider,
    string? ModelId,
    DateTime? LastTestedAtUtc,
    DateTime? UpdatedAtUtc);

public sealed record TestAiCredentialRequest(string Provider, string ModelId, string ApiKey);

public sealed record ActivateAiCredentialRequest(string Provider, string ModelId, string ApiKey);

public sealed record RotateAiCredentialRequest(string Provider, string ModelId, string ApiKey);

public sealed record AiCredentialTestResultDto(
    bool Succeeded,
    string Provider,
    string ModelId,
    string? Error);
