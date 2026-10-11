namespace RentalCommand.Core.Constants;

/// <summary>
/// Stable source markers carried inside outbox JSON payloads when dispatch policy depends on the
/// business workflow that created the message.
/// </summary>
public static class OutboxPayloadSources
{
    /// <summary>
    /// Native lease e-sign signing-link email. The signer is resolved from the lease tenant.
    /// </summary>
    public const string LeaseEsignSigningLink = "lease-esign-signing-link";

    /// <summary>FCRA adverse-action notice delivery to a rental applicant.</summary>
    public const string AdverseActionNotice = "adverse-action-notice";
}
