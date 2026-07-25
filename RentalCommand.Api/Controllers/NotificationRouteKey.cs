using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

internal static class NotificationRouteKey
{
    internal static bool TryParse(string? raw, out string key)
    {
        key = raw?.Trim() ?? string.Empty;
        return key.Length is > 0 and <= 128;
    }

    internal static WorkspaceReadScope Scope(ActiveAccessContext active)
    {
        return new WorkspaceReadScope(active.PortfolioId, active.UserId, active.SessionId,
            active.AccessContextId, active.AccessRevision);
    }

    internal static BadRequestObjectResult Invalid() =>
        new(new
        {
            error = "Idempotency-Key is required and must be at most 128 characters.",
        });
}
