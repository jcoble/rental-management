using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RentalCommand.Api.Auth;

/// <summary>
/// Destructive cutover guard for the former one-user/one-role Team API. The legacy controller is
/// retained temporarily only so downstream source still compiles; no action body can execute.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LegacyTeamApiUnavailableAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        context.Result = new ObjectResult(new
        {
            error = "The legacy single-role Team API was removed. Use workspace memberships and scoped assignments.",
        })
        {
            StatusCode = StatusCodes.Status410Gone,
        };
    }
}
