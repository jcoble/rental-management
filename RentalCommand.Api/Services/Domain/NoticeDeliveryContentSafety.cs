using System.Text.RegularExpressions;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// Rejects notice copy that still contains reserved merge syntax or internal
/// administrator guidance. Delivery remains authoritative even when a client
/// does not perform the same presentation check.
/// </summary>
public static partial class NoticeDeliveryContentSafety
{
    public const string CorrectionMessage =
        "Review the notice and remove any unfinished merge fields or internal instructions before sending.";

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex ReservedMergeResidue();

    public static bool IsSafe(string? subject, string? body) =>
        FindIssue(subject, body) is null;

    public static string? FindIssue(string? subject, string? body) =>
        IsUnsafe(subject) || IsUnsafe(body) ? CorrectionMessage : null;

    public static void RequireSafe(string? subject, string? body)
    {
        if (FindIssue(subject, body) is { } issue)
            throw new InvalidOperationException(issue);
    }

    private static bool IsUnsafe(string? value) =>
        !string.IsNullOrEmpty(value)
        && (value.Contains("Workspace administrator:", StringComparison.OrdinalIgnoreCase)
            || ReservedMergeResidue().IsMatch(value));
}
