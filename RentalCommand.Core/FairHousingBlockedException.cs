namespace RentalCommand.Core;

/// <summary>
/// Thrown by a send path (a landlord notice approval or a new landlord → tenant message) when the
/// Fair Housing Act review flagged the outgoing copy and the caller did NOT acknowledge/override the
/// review. Carries the flagged phrases + concerns so the request pipeline can surface them to the
/// human, who can fix the copy or consciously override.
///
/// <para>
/// Mapped by the global exception handler to <b>HTTP 422 (Unprocessable Content)</b> — the request was
/// syntactically valid but its <i>content</i> can't be sent as-is. This is deliberately distinct from a
/// <see cref="DomainValidationException"/> (400/409, no structured payload): a Fair-Housing block is a
/// soft, overridable gate, not a hard invariant violation, and the client needs the structured concern
/// list to render the warning and offer "send anyway".
/// </para>
///
/// <para>
/// Fail-open by design lives at the call site, not here: this exception is thrown ONLY when the review
/// actually ran AND found concerns. If the review is unavailable (no AI key) or errored, the send
/// proceeds without throwing — an infrastructure hiccup must never block legitimate landlord mail.
/// </para>
/// </summary>
public sealed class FairHousingBlockedException : Exception
{
    /// <summary>The flagged phrases and the Fair-Housing concern each raises.</summary>
    public IReadOnlyList<FairHousingConcern> Concerns { get; }

    public FairHousingBlockedException(IReadOnlyList<FairHousingConcern> concerns)
        : base("This message was flagged by the Fair Housing review and was not sent. "
               + "Revise the flagged language, or resend with the Fair Housing review acknowledged.")
    {
        Concerns = concerns;
    }
}

/// <summary>
/// A single Fair-Housing concern surfaced to the client on a 422 block: the flagged phrase and a
/// plain-language explanation. A transport-layer mirror of the API's <c>FairHousingIssue</c>, declared
/// in Core so <see cref="FairHousingBlockedException"/> (and the global handler that maps it) don't
/// depend on the API DTO assembly.
/// </summary>
public sealed record FairHousingConcern(string Phrase, string Concern);
