namespace RentalCommand.Core.Enums;

/// <summary>Type of an append-only native e-sign audit event. Serialized as its string name on the wire.</summary>
public enum SignatureAuditEventType
{
    /// <summary>The request was created and dispatched to signers.</summary>
    Sent,

    /// <summary>A signer opened the signing page.</summary>
    Viewed,

    /// <summary>A signer applied their signature + consent.</summary>
    Signed,

    /// <summary>A signer declined to sign.</summary>
    Declined,

    /// <summary>All signers have signed; the executed document was generated.</summary>
    Completed,

    /// <summary>The request was voided/cancelled.</summary>
    Voided
}
