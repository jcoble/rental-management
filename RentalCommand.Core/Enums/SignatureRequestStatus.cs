namespace RentalCommand.Core.Enums;

/// <summary>
/// Where a native e-sign envelope (one <see cref="Entities.SignatureRequest"/>) sits in its lifecycle.
/// Serialized as its string name on the wire (matching the app-wide string-enum convention).
/// </summary>
public enum SignatureRequestStatus
{
    /// <summary>Created but not yet sent to any signer (reserved; native sends go straight to Sent).</summary>
    Draft,

    /// <summary>Dispatched to the signer(s); awaiting action.</summary>
    Sent,

    /// <summary>At least one signer has opened/viewed the document but none has signed yet.</summary>
    Viewed,

    /// <summary>Some — but not all — signers have signed.</summary>
    PartiallySigned,

    /// <summary>Every signer has signed; executed-document rendering/finalization is durably pending.</summary>
    ExecutionPending,

    /// <summary>Every signer has signed; the executed PDF + certificate have been generated and stored.</summary>
    Completed,

    /// <summary>A signer declined; the request is dead.</summary>
    Declined,

    /// <summary>The request was voided/cancelled by the landlord before completion.</summary>
    Voided
}
