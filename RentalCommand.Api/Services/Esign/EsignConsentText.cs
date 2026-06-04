namespace RentalCommand.Api.Services.Esign;

/// <summary>
/// Boilerplate ESIGN / UETA electronic-records consent and disclosure wording used by the native
/// e-sign flow. Shown to the signer (who must affirmatively consent) and restated on the Certificate of
/// Completion. Clear and correct for a residential lease; this is not legal advice.
/// </summary>
public static class EsignConsentText
{
    /// <summary>The consent disclosure presented on the signing page; the signer must check "I agree".</summary>
    public const string ConsentDisclosure = """
ELECTRONIC RECORDS AND SIGNATURE DISCLOSURE AND CONSENT

By selecting "I agree to use electronic records and signatures" and signing below, you consent to conduct
this transaction electronically and to sign this document using an electronic signature, under the U.S.
Electronic Signatures in Global and National Commerce Act (E-SIGN, 15 U.S.C. § 7001 et seq.) and the
Uniform Electronic Transactions Act (UETA) as adopted in your state.

You agree that:
1. Your electronic signature is the legal equivalent of your handwritten signature and is binding.
2. You intend to sign this document and to be bound by its terms.
3. You can access and retain this document in electronic form. To view and keep a copy you need a device
   with a current web browser and a PDF reader, plus the ability to download or print the document.
4. You may request a paper copy of this signed document, and you may withdraw your consent to use
   electronic records, by contacting the sender — but doing so will not affect the legal validity of any
   document you have already signed electronically.
5. Information about you — including your name, email address, the date and time you view and sign, and
   the IP address and browser you use — will be recorded to create a tamper-evident audit trail and a
   Certificate of Completion attached to the signed document.

If you do not agree, do not sign; instead contact the sender to arrange an alternative.
""";

    /// <summary>The shorter statement printed on the Certificate of Completion page.</summary>
    public const string CertificateStatement = """
This document was executed electronically using Rental Command's native electronic-signature service. Each
signer affirmatively consented to use electronic records and signatures under the U.S. ESIGN Act (15 U.S.C.
§ 7001 et seq.) and the Uniform Electronic Transactions Act (UETA). Each electronic signature is the legal
equivalent of a handwritten signature. The audit details below — names, email addresses, signing
timestamps (UTC), originating IP addresses, and browser user-agents — together with the SHA-256 hash of
this document, constitute the record of completion.
""";
}
