using RentalCommand.Core.Atomic;

namespace RentalCommand.Core.Auth;

public sealed record ChangePasswordCommand(
    Guid AuthSessionId,
    int UserId,
    int AccessContextId,
    long ExpectedAccessRevision,
    string CurrentPassword,
    string NewPassword,
    string PasswordIntentHash) : IAtomicCommandData;

public enum ChangePasswordOutcome
{
    Changed,
    UserNotFound,
    AccessUnavailable,
    NoLocalPassword,
    CurrentPasswordIncorrect,
}

public sealed record ChangePasswordResult(
    ChangePasswordOutcome Outcome,
    int UserId,
    int AccessContextId) : IAtomicResultData;
