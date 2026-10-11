using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public sealed partial class AssistantActionService : IAssistantActionService
{
    private readonly RentalCommandDbContext _db;
    private readonly IExpenseService _expenses;
    private readonly TimeProvider _timeProvider;

    public AssistantActionService(RentalCommandDbContext db, IExpenseService expenses, TimeProvider timeProvider)
    {
        _db = db;
        _expenses = expenses;
        _timeProvider = timeProvider;
    }

    public async Task<AssistantActionDraftResponse> DraftAsync(
        WorkspaceReadScope scope,
        AssistantActionDraftRequest request,
        CancellationToken ct = default)
    {
        var command = request.Command.Trim();
        if (command.Length == 0)
        {
            return Unsupported("Tell me what action you want me to draft.");
        }

        if (!LooksLikeExpenseAction(command))
        {
            return Unsupported("I can answer that as a question, but I can only draft expense actions right now.");
        }

        var draft = await BuildExpenseDraftAsync(scope, command, ct);
        var missing = RequiredMissingFields(draft.Expense).ToList();
        var status = missing.Count > 0
            ? AssistantActionStatus.MissingRequiredFields
            : request.WriteModeEnabled
                ? AssistantActionStatus.DraftReady
                : AssistantActionStatus.WriteModeRequired;

        return new AssistantActionDraftResponse
        {
            Status = status,
            RequiresWriteMode = !request.WriteModeEnabled,
            CanExecute = request.WriteModeEnabled && missing.Count == 0,
            MissingFields = missing,
            Draft = draft,
            Message = BuildDraftMessage(draft, missing, request.WriteModeEnabled),
        };
    }

    public async Task<AssistantActionExecuteResponse> ExecuteAsync(
        WorkspaceReadScope scope,
        AssistantActionExecuteRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (!request.WriteModeEnabled)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.WriteModeRequired,
                Message = "Action mode is off. Turn it on before I change portfolio data.",
                Kind = request.Draft?.Kind ?? AssistantActionKind.Unsupported,
            };
        }

        if (!request.Confirmed)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.NotConfirmed,
                Message = "Review the draft and confirm before I create anything.",
                Kind = request.Draft?.Kind ?? AssistantActionKind.Unsupported,
            };
        }

        if (request.Draft?.Kind != AssistantActionKind.CreateExpense || request.Draft.Expense is null)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.InvalidDraft,
                Message = "That action draft is no longer valid. Ask me to draft it again.",
                Kind = request.Draft?.Kind ?? AssistantActionKind.Unsupported,
            };
        }

        var expense = request.Draft.Expense;
        var missing = RequiredMissingFields(expense).ToList();
        if (missing.Count > 0 || expense.Amount is null)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.MissingRequiredFields,
                Message = "I still need " + string.Join(", ", missing) + " before creating the expense.",
                Kind = AssistantActionKind.CreateExpense,
            };
        }

        var propertyIsAuthorized = await AuthorizedProperties(scope)
            .AnyAsync(property => property.Id == expense.PropertyId, ct);
        if (!propertyIsAuthorized)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.InvalidDraft,
                Message = "That expense draft no longer targets a property you can manage. Draft it again.",
                Kind = AssistantActionKind.CreateExpense,
            };
        }

        var created = await _expenses.CreateAsync(
            scope,
            new CreateExpenseRequest
            {
                PropertyId = expense.PropertyId,
                Category = expense.Category,
                Status = expense.Status,
                Description = expense.Description.Trim(),
                Amount = expense.Amount.Value,
                IncurredAt = expense.IncurredAt,
                PaidAt = expense.PaidAt,
                Notes = BuildNotes(expense.Notes),
            },
            idempotencyKey,
            ct);

        if (created is null)
        {
            return new AssistantActionExecuteResponse
            {
                Status = AssistantActionStatus.InvalidDraft,
                Message = "I could not create the expense because one of the linked records was not found in this portfolio.",
                Kind = AssistantActionKind.CreateExpense,
            };
        }

        return new AssistantActionExecuteResponse
        {
            Status = AssistantActionStatus.Created,
            Kind = AssistantActionKind.CreateExpense,
            EntityId = created.Id,
            DetailHref = $"/expenses/{created.Id}",
            Expense = created,
            Message = $"Created expense #{created.Id} for {created.Amount:C}.",
        };
    }

    internal static bool LooksLikeExpenseAction(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var lower = command.ToLowerInvariant();
        return ActionVerbRegex().IsMatch(lower) &&
               (lower.Contains("expense") ||
                lower.Contains("receipt") ||
                lower.Contains("bill") ||
                lower.Contains("invoice") ||
                lower.Contains("paid "));
    }

    private async Task<AssistantActionDraft> BuildExpenseDraftAsync(
        WorkspaceReadScope scope,
        string command,
        CancellationToken ct)
    {
        var today = _timeProvider.UtcNow().Date;
        var amount = ExtractAmount(command);
        var category = InferCategory(command);
        var status = InferStatus(command);
        var propertyHint = ExtractPropertyHint(command);
        var property = propertyHint is null
            ? null
            : await ResolvePropertyAsync(scope, propertyHint, ct);
        var description = ExtractDescription(command, amount, propertyHint);

        var expense = new AssistantExpenseDraft
        {
            PropertyId = property?.Id,
            PropertyName = property?.Name,
            Amount = amount,
            Category = category,
            Status = status,
            Description = description,
            IncurredAt = today,
            PaidAt = status == ExpenseStatus.Paid ? today : null,
            Notes = propertyHint is not null && property is null
                ? $"Assistant could not match property hint \"{propertyHint}\"."
                : "Created from assistant action draft.",
        };

        return new AssistantActionDraft
        {
            Kind = AssistantActionKind.CreateExpense,
            Risk = AssistantActionRisk.Medium,
            Summary = BuildExpenseSummary(expense),
            Expense = expense,
        };
    }

    private async Task<PropertyMatch?> ResolvePropertyAsync(
        WorkspaceReadScope scope,
        string hint,
        CancellationToken ct)
    {
        var normalized = StripUnitSuffix(hint).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return null;

        var properties = AuthorizedProperties(scope);
        var exactQuery = properties
            .Where(p => p.Name.ToLower() == normalized || p.AddressLine1.ToLower() == normalized)
            .OrderBy(p => p.Id);
        var exact = await ResolveUniqueAsync(exactQuery, ct);

        if (exact is not null) return exact;

        var partialQuery = properties
            .Where(p => p.Name.ToLower().Contains(normalized) || p.AddressLine1.ToLower().Contains(normalized))
            .OrderBy(p => p.Id);
        return await ResolveUniqueAsync(partialQuery, ct);
    }

    private async Task<PropertyMatch?> ResolveUniqueAsync(
        IQueryable<RentalCommand.Core.Entities.Property> query,
        CancellationToken ct)
    {
        var match = await query
            .Select(property => new
            {
                property.Id,
                property.Name,
                HasOtherMatch = query.Any(other => other.Id != property.Id),
            })
            .FirstOrDefaultAsync(ct);
        return match is not null && !match.HasOtherMatch
            ? new PropertyMatch(match.Id, match.Name)
            : null;
    }

    private IQueryable<RentalCommand.Core.Entities.Property> AuthorizedProperties(WorkspaceReadScope scope) =>
        _db.Properties
            .AsNoTracking()
            .Where(property => property.DeletedAt == null)
            .WhereAuthorized(
                _db,
                scope,
                CapabilityKeys.MoneyExpensesManage,
                _timeProvider.GetUtcNow().UtcDateTime);

    private static IEnumerable<string> RequiredMissingFields(AssistantExpenseDraft? expense)
    {
        if (expense is null)
        {
            yield return "expense details";
            yield break;
        }

        if (expense.Amount is null or <= 0m) yield return "amount";
        if (expense.PropertyId is null) yield return "property";
        if (string.IsNullOrWhiteSpace(expense.Description)) yield return "description";
    }

    private static decimal? ExtractAmount(string command)
    {
        var match = AmountRegex().Match(command);
        if (!match.Success) return null;

        var value = match.Groups["amount"].Value.Replace(",", string.Empty);
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;
    }

    private static ScheduleECategory InferCategory(string command)
    {
        var lower = command.ToLowerInvariant();
        if (lower.Contains("plumb") || lower.Contains("repair") || lower.Contains("maintenance") ||
            lower.Contains("hvac") || lower.Contains("electrician"))
        {
            return ScheduleECategory.Repairs;
        }

        if (lower.Contains("clean") || lower.Contains("lawn") || lower.Contains("landscap"))
            return ScheduleECategory.CleaningMaintenance;
        if (lower.Contains("utility") || lower.Contains("water") || lower.Contains("gas") || lower.Contains("electric"))
            return ScheduleECategory.Utilities;
        if (lower.Contains("insurance"))
            return ScheduleECategory.Insurance;
        if (lower.Contains("tax"))
            return ScheduleECategory.Taxes;
        if (lower.Contains("supply") || lower.Contains("supplies"))
            return ScheduleECategory.Supplies;

        return ScheduleECategoryMap.FromAccountName(command);
    }

    private static ExpenseStatus InferStatus(string command)
    {
        var lower = command.ToLowerInvariant();
        if (lower.Contains("unpaid") || lower.Contains("bill") || lower.Contains("invoice") || lower.Contains("due "))
            return ExpenseStatus.Pending;
        return ExpenseStatus.Paid;
    }

    private static string? ExtractPropertyHint(string command)
    {
        var match = PropertyHintRegex().Match(command);
        if (!match.Success) return null;

        var hint = match.Groups["property"].Value.Trim();
        hint = TrailingDateWordsRegex().Replace(hint, string.Empty).Trim();
        return hint.Length == 0 ? null : hint;
    }

    private static string ExtractDescription(string command, decimal? amount, string? propertyHint)
    {
        var description = command;
        description = ActionVerbRegex().Replace(description, string.Empty);
        description = ExpenseWordsRegex().Replace(description, " ");
        if (!string.IsNullOrWhiteSpace(propertyHint))
            description = description.Replace(propertyHint, " ", StringComparison.OrdinalIgnoreCase);
        if (amount is not null)
            description = description.Replace(amount.Value.ToString(CultureInfo.InvariantCulture), " ");
        description = AmountRegex().Replace(description, " ");
        description = FillerWordsRegex().Replace(description, " ");
        description = WhitespaceRegex().Replace(description, " ").Trim().Trim('.', ',', ';', ':').Trim();

        return description.Length == 0 ? "Assistant-created expense" : description;
    }

    private static string StripUnitSuffix(string hint) => UnitSuffixRegex().Replace(hint, string.Empty);

    private static string BuildDraftMessage(
        AssistantActionDraft draft,
        IReadOnlyList<string> missing,
        bool writeModeEnabled)
    {
        if (missing.Count > 0)
        {
            return "I can draft that expense, but I still need " + string.Join(", ", missing) + ".";
        }

        return writeModeEnabled
            ? $"Review this before I create it: {draft.Summary}"
            : $"I drafted this, but action mode is off: {draft.Summary}";
    }

    private static string BuildExpenseSummary(AssistantExpenseDraft expense)
    {
        var amount = expense.Amount?.ToString("C", CultureInfo.CurrentCulture) ?? "an unknown amount";
        var property = string.IsNullOrWhiteSpace(expense.PropertyName) ? "unassigned property" : expense.PropertyName;
        return $"{expense.Status} {amount} {expense.Category} expense for {property}: {expense.Description}";
    }

    private static string? BuildNotes(string? existing)
    {
        const string marker = "Created by AI assistant after explicit user confirmation.";
        return string.IsNullOrWhiteSpace(existing) ? marker : $"{marker} {existing}";
    }

    private static AssistantActionDraftResponse Unsupported(string message) => new()
    {
        Status = AssistantActionStatus.Unsupported,
        Message = message,
        RequiresWriteMode = false,
        CanExecute = false,
    };

    private sealed record PropertyMatch(int Id, string Name);

    [GeneratedRegex(@"\b(log|record|add|create|save|enter|book)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionVerbRegex();

    [GeneratedRegex(@"(?<![\w.])(?:\$|usd\s*)?(?<amount>(?:[0-9]{1,3}(?:,[0-9]{3})*|[0-9]+)(?:\.[0-9]{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"\b(?:for|at|on)\s+(?<property>[^,.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PropertyHintRegex();

    [GeneratedRegex(@"\b(today|yesterday|tomorrow|paid|unpaid|bill|invoice|receipt)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDateWordsRegex();

    [GeneratedRegex(@"\b(expense|receipt|bill|invoice)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExpenseWordsRegex();

    [GeneratedRegex(@"\b(a|an|the|for|at|on|of|to|paid|unpaid|today|yesterday|usd|dollar|dollars)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillerWordsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s+(?:unit|apt|apartment|suite|#)\s+[A-Za-z0-9-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitSuffixRegex();
}
