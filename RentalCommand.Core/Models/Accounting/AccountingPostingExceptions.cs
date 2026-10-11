namespace RentalCommand.Core.Models.Accounting;

/// <summary>Raised when a proposed journal entry violates a posting invariant.</summary>
public sealed class AccountingPostingValidationException : InvalidOperationException
{
    public AccountingPostingValidationException(string message) : base(message) { }
}

/// <summary>Raised when a source key is reused with different accounting facts.</summary>
public sealed class AccountingIdempotencyConflictException : InvalidOperationException
{
    public AccountingIdempotencyConflictException(string message) : base(message) { }
}
