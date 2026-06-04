namespace RentalCommand.Core.Enums;

/// <summary>How a signer captured their signature on the native e-sign page.</summary>
public enum SignatureSignatureType
{
    /// <summary>No signature captured yet.</summary>
    None,

    /// <summary>The signer typed their name; it is rendered in a script style on the executed PDF.</summary>
    Typed,

    /// <summary>The signer drew their signature on a canvas; the PNG image is stamped on the PDF.</summary>
    Drawn
}
