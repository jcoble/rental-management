namespace RentalCommand.Core.Enums;

/// <summary>
/// Where a lease sits in the electronic-signature workflow. Serialized as its string name on the wire
/// (matching the app-wide enum convention).
/// </summary>
public enum EsignStatus
{
    /// <summary>No signature request has been sent for this lease.</summary>
    None,

    /// <summary>The agreement has been sent to the signer(s) and is awaiting signature.</summary>
    Sent,

    /// <summary>All signers have signed; the signed document is stored on the lease.</summary>
    Signed,

    /// <summary>A signer declined to sign the agreement.</summary>
    Declined
}
