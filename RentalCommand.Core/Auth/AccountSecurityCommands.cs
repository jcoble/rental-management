using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Auth;

public sealed record BootstrapAccountCommand(
    string Email,
    string NormalizedEmail,
    string DisplayName,
    string? PasswordHash,
    string CredentialIntentHash,
    bool EmailConfirmed,
    string PortfolioName,
    string ManagementCompanyName,
    string OwnerName,
    Guid EmailLockId) : IAtomicCommandData;

public enum BootstrapAccountOutcome
{
    Created,
    DuplicateEmail,
}

public sealed record BootstrapAccountResult(
    BootstrapAccountOutcome Outcome,
    int UserId,
    int PortfolioId,
    int AccessContextId) : IAtomicResultData;

public sealed record ConfirmAccountEmailCommand(
    int UserId,
    string ExpectedSecurityStamp,
    bool TokenWasValidated,
    string ConfirmationIntentHash) : IAtomicCommandData;

public enum ConfirmAccountEmailOutcome
{
    Confirmed,
    AlreadyConfirmed,
    InvalidToken,
    UserNotFound,
}

public sealed record ConfirmAccountEmailResult(
    ConfirmAccountEmailOutcome Outcome,
    int UserId) : IAtomicResultData;

public sealed record ResetAccountPasswordCommand(
    int UserId,
    string ExpectedSecurityStamp,
    bool TokenWasValidated,
    string PasswordHash,
    string PasswordIntentHash) : IAtomicCommandData;

public enum ResetAccountPasswordOutcome
{
    Reset,
    InvalidToken,
    UserNotFound,
}

public sealed record ResetAccountPasswordResult(
    ResetAccountPasswordOutcome Outcome,
    int UserId) : IAtomicResultData;

public sealed record ConfirmGoogleAccountEmailCommand(
    int UserId,
    string ExpectedSecurityStamp,
    string GoogleSubjectHash) : IAtomicCommandData;

public sealed record AuthEmailOutboxCommand(
    int UserId,
    int? ExpectedPortfolioId,
    string ExpectedSecurityStamp,
    string EmailKind,
    string PreparedEmailPayload,
    string EmailIntentHash,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AuthEmailOutboxResult(
    bool Enqueued,
    int UserId,
    int PortfolioId,
    string EmailKind) : IAtomicResultData;

public sealed record AtomicInitialWorkspaceBootstrap(
    int PortfolioId,
    int AccessContextId) : IAtomicResultData;
