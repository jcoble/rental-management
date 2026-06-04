using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>A single turn in a multi-turn Q&amp;A conversation.</summary>
/// <param name="Role">"user" | "assistant"</param>
/// <param name="Content">The text of the turn.</param>
public record QaTurn(string Role, string Content);

/// <summary>Request body for POST /api/v1/ai/ask.</summary>
public class AskRequest
{
    /// <summary>The current user question (plain English).</summary>
    [Required]
    [MaxLength(4000)]
    public string Question { get; set; } = string.Empty;

    /// <summary>Prior turns in the conversation, oldest first (optional).</summary>
    public List<QaTurn>? History { get; set; }

    /// <summary>When true, also email the answer to the signed-in user (their own claim email).</summary>
    public bool DeliverViaEmail { get; set; }

    /// <summary>When true, also text (SMS) the answer to the portfolio owner's phone.</summary>
    public bool DeliverViaSms { get; set; }

    // NOTE: no client-supplied recipient overrides. Delivery is "send this answer to ME" — the
    // recipient is always resolved server-side (the authenticated user's email / the portfolio
    // owner's phone) so the assistant can never be used to relay arbitrary content to arbitrary
    // recipients on the landlord's email/SMS account.
}

/// <summary>
/// Resolved delivery options handed to the Q&amp;A service. The controller fills in sensible
/// default recipients (e.g. the signed-in user's email) before the service enqueues outbox rows.
/// </summary>
/// <param name="ViaEmail">Email the answer when true.</param>
/// <param name="ViaSms">Text the answer when true.</param>
/// <param name="ToEmail">Email recipient — always the authenticated user's own email; null when unknown.</param>
public record QaDeliveryOptions(
    bool ViaEmail,
    bool ViaSms,
    string? ToEmail)
{
    /// <summary>No delivery requested.</summary>
    public static readonly QaDeliveryOptions None = new(false, false, null);

    /// <summary>True when at least one channel is requested.</summary>
    public bool AnyRequested => ViaEmail || ViaSms;
}

/// <summary>Response from POST /api/v1/ai/ask.</summary>
/// <param name="Answer">The assistant's plain-English answer.</param>
/// <param name="ToolsUsed">Distinct list of tool names the model invoked to answer.</param>
/// <param name="LlmAvailable">False when no API key is configured (noop path).</param>
/// <param name="TokensUsed">Total input + output tokens consumed across all loop iterations.</param>
/// <param name="ModelId">The model that produced the answer.</param>
/// <param name="DeliveredChannels">
/// Channels the answer was queued for delivery on (e.g. "Email", "Sms"). Empty when no
/// delivery was requested or no recipient could be resolved for a requested channel.
/// </param>
/// <param name="Source">
/// How the answer was produced: "Data" (live portfolio data via tools) or "Docs" (grounded in
/// knowledge-base articles). Lets the web render the right affordance (e.g. doc links).
/// </param>
/// <param name="Citations">
/// For "Docs" answers: the knowledge-base articles the answer was grounded in, so the web can
/// link to <c>/docs/{slug}</c>. Null/empty for data answers.
/// </param>
public record AskResponse(
    string Answer,
    List<string> ToolsUsed,
    bool LlmAvailable,
    int TokensUsed,
    string ModelId,
    List<string>? DeliveredChannels = null,
    string Source = "Data",
    List<KbCitation>? Citations = null);
