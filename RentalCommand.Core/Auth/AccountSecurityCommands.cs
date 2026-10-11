using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Auth;

public sealed record BootstrapAccountCommand(
    string Email,
    string NormalizedEmail,
    string DisplayName,
    [property: AtomicFingerprintIgnore] string? PasswordHash,
    string CredentialIntentHash,
    bool EmailConfirmed,
    bool TermsPrivacyAccepted,
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
    int AccessContextId);

public sealed record ConfirmAccountEmailCommand(
    int UserId,
    [property: AtomicFingerprintIgnore] string ExpectedSecurityStamp,
    [property: AtomicFingerprintIgnore] bool TokenWasValidated,
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
    int UserId);

public sealed record ResetAccountPasswordCommand(
    int UserId,
    [property: AtomicFingerprintIgnore] string ExpectedSecurityStamp,
    [property: AtomicFingerprintIgnore] bool TokenWasValidated,
    [property: AtomicFingerprintIgnore] string PasswordHash,
    string PasswordIntentHash) : IAtomicCommandData;

public enum ResetAccountPasswordOutcome
{
    Reset,
    InvalidToken,
    UserNotFound,
}

public sealed record ResetAccountPasswordResult(
    ResetAccountPasswordOutcome Outcome,
    int UserId);

public sealed record ConfirmGoogleAccountEmailCommand(
    int UserId,
    [property: AtomicFingerprintIgnore] string ExpectedSecurityStamp,
    string GoogleSubjectHash) : IAtomicCommandData;

public sealed record AuthEmailOutboxCommand(
    int UserId,
    int? ExpectedPortfolioId,
    [property: AtomicFingerprintIgnore] string ExpectedSecurityStamp,
    string EmailKind,
    [property: AtomicFingerprintIgnore] string PreparedEmailPayload,
    string EmailIntentHash,
    [property: AtomicFingerprintIgnore] string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AuthEmailOutboxResult(
    bool Enqueued,
    int UserId,
    int PortfolioId,
    string EmailKind);

public sealed record AtomicInitialWorkspaceBootstrap(
    int PortfolioId,
    int AccessContextId);
