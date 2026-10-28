using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Restaurant.Application.Common.Interfaces;

namespace Restaurant.Api.Auth;

/// <summary>
/// [RequirePermission(Permissions.Staff.Manage)]: the caller's current membership must hold the
/// permission (full access always does). Enforced on the server whatever the UI shows.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public class RequirePermissionAttribute(params string[] anyOf) : Attribute, IAsyncActionFilter
{
    /// <summary>Holding any one of these is enough.</summary>
    public string[] AnyOf { get; } = anyOf;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.TenantId is null || !AnyOf.Any(tenant.HasPermission))
        {
            context.Result = new ObjectResult(new
            {
                title = tenant.TenantId is null ? "Select a restaurant first." : "You don't have permission to do this.",
                status = StatusCodes.Status403Forbidden,
                permission = string.Join(" or ", AnyOf)
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        await next();
    }
}
