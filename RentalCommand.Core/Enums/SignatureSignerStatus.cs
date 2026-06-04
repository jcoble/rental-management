namespace RentalCommand.Core.Enums;

/// <summary>
/// Per-signer state within a native e-sign request. Serialized as its string name on the wire.
/// </summary>
public enum SignatureSignerStatus
{
    /// <summary>The signer has been sent the link but has not yet opened it.</summary>
    Pending,

    /// <summary>The signer opened the signing page (captured with IP + user-agent).</summary>
    Viewed,

    /// <summary>The signer applied their signature and gave consent.</summary>
    Signed,

    /// <summary>The signer declined to sign.</summary>
    Declined
}
